using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>Resolves a DAM asset id another module is about to store, inside the resolved workspace.</summary>
/// <remarks>
/// Its own facade and business rather than a method on the creation seam, which is the split
/// <c>IGeneratedImageLookupBusiness</c> already makes: the cross-module surface stays one boolean, and a
/// caller resolving an id does not gain access to everything an asset can be told to do. It reads through
/// the one data layer, because there is nothing to compose.
/// </remarks>
public interface IMediaAssetLookupBusiness
{
    /// <inheritdoc cref="IMediaAssetRepository.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IMediaAssetLookupFacade.ResolveLinkTargetAsync"/>
    Task<MediaAssetLinkTarget> ResolveLinkTargetAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// What a creator has said one of this workspace's live assets shows, for the version asked for or the
    /// current one — or null when the asset is not there, is deleted, or has no such version.
    /// </summary>
    /// <remarks>
    /// The alt text is the asset's, not a version's: a creator describes the picture, and a new version of it
    /// is the same picture improved. The version number is returned so a caller can record which bytes the
    /// description stood beside.
    /// </remarks>
    Task<MediaAssetDescription?> DescribeAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetDataLayer.ResolvePictureAsync"/>
    Task<MediaPictureTarget?> ResolvePictureAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetDataLayer.OpenPictureAsync"/>
    Task<MediaPictureOpen> OpenPictureAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetLookupBusiness"/>
internal sealed class MediaAssetLookupBusiness(IMediaAssetDataLayer assets) : IMediaAssetLookupBusiness
{
    public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        mediaAssetId == Guid.Empty

            // Nothing to ask the database: an empty id was never issued, so it is no asset of this
            // workspace or of any other.
            ? Task.FromResult(false)
            : assets.ExistsAsync(mediaAssetId, cancellationToken);

    public async Task<MediaAssetLinkTarget> ResolveLinkTargetAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken)
    {
        // The asset first, and only then the version: "no such version" is said only about an asset this
        // workspace holds, so it can never be used to learn that a neighbour's asset exists.
        if (!await ExistsAsync(mediaAssetId, cancellationToken))
        {
            return MediaAssetLinkTarget.AssetNotFound;
        }

        if (versionNumber is not { } number)
        {
            return MediaAssetLinkTarget.Linkable;
        }

        // Numbers start at one; anything below it names no version and is not worth a query.
        return number >= 1 && await assets.VersionExistsAsync(mediaAssetId, number, cancellationToken)
            ? MediaAssetLinkTarget.Linkable
            : MediaAssetLinkTarget.VersionNotFound;
    }

    public async Task<MediaAssetDescription?> DescribeAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken)
    {
        if (mediaAssetId == Guid.Empty)
        {
            return null;
        }

        var current = await assets.DescribeAsync(mediaAssetId, cancellationToken);

        if (current is null || versionNumber is not { } pinned)
        {
            return current;
        }

        // A pin names one version; a pin the asset does not have describes nothing.
        return pinned >= 1 && await assets.VersionExistsAsync(mediaAssetId, pinned, cancellationToken)
            ? current with { VersionNumber = pinned }
            : null;
    }

    public Task<MediaPictureTarget?> ResolvePictureAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>

        // An empty id was never issued and a version below one names nothing: neither is worth a query.
        mediaAssetId == Guid.Empty || versionNumber is < 1
            ? Task.FromResult<MediaPictureTarget?>(null)
            : assets.ResolvePictureAsync(mediaAssetId, versionNumber, cancellationToken);

    public Task<MediaPictureOpen> OpenPictureAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
        mediaAssetId == Guid.Empty || versionNumber < 1
            ? Task.FromResult(new MediaPictureOpen(MediaPictureOpenOutcome.NotFound))
            : assets.OpenPictureAsync(mediaAssetId, versionNumber, cancellationToken);
}
