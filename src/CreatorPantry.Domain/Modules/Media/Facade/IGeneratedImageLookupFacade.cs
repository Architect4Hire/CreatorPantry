using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary another module crosses to resolve a generated-image id (12.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what 12.3's note asked for</strong>: <c>PromptRecord.GeneratedImageId</c> is validated
/// inside the resolved workspace before an immutable row is written, so a wrong id is refused rather than
/// stored permanently. The composite foreign key remains the authority — a wrong value is unrepresentable,
/// not merely refused — and this turns a storage exception into a field error a creator can act on.
/// </para>
/// <para>
/// Deliberately the same shape as <c>IAiProposalLookupFacade</c>: one boolean, no role gate (a resolved
/// workspace context is the authorization for the lowest role), and one answer for an unknown id and a
/// neighbour's so that neither discloses the other.
/// </para>
/// </remarks>
public interface IGeneratedImageLookupFacade
{
    /// <inheritdoc cref="IGeneratedImageLookupBusiness.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageLookupBusiness.IsAvailableAsync"/>
    /// <remarks>
    /// For a module about to point new work at an image (AF.1.3), where <see cref="ExistsAsync"/> is not enough:
    /// a declined or expired image still has its row, and offering it as a source would hand a creator a
    /// picture that is no longer there.
    /// </remarks>
    Task<bool> IsAvailableAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageLookupBusiness.ResolvePictureAsync"/>
    /// <remarks>
    /// For a module about to ask for a generated picture's bytes (AF.3.4): metadata first, so one that is
    /// declined, expired, not this workspace's or too large to send is refused before anything is queued.
    /// </remarks>
    Task<MediaPictureTarget?> ResolvePictureAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageLookupBusiness.OpenPictureAsync"/>
    /// <remarks>
    /// The bytes themselves, authorised again at the moment they are read. A picture declined or collected
    /// since <see cref="ResolvePictureAsync"/> answered is not found. The caller disposes what it is handed.
    /// </remarks>
    Task<MediaPictureOpen> OpenPictureAsync(Guid generatedImageId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageLookupFacade"/>
internal sealed class GeneratedImageLookupFacade(IGeneratedImageLookupBusiness business)
    : IGeneratedImageLookupFacade
{
    public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        business.ExistsAsync(generatedImageId, cancellationToken);

    public Task<bool> IsAvailableAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        business.IsAvailableAsync(generatedImageId, cancellationToken);

    public Task<MediaPictureTarget?> ResolvePictureAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        business.ResolvePictureAsync(generatedImageId, cancellationToken);

    public Task<MediaPictureOpen> OpenPictureAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        business.OpenPictureAsync(generatedImageId, cancellationToken);
}
