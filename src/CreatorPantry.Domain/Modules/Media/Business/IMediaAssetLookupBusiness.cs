using CreatorPantry.Domain.Modules.Media.Data;

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
}
