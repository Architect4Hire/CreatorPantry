using CreatorPantry.Domain.Modules.Content.Data;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>Whether an id names a card on the resolved workspace's board.</summary>
public interface IContentCardLookupBusiness
{
    /// <inheritdoc cref="Facade.IContentCardLookupFacade.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid cardId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IContentCardLookupBusiness"/>
internal sealed class ContentCardLookupBusiness(IContentCardDataLayer dataLayer) : IContentCardLookupBusiness
{
    public async Task<bool> ExistsAsync(Guid cardId, CancellationToken cancellationToken) =>

        // Nothing to ask the database: an empty id was never issued.
        cardId != Guid.Empty && await dataLayer.ExistsAsync(cardId, cancellationToken);
}
