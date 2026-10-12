using CreatorPantry.Domain.Modules.Content.Business;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The one question another module may ask about a piece of work without reading it: is it this workspace's?
/// </summary>
/// <remarks>
/// <para>
/// A facade of its own, over the creative context's own storage and nothing else, for the reason
/// <see cref="ICreativeContextKeptPictureFacade"/> is one: a module that only needs to check an id it was
/// handed should not have to resolve everything <see cref="ICreativeContextFacade"/> needs.
/// </para>
/// <para>
/// <strong>No role gate.</strong> A resolved workspace context is the authorization for the lowest role, and
/// the answer is a yes or no about an id the caller already holds. An id nobody has and another workspace's
/// are one answer, by the workspace query filter (tenancy.md).
/// </para>
/// </remarks>
public interface ICreativeContextLookupFacade
{
    /// <summary>True when the resolved workspace has a piece of work with this id, archived or not.</summary>
    Task<bool> ExistsAsync(Guid contextId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextLookupFacade"/>
internal sealed class CreativeContextLookupFacade(ICreativeContextLookupBusiness business) : ICreativeContextLookupFacade
{
    public Task<bool> ExistsAsync(Guid contextId, CancellationToken cancellationToken) =>
        business.ExistsAsync(contextId, cancellationToken);
}
