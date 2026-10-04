using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Brand;

public static class BrandServiceCollectionExtensions
{
    /// <summary>
    /// The brand module's composition root. Requires <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>
    /// , tenancy, application time, audit and idempotency to be registered.
    /// </summary>
    public static IServiceCollection AddBrandModule(this IServiceCollection services)
    {
        services.AddContentChannelCatalog();
        services.AddScoped<IBrandProfileRepository, BrandProfileRepository>();
        services.AddScoped<IBrandProfileDataLayer, BrandProfileDataLayer>();
        services.AddScoped<IBrandProfileBusiness, BrandProfileBusiness>();
        services.AddScoped<IBrandProfileFacade, BrandProfileFacade>();
        services.AddScoped<IValidator<CreateBrandProfileViewModel>, CreateBrandProfileViewModelValidator>();
        services.AddScoped<IValidator<UpdateBrandProfileViewModel>, UpdateBrandProfileViewModelValidator>();

        services.AddScoped<IBrandSetupSessionRepository, BrandSetupSessionRepository>();
        services.AddScoped<IBrandSetupSessionDataLayer, BrandSetupSessionDataLayer>();
        services.AddScoped<IBrandSetupSessionBusiness, BrandSetupSessionBusiness>();
        services.AddScoped<IBrandSetupSessionFacade, BrandSetupSessionFacade>();
        services.AddScoped<IValidator<SaveBrandSetupSessionViewModel>, SaveBrandSetupSessionViewModelValidator>();

        // The store that refuses everything unless the host registered a real one, so this module resolves in
        // a host with no storage and fails loudly only if an object is actually reached for.
        services.AddPrivateObjectStorage();
        services.AddScoped<IBrandSourceObjectGateway, BrandSourceObjectGateway>();
        services.AddScoped<IBrandSourceEmbeddingGateway, BrandSourceEmbeddingGateway>();

        // Likewise the scanner that clears nothing: without a real one, uploads are refused rather than
        // accepted unscanned.
        services.AddMalwareScanning();
        services.AddScoped<IBrandSourceDocumentRepository, BrandSourceDocumentRepository>();
        services.AddScoped<IBrandSourceDocumentDataLayer, BrandSourceDocumentDataLayer>();
        services.AddScoped<IBrandSourceDocumentBusiness, BrandSourceDocumentBusiness>();
        services.AddScoped<IBrandSourceDocumentFacade, BrandSourceDocumentFacade>();
        services.AddScoped<IValidator<UploadBrandSourceDocumentViewModel>, UploadBrandSourceDocumentViewModelValidator>();
        services.AddScoped<IValidator<PasteBrandSourceTextViewModel>, PasteBrandSourceTextViewModelValidator>();
        services.AddScoped<IValidator<ReplaceBrandSourceDocumentViewModel>, ReplaceBrandSourceDocumentViewModelValidator>();
        services.AddScoped<IValidator<BrandSourceDocumentLifecycleViewModel>, BrandSourceDocumentLifecycleViewModelValidator>();

        // The extraction seam. The repository is registered here rather than only in the Worker because the
        // upload and the replacement stage a queued operation through it, in the same save as the version —
        // which is the whole reason a committed version always has work queued for it.
        services.AddScoped<IValidator<CreateBrandStyleGuideViewModel>, CreateBrandStyleGuideViewModelValidator>();
        services.AddScoped<
            IValidator<BrandStyleGuideVersionComparisonViewModel>,
            BrandStyleGuideVersionComparisonViewModelValidator>();
        services.AddScoped<
            IValidator<ApproveBrandStyleGuideVersionViewModel>,
            ApproveBrandStyleGuideVersionViewModelValidator>();
        services.AddScoped<
            IValidator<SaveBrandStyleGuideVersionViewModel>,
            SaveBrandStyleGuideVersionViewModelValidator>();
        services.AddScoped<
            IValidator<ActivateBrandStyleGuideVersionViewModel>,
            ActivateBrandStyleGuideVersionViewModelValidator>();
        services.AddScoped<IBrandStyleGuideRepository, BrandStyleGuideRepository>();
        services.AddScoped<IBrandStyleGuideDataLayer, BrandStyleGuideDataLayer>();
        services.AddScoped<IBrandStyleGuideBusiness, BrandStyleGuideBusiness>();
        services.AddScoped<IBrandStyleGuideFacade, BrandStyleGuideFacade>();

        // The editor's autosave target (11A.15 / 11A.23b): one creator's unsaved edit of one guide.
        services.AddScoped<
            IValidator<SaveBrandStyleGuideEditSessionViewModel>,
            SaveBrandStyleGuideEditSessionViewModelValidator>();
        services.AddScoped<IBrandStyleGuideEditSessionRepository, BrandStyleGuideEditSessionRepository>();
        services.AddScoped<IBrandStyleGuideEditSessionDataLayer, BrandStyleGuideEditSessionDataLayer>();
        services.AddScoped<IBrandStyleGuideEditSessionBusiness, BrandStyleGuideEditSessionBusiness>();
        services.AddScoped<IBrandStyleGuideEditSessionFacade, BrandStyleGuideEditSessionFacade>();
        // The grounding read over what the embedding worker produced. Registered with the module rather than
        // with the worker, because its caller is an AI task running in the Worker host and the API host alike.
        services.AddScoped<IBrandSourcePassageRepository, BrandSourcePassageRepository>();
        services.AddScoped<IBrandSourcePassageDataLayer, BrandSourcePassageDataLayer>();
        services.AddScoped<IBrandSourcePassageBusiness, BrandSourcePassageBusiness>();
        services.AddScoped<IBrandSourcePassageFacade, BrandSourcePassageFacade>();
        services.AddScoped<IBrandSourceEmbeddingRepository, BrandSourceEmbeddingRepository>();
        services.AddScoped<IBrandSourceEmbeddingDataLayer, BrandSourceEmbeddingDataLayer>();
        services.AddScoped<IBrandSourceEmbeddingBusiness, BrandSourceEmbeddingBusiness>();
        services.AddScoped<IBrandSourceEmbeddingFacade, BrandSourceEmbeddingFacade>();
        services.AddScoped<IBrandSourceExtractionRepository, BrandSourceExtractionRepository>();
        services.AddScoped<IBrandSourceExtractionDataLayer, BrandSourceExtractionDataLayer>();
        services.AddScoped<IBrandSourceExtractionBusiness, BrandSourceExtractionBusiness>();
        services.AddScoped<IBrandSourceExtractionFacade, BrandSourceExtractionFacade>();

        // The creator-facing half of the same seam: reading a version's text and correcting it. A separate
        // facade from the worker's because that one checks no role by design — see its remarks.
        services.AddScoped<IBrandSourceExtractionReviewBusiness, BrandSourceExtractionReviewBusiness>();
        services.AddScoped<IBrandSourceExtractionReviewFacade, BrandSourceExtractionReviewFacade>();
        services.AddScoped<IValidator<CorrectBrandSourceExtractionViewModel>, CorrectBrandSourceExtractionViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Adds the extraction queue driver. For the Worker host alone, like <c>AddAiOperationWorker</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddBrandModule"/> because the claim is cross-workspace and the API has no
    /// business holding it: a host that cannot claim cannot accidentally run a creator's extraction inside a
    /// request. The module itself must also be registered — this adds the driver, not the seam below it.
    /// </remarks>
    public static IServiceCollection AddBrandSourceExtractionWorker(this IServiceCollection services)
    {
        services.AddScoped<BrandSourceExtractionClaimRepository>();
        services.AddScoped<IBrandSourceExtractionWorker, BrandSourceExtractionWorker>();

        return services;
    }

    /// <summary>
    /// Adds the embedding queue driver. For the Worker host alone, for the reason
    /// <see cref="AddBrandSourceExtractionWorker"/> gives: a host that cannot claim cannot accidentally embed a
    /// creator's document inside a request. The module must also be registered, and the host must register an
    /// <c>IEmbeddingGenerator</c> — this adds the driver, not the provider.
    /// </summary>
    public static IServiceCollection AddBrandSourceEmbeddingWorker(this IServiceCollection services)
    {
        services.AddScoped<BrandSourceEmbeddingClaimRepository>();
        services.AddScoped<IBrandSourceEmbeddingWorker, BrandSourceEmbeddingWorker>();

        return services;
    }
}
