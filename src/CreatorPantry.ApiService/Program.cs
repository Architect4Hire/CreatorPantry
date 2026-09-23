using System.Text.Json.Serialization;
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
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.ServiceDefaults;
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

builder.Services.AddApplicationTime();
builder.Services.AddAuthDomain();
builder.Services.AddTenancy();
builder.Services.AddMeasurementModule();
builder.Services.AddVocabularyModule();
builder.Services.AddIngredientModule();
builder.Services.AddAudit();
builder.Services.AddOutbox();
builder.Services.AddIdempotency(builder.Configuration);
builder.Services.AddInternalTokenAuthentication(builder.Configuration);
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
