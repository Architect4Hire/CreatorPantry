using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Storage;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Media;

/// <summary>
/// The Media module's composition root: generated images now, the DAM from 12.9.
/// </summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generated-image workspace: the lookup another module needs to validate a reference,
    /// and the seam that requests and runs a generation (12.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gateways are registered here with everything else, because a module owns its own gateways —
    /// but the <see cref="IPrivateObjectStore"/> and the <c>IImageGenerator</c> beneath them belong to the
    /// host. <see cref="StorageServiceCollectionExtensions.AddPrivateObjectStorage"/> is a <c>TryAdd</c>, so
    /// a host that registered a real store keeps it and one that did not gets the store that refuses
    /// everything rather than a container that will not build.
    /// </para>
    /// <para>
    /// The queue <em>driver</em> is not here. <see cref="MediaServiceCollectionExtensions.AddGeneratedImageWorker"/>
    /// adds it, for the Worker host alone, for the reason the brand queues give: a host that cannot claim
    /// cannot accidentally run a paid generation inside a request.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMediaModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGeneratedImageRepository, GeneratedImageRepository>();
        services.AddScoped<IGeneratedImageDataLayer, GeneratedImageDataLayer>();
        services.AddScoped<IGeneratedImageLookupBusiness, GeneratedImageLookupBusiness>();
        services.AddScoped<IGeneratedImageLookupFacade, GeneratedImageLookupFacade>();

        services.AddPrivateObjectStorage();
        services.AddMalwareScanning();

        services.AddScoped<IGeneratedImageProviderGateway, GeneratedImageProviderGateway>();
        services.AddScoped<IGeneratedImageObjectGateway, GeneratedImageObjectGateway>();
        services.AddScoped<IGeneratedImageOperationDetailRepository, GeneratedImageOperationDetailRepository>();
        services.AddScoped<IGeneratedImageGenerationRepository, GeneratedImageGenerationRepository>();
        services.AddScoped<IGeneratedImageGenerationDataLayer, GeneratedImageGenerationDataLayer>();
        services.AddScoped<IGeneratedImageGenerationBusiness, GeneratedImageGenerationBusiness>();
        services.AddScoped<IGeneratedImageGenerationFacade, GeneratedImageGenerationFacade>();
        services.AddScoped<IStagedImageBusiness, StagedImageBusiness>();
        services.AddScoped<IStagedImageFacade, StagedImageFacade>();

        services.AddScoped<IMediaAssetObjectGateway, MediaAssetObjectGateway>();
        services.AddScoped<IMediaRenditionObjectGateway, MediaRenditionObjectGateway>();
        services.AddScoped<IMediaRenditionRepository, MediaRenditionRepository>();
        services.AddScoped<IMediaRenditionDataLayer, MediaRenditionDataLayer>();
        services.AddScoped<IMediaRenditionBusiness, MediaRenditionBusiness>();
        services.AddScoped<IMediaRenditionFacade, MediaRenditionFacade>();

        // Storing a picture writes a rendition request to the outbox (AF.5.5), so the module cannot be
        // composed without the writer. TryAdd, so a host that calls AddOutbox keeps its own registration
        // and one that only wants this module's reads does not have to know the outbox exists.
        services.TryAddScoped<IOutboxWriter, OutboxWriter>();

        // Keyed by message type, which is how the outbox dispatcher finds it. Registered with the module
        // rather than the worker because it is inert without a dispatcher, and only the Worker hosts one.
        services.AddKeyedScoped<IOutboxMessageHandler, MediaRenditionRequestedOutboxHandler>(
            MediaRenditionRequestedEvent.MessageType);
        services.AddScoped<IMediaAssetRepository, MediaAssetRepository>();
        services.AddScoped<IMediaAssetSearchRepository, MediaAssetSearchRepository>();
        services.AddScoped<IMediaAssetDetailRepository, MediaAssetDetailRepository>();
        services.AddScoped<IValidator<MediaAssetSearchViewModel>, MediaAssetSearchViewModelValidator>();
        services.AddScoped<IValidator<MediaAssetUtilizationViewModel>, MediaAssetUtilizationViewModelValidator>();
        services.AddScoped<IMediaAssetDataLayer, MediaAssetDataLayer>();
        services.AddScoped<IMediaAssetBusiness, MediaAssetBusiness>();
        services.AddScoped<IMediaAssetFacade, MediaAssetFacade>();
        services.AddScoped<IMediaAssetLookupBusiness, MediaAssetLookupBusiness>();
        services.AddScoped<IMediaAssetLookupFacade, MediaAssetLookupFacade>();

        // Stored picture analyses (AF.3.4): what a model saw in a picture, kept so it need not look twice.
        services.AddScoped<IMediaPictureAnalysisRepository, MediaPictureAnalysisRepository>();
        services.AddScoped<IMediaPictureAnalysisDataLayer, MediaPictureAnalysisDataLayer>();
        services.AddScoped<IMediaPictureAnalysisBusiness, MediaPictureAnalysisBusiness>();
        services.AddScoped<IMediaPictureAnalysisFacade, MediaPictureAnalysisFacade>();

        return services;
    }

    /// <summary>
    /// Adds the image-generation queue driver. For the Worker host alone.
    /// </summary>
    /// <remarks>
    /// The module must also be registered, and the host must register an <c>IImageGenerator</c> — this adds
    /// the driver, not the provider. A host with no <c>images</c> deployment registers the unconfigured
    /// one, and a claimed operation then settles as <c>provider-not-configured</c>.
    /// </remarks>
    public static IServiceCollection AddGeneratedImageWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<GeneratedImageClaimRepository>();
        services.AddScoped<IGeneratedImageWorker, GeneratedImageWorker>();
        services.AddScoped<IStagedImageRetentionWorker, StagedImageRetentionWorker>();
        services.AddScoped<MediaRenditionClaimRepository>();
        services.AddScoped<IMediaRenditionBackfillWorker, MediaRenditionBackfillWorker>();

        return services;
    }
}
