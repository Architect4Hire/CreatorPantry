using CreatorPantry.Domain.Modules.Content.Business;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The one question another module may ask about a board card without reading it: is it this workspace's?
/// </summary>
/// <remarks>
/// <para>
/// A facade of its own, for the reason <see cref="ICreativeContextLookupFacade"/> is one: the publishing
/// module confirms the card a confirmation names before it writes a ledger row, and it should not have to
/// resolve the lineage, the move count and the package that <see cref="IContentCardFacade.GetDetailAsync"/>
/// assembles to answer a yes or no.
/// </para>
/// <para>
/// <strong>No role gate.</strong> A resolved workspace context is the authorization for the lowest role, and
/// the answer is a yes or no about an id the caller already holds. An id nobody has and another workspace's
/// are one answer, by the workspace query filter (tenancy.md).
/// </para>
/// </remarks>
public interface IContentCardLookupFacade
{
    /// <summary>True when the resolved workspace has a card with this id, in whatever column.</summary>
    Task<bool> ExistsAsync(Guid cardId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IContentCardLookupFacade"/>
internal sealed class ContentCardLookupFacade(IContentCardLookupBusiness business) : IContentCardLookupFacade
{
    public Task<bool> ExistsAsync(Guid cardId, CancellationToken cancellationToken) =>
        business.ExistsAsync(cardId, cancellationToken);
}
