using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary another module crosses to resolve a DAM asset id (12.9a).
/// </summary>
/// <remarks>
/// <para>
/// What <c>PromptRecord.DamAssetId</c> needed before it could exist: the record is immutable, so an id
/// written once can never be corrected, and this is what resolves one inside the caller's own workspace
/// before the row is written. The composite foreign key remains the authority — a wrong value is
/// unrepresentable, not merely refused — and this turns a storage exception into a field error a creator
/// can act on.
/// </para>
/// <para>
/// Deliberately the same shape as <c>IGeneratedImageLookupFacade</c>: one boolean, no role gate (a
/// resolved workspace context is the authorization for the lowest role), and one answer for an unknown id
/// and a neighbour's so neither discloses the other. A soft-deleted asset answers false, because a prompt
/// should not be pinned to something a creator has thrown away.
/// </para>
/// </remarks>
public interface IMediaAssetLookupFacade
{
    /// <inheritdoc cref="IMediaAssetRepository.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether something in this workspace may link to that asset, optionally pinned to one of its versions
    /// (12.10i).
    /// </summary>
    /// <param name="versionNumber">The version a link would pin, or <c>null</c> for one that follows the current.</param>
    /// <returns>
    /// <see cref="MediaAssetLinkTarget.Linkable"/>, or which of the two things was not there. An unknown
    /// asset, another workspace's and a removed one are all <see cref="MediaAssetLinkTarget.AssetNotFound"/>,
    /// so asking about an id cannot reveal that a neighbour owns it.
    /// </returns>
    Task<MediaAssetLinkTarget> ResolveLinkTargetAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetLookupFacade"/>
internal sealed class MediaAssetLookupFacade(IMediaAssetLookupBusiness business) : IMediaAssetLookupFacade
{
    public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        business.ExistsAsync(mediaAssetId, cancellationToken);

    public Task<MediaAssetLinkTarget> ResolveLinkTargetAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
        business.ResolveLinkTargetAsync(mediaAssetId, versionNumber, cancellationToken);
}
