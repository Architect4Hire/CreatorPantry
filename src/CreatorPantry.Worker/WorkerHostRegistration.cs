using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.ServiceDefaults;
using CreatorPantry.Worker.Caching;
using CreatorPantry.Worker.Storage;
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

        // Required by the AI module, not optional beside it: this host is where attempts actually settle, so
        // without it every StoreProposalAsync/FailAsync fails to resolve its dependencies at startup rather
        // than quietly recording no usage (USAGE-001) or admitting a run against nothing (USAGE-004).
        builder.Services.AddAiUsageModule(builder.Configuration);

        // Registered deliberately, for the AI operation worker: it loads the exact recipe/version an operation
        // names through the recipe module's own facade, never its repositories, and that facade needs the whole
        // seam below it — tenancy for workspace resolution, and every module IRecipeFacade itself calls
        // facade-to-facade.
        builder.Services.AddTenancy();
        builder.Services.AddMeasurementModule();
        builder.Services.AddVocabularyModule();
        builder.Services.AddIngredientModule();
        builder.Services.AddRecipesModule(builder.Configuration);

        // The consumer of the recipe-version-changed event the Recipes handler fans out to. Worker-only: the
        // dispatcher that delivers the event runs here, so nothing in the API reacts to a recipe change.
        builder.Services.AddContentModule();

        // The content module resolves a saved prompt's generated-image pin through the Media module, so a
        // host carrying one carries the other. It is also where 12.7's image generation runs, which is why
        // AddGeneratedImageWorker is added below alongside the brand queues.
        builder.Services.AddMediaModule();
        builder.Services.AddAudit();
        builder.Services.AddIdempotency(builder.Configuration);
        builder.Services.AddAiOperationWorker();

        // Document extraction (11A.10). The store comes first because the brand module's gateway needs one, and
        // the registration order between the two does not matter — AddBrandModule's own AddPrivateObjectStorage
        // is a TryAdd, so the real store registered here is the one that survives.
        builder.AddCreatorPantryObjectStorage();
        builder.Services.AddBrandModule();
        builder.Services.AddBrandSourceExtractionWorker();
        builder.Services.AddBrandSourceEmbeddingWorker();

        // Image generation (12.7). It reads bytes a provider returned, which is the first inbound-content
        // path this host has had — the API registers the development scanner for its uploads and this host
        // never needed one. Without this line every generated image is refused by the fail-closed scanner,
        // on a developer's machine as much as anywhere, which would read as a broken job rather than as a
        // missing scanner. The call is a no-op outside Development, so a deployed host still fails closed.
        builder.Services.AddDevelopmentMalwareScanning(builder.Environment);
        builder.Services.AddGeneratedImageWorker();

        // 11A.19's brand-context assembler. Here rather than in the API because its callers are task handlers,
        // which run in this host, and after AddBrandModule because every read it makes goes through that
        // module's facades. Nothing injects it yet — the handlers take it one at a time in 11A.20 — and
        // registering it now is what has the container prove the dependency graph resolves.
        builder.Services.AddAiBrandContext();

        // The one host that sweeps every workspace's execution metadata looking for attempts the account
        // ledger never received (USAGE-002). Registered here rather than in the API for the same reason the
        // queue is: it is background work, and it belongs where background work runs.
        builder.Services.AddAiUsageReconciliation(builder.Configuration);

        builder.Services.AddOutbox();
        builder.Services.AddHostedService<OutboxDispatcherHostedService>();
        builder.Services.AddHostedService<AiOperationWorkerHostedService>();
        builder.Services.AddHostedService<AiOperationMaintenanceHostedService>();
        builder.Services.AddHostedService<AiQuotaMaintenanceHostedService>();
        builder.Services.AddHostedService<AiUsageReconciliationHostedService>();
        builder.Services.AddHostedService<BrandSourceExtractionWorkerHostedService>();
        builder.Services.AddHostedService<BrandSourceExtractionMaintenanceHostedService>();
        builder.Services.AddHostedService<BrandSourceEmbeddingWorkerHostedService>();
        builder.Services.AddHostedService<BrandSourceEmbeddingMaintenanceHostedService>();
        builder.Services.AddHostedService<GeneratedImageWorkerHostedService>();
        builder.Services.AddHostedService<GeneratedImageMaintenanceHostedService>();
        builder.Services.AddHostedService<StagedImageRetentionHostedService>();

        return builder;
    }
}
