using System.Text.Json.Serialization;
using CreatorPantry.AiProvider;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Caching;
using CreatorPantry.ApiService.Development;
using CreatorPantry.ApiService.Http;
using CreatorPantry.ApiService.Tenancy;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Registered directly (not via AddSqlServerDbContext) because that helper pools contexts, and a pooled
// context cannot take a scoped constructor dependency (IWorkspaceContext, for the per-request query filter):
// the pool's activator resolves against the root container, so a scoped service fails to resolve there.
// EnrichSqlServerDbContext still layers on Aspire's health check, retry, and tracing integration.
builder.Services.AddDbContext<CreatorPantryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString(CreatorPantryDbContext.ConnectionName)));
builder.EnrichSqlServerDbContext<CreatorPantryDbContext>();

// Redis where the AppHost supplies it, in-process otherwise. Reference reads are the only consumer today.
builder.AddCreatorPantryCache();

// IChatClient and IEmbeddingGenerator over the AppHost's model deployments, or the unconfigured clients in
// development where there are none. Nothing calls a model yet; this establishes the abstractions.
builder.AddCreatorPantryAi();

builder.Services.AddApplicationTime();
builder.Services.AddAuthDomain();
builder.Services.AddTenancy();
builder.Services.AddMeasurementModule();
builder.Services.AddVocabularyModule();
builder.Services.AddIngredientModule();
builder.Services.AddRecipesModule(builder.Configuration);
builder.Services.AddBrandModule();
// The provider-specific failure classifier goes in before AddAiModule, whose TryAdd fallback is deliberately
// weaker: it cannot read a provider SDK's status code, so it cannot tell a rate limit or a safety block from a
// generic transient fault.
builder.Services.AddSingleton<IAiFailureClassifier, AzureInferenceFailureClassifier>();
builder.AddAiResilience(static exception => exception is AiTransientFailureException);
builder.Services.AddAiModule(builder.Configuration, AiResilience.PipelineKey);

// Required by the AI module, not optional beside it: every path that settles a provider attempt stages a
// per-account usage entry and its allowance settlement through these facades in the same transaction
// (USAGE-001, USAGE-004).
builder.Services.AddAiUsageModule(builder.Configuration);

// The ops-only administration seam (USAGE-009). Registered here and not in the worker, which has no ops route.
builder.Services.AddAiUsageAdministration();

// The request seam, which the worker does not register: it needs the recipe module, already added above.
builder.Services.AddAiProposalSeam();

// The readiness evaluation, which needs the proposal seam above plus the ingredient module for the four rules
// that read another module. The worker has no readiness route and deliberately does not register it.
builder.Services.AddRecipeReadinessSeam();

// The recipe exports (RCPUB-002). They read the accepted SEO revision through the content module, which the API
// did not carry before: its staleness consumer is the worker's concern, but its read facades are needed here.
builder.Services.AddContentModule();
builder.Services.AddRecipeExportSeam();

// AIREC-001's own request seam. No recipe-module prerequisite -- a concept request names no recipe.
builder.Services.AddAiConceptRequestSeam();

// AIREC-002's, likewise: a first-draft request names no recipe, and accepting one is a separate step.
builder.Services.AddAiFirstDraftRequestSeam();

// AIREC-003's. Recipe-bound, so it carries the recipe-module prerequisite the proposal seam does.
builder.Services.AddAiRevisionRequestSeam();
builder.Services.AddAiSubstitutionRequestSeam();

// AIREC-005's. Recipe-bound like the two above, plus the same yield pre-check dependency AIREC-004 exercises.
builder.Services.AddAiAdaptationRequestSeam();

// AIREC-006's. Recipe-bound like the others, with no capability-specific field of its own -- see
// AiTaskCatalog.RequiresTaskInputs for why it still needs its own route.
builder.Services.AddAiReviewRequestSeam();
builder.Services.AddAiEditorialPackageRequestSeam();
builder.Services.AddAiSeoPackageRequestSeam();

// AIREC-008's. No recipe-module prerequisite -- it explains an existing proposal, not a recipe.
builder.Services.AddAiProposalExplanationRequestSeam();
builder.Services.AddAudit();
builder.Services.AddOutbox();
builder.Services.AddIdempotency(builder.Configuration);
builder.Services.AddInternalTokenAuthentication(builder.Configuration);

// The ops API key scheme (baseline B-14), registered beside the gateway's token rather than as the default:
// it is evaluated only where the Ops policy names it, which is what confines a machine key to /api/v1/ops/*.
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, OpsApiKeyAuthenticationHandler>(OpsApiKeyPolicy.Scheme, _ => { });

builder.Services.AddCreatorPantryAuthorization();

// Account messages carry reset tokens. Only local development may use the in-memory sink; anywhere else a
// real, durably queued sender is required before the API can start.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDevelopmentAccountMessageSink();
}
else
{
    throw new InvalidOperationException(
        "No account message delivery is configured. Register an IAccountMessageSender provider adapter.");
}

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    // Problems written by the framework (exceptions, status-code pages) get a stable code; problems built by
    // controllers already carry their own.
    if (!context.ProblemDetails.Extensions.ContainsKey(ProblemResults.CodeExtension))
    {
        context.ProblemDetails.Extensions[ProblemResults.CodeExtension] =
            ProblemResults.CodeForStatus(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode);
    }
});

builder.Services.AddCreatorPantryApiVersioning();

builder.Services.AddControllers(options =>
        // Field rules live in FluentValidation validators run by the facades, not in implicit [Required].
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .ConfigureApiBehaviorOptions(options =>
        // With implicit [Required] suppressed, model state only fails for unreadable bodies.
        options.InvalidModelStateResponseFactory = ProblemResults.MalformedRequest)
    .AddJsonOptions(options =>
        // Enums as their declared names, not their numbers. Numbers make the contract asymmetric — the
        // dimension filter already accepts "Temperature" while the response would answer 3 — and they publish
        // as a bare "integer" with no names, so a client has to keep a private mapping the document never
        // gave it. Names also make inserting an enum member a visible change rather than a silent renumbering.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The same converter again, for the minimal-API endpoints (health, development tooling), which serialize
// through this options instance rather than MVC's. The OpenAPI document reads neither — a schema transformer
// in OpenApiDocumentation states the enum shape explicitly so it cannot contradict what is written here.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();

// Empty error responses (unknown route, unsupported API version, wrong method) become ProblemDetails.
app.UseStatusCodePages();

// 4 MB default body limit; upload routes raise it with [RequestSizeLimit]. Browser security headers are
// applied by the gateway, the only browser-facing path to these routes.
app.UseRequestBodyLimit();

app.UseAuthentication();
app.UseWorkspaceResolution();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapDevelopmentEndpoints();
app.MapApiDocumentation();
app.MapAuthenticatedControllers();

app.Run();
