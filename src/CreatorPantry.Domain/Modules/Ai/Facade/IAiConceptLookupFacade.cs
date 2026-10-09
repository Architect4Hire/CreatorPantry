using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The cross-module entry point for resolving a recipe concept — a request id and a concept id — for a module
/// that stores the pair (AF.1.3).
/// </summary>
/// <remarks>
/// <para>
/// It exists because a concept is not a row. <c>CreativeContextReference.ConceptRequestId</c> is
/// workspace-paired to <c>AiOperations</c>, but nothing in the schema can say that the operation is a concept
/// request or that <c>ConceptId</c> names something it offered, so the write seam has to ask before it stores.
/// </para>
/// <para>
/// A class of its own for the reason <see cref="IAiProposalLookupFacade"/> gives, and the same shape: one
/// boolean, no role gate — a resolved workspace context is the authorization for the lowest role — and one
/// answer for every kind of miss so that none discloses another workspace's request.
/// </para>
/// </remarks>
public interface IAiConceptLookupFacade
{
    /// <inheritdoc cref="IAiConceptLookupBusiness.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IAiConceptLookupBusiness.FindAsync"/>
    Task<AiConceptReference?> FindAsync(Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiConceptLookupFacade"/>
internal sealed class AiConceptLookupFacade(IAiConceptLookupBusiness business) : IAiConceptLookupFacade
{
    public Task<bool> ExistsAsync(Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken) =>
        business.ExistsAsync(conceptRequestId, conceptId, cancellationToken);

    public Task<AiConceptReference?> FindAsync(
        Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken) =>
        business.FindAsync(conceptRequestId, conceptId, cancellationToken);
}
