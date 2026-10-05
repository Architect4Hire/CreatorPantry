using CreatorPantry.Domain.Modules.Media.Business;

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
}

/// <inheritdoc cref="IGeneratedImageLookupFacade"/>
internal sealed class GeneratedImageLookupFacade(IGeneratedImageLookupBusiness business)
    : IGeneratedImageLookupFacade
{
    public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        business.ExistsAsync(generatedImageId, cancellationToken);
}
