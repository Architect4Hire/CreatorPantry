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
}
