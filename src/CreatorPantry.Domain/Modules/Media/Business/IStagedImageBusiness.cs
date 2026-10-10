using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>
/// The staged-image library: reading one, declining one, and the retention the sweep performs (IMG-005,
/// IMG-006).
/// </summary>
/// <remarks>
/// Thin, and honestly so. Every rule this domain has is either in the schema (one row per variant, one row
/// per object), in <see cref="Managers.MediaPolicy"/> (how long an image lives, how old an orphan must be),
/// or in the data layer's ordering of a row move against a byte delete. There is no calculation left for a
/// business layer to own — but the seam exists because backend.md says a facade does not reach a data
/// layer, and a layer that is a pass-through today is where tomorrow's rule lands.
/// </remarks>
public interface IStagedImageBusiness
{
    Task<StagedImageOpen> OpenAsync(
        Guid generatedImageId, MediaRenditionPurpose? rendition, CancellationToken cancellationToken);

    Task<StagedImageRejectOutcome> RejectAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Runs one workspace's share of the retention sweep: expire, purge, then reconcile.</summary>
    /// <remarks>
    /// In that order, and the order is the point. Expiring first moves rows into the purge queue the same
    /// pass can drain; reconciling last means the rows it checks against are as settled as this pass can
    /// make them, so an object whose row was just written is never judged an orphan on a stale read.
    /// Renditions are purged between the first two: those of an image that was declined, expired or kept,
    /// and those of a library asset that was deleted, which is the one thing this sweep removes for the DAM.
    /// </remarks>
    Task<StagedImageRetentionSummary> RunRetentionAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IStagedImageBusiness"/>
internal sealed class StagedImageBusiness(
    IGeneratedImageDataLayer images, IMediaRenditionDataLayer renditions) : IStagedImageBusiness
{
    public Task<StagedImageOpen> OpenAsync(
        Guid generatedImageId, MediaRenditionPurpose? rendition, CancellationToken cancellationToken) =>
        generatedImageId == Guid.Empty
            ? Task.FromResult(new StagedImageOpen(StagedImageOpenOutcome.NotFound))
            : images.OpenAsync(generatedImageId, rendition, cancellationToken);

    public Task<StagedImageRejectOutcome> RejectAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        generatedImageId == Guid.Empty
            ? Task.FromResult(StagedImageRejectOutcome.NotFound)
            : images.RejectAsync(generatedImageId, cancellationToken);

    public async Task<StagedImageRetentionSummary> RunRetentionAsync(CancellationToken cancellationToken)
    {
        var expired = await images.ExpireAsync(cancellationToken);

        // After expiry, so an image that expired a moment ago loses its renditions in this same pass.
        var renditionsPurged = await renditions.PurgeAsync(cancellationToken);
        var purged = await images.PurgeAsync(cancellationToken);
        var orphans = await images.ReconcileOrphansAsync(cancellationToken);

        return new StagedImageRetentionSummary(expired, purged, orphans, renditionsPurged);
    }
}
