using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Development;
using CreatorPantry.ApiService.Http;
using CreatorPantry.ApiService.Tenancy;
using CreatorPantry.Domain.Audit;
using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Gateways.AccountMessages;
using CreatorPantry.Domain.Idempotency;
using CreatorPantry.Domain.Outbox;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Domain.Time;
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

builder.Services.AddApplicationTime();
builder.Services.AddAuthDomain();
builder.Services.AddTenancy();
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
        options.InvalidModelStateResponseFactory = ProblemResults.MalformedRequest);

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
