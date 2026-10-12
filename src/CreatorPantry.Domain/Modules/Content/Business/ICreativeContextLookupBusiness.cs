using CreatorPantry.Domain.Modules.Content.Data;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>Whether an id names a piece of work in the resolved workspace.</summary>
public interface ICreativeContextLookupBusiness
{
    /// <inheritdoc cref="Facade.ICreativeContextLookupFacade.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid contextId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextLookupBusiness"/>
internal sealed class CreativeContextLookupBusiness(ICreativeContextDataLayer dataLayer) : ICreativeContextLookupBusiness
{
    public async Task<bool> ExistsAsync(Guid contextId, CancellationToken cancellationToken) =>

        // Nothing to ask the database: an empty id was never issued.
        contextId != Guid.Empty && await dataLayer.FindAsync(contextId, cancellationToken) is not null;
}
