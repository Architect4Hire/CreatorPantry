using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.ServiceDefaults;
using CreatorPantry.Worker.Caching;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Worker;

public static class WorkerHostRegistration
{
    /// <summary>
    /// Registers everything this host owns: its stores, its model abstractions, the domain modules an AI
    /// operation reaches through, and the hosted services that drive the queues.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method rather than statements in <c>Program</c> so that <c>WorkerHostCompositionTests</c> can build
    /// this container without starting the process. That is not tidiness: this host has no endpoint, no health
    /// check and nothing that fails a request, so a container it cannot build makes it exit quietly at startup
    /// and every queued AI operation simply stays <c>Requested</c> forever. The failure looks, from the
    /// browser, exactly like a slow model.
    /// </para>
    /// <para>
    /// <c>AddServiceDefaults</c> stays in <c>Program</c>. It configures telemetry exporters and service
    /// discovery from the environment Aspire injects, which is the one part of composition a test has no
    /// business reproducing.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddWorkerServices(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddApplicationTime();

        // The modules registered below reach their data through facades that take IApplicationCache, so this
        // is a prerequisite for the container validating at all, not an optimisation. Same Redis as the API.
        builder.AddCreatorPantryCache();

        // Not AddSqlServerDbContext: see ApiService/Program.cs for why (a pooled context cannot take the
        // scoped IWorkspaceContext dependency CreatorPantryDbContext's constructor accepts).
        builder.Services.AddDbContext<CreatorPantryDbContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString(CreatorPantryDbContext.ConnectionName)));
        builder.EnrichSqlServerDbContext<CreatorPantryDbContext>();

        // The worker gets the same model abstractions as the API, because generation, embedding and media jobs
        // run here. See AiProviderRegistration for the unconfigured-development fallback.
        builder.AddCreatorPantryAi();

        // The provider-specific classifier first: AddAiModule's fallback cannot read a provider SDK's status
        // code, so it cannot tell a rate limit or a safety block from a generic transient fault. AddAiModule
        // also validates and loads every prompt template, so a malformed one stops this host rather than a job.
        builder.Services.AddSingleton<IAiFailureClassifier, AzureInferenceFailureClassifier>();
        builder.AddAiResilience(static exception => exception is AiTransientFailureException);
        builder.Services.AddAiModule(builder.Configuration, AiResilience.PipelineKey);

        // Registered deliberately, for the AI operation worker: it loads the exact recipe/version an operation
        // names through the recipe module's own facade, never its repositories, and that facade needs the whole
        // seam below it — tenancy for workspace resolution, and every module IRecipeFacade itself calls
        // facade-to-facade.
        builder.Services.AddTenancy();
        builder.Services.AddMeasurementModule();
        builder.Services.AddVocabularyModule();
        builder.Services.AddIngredientModule();
        builder.Services.AddRecipesModule();
        builder.Services.AddAudit();
        builder.Services.AddIdempotency(builder.Configuration);
        builder.Services.AddAiOperationWorker();

        builder.Services.AddOutbox();
        builder.Services.AddHostedService<OutboxDispatcherHostedService>();
        builder.Services.AddHostedService<AiOperationWorkerHostedService>();
        builder.Services.AddHostedService<AiOperationMaintenanceHostedService>();

        return builder;
    }
}
