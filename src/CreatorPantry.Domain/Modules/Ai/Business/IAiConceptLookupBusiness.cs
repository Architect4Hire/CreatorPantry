using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

/// <summary>Answers whether a recipe concept is one this workspace was actually offered.</summary>
public interface IAiConceptLookupBusiness
{
    /// <summary>
    /// Whether <paramref name="conceptId"/> is a concept inside the proposal of the concept request
    /// <paramref name="conceptRequestId"/>, in the resolved workspace.
    /// </summary>
    /// <remarks>
    /// False for a request this workspace has never held, for another workspace's, for an operation that is not
    /// a concept request, for a request with no proposal yet, and for a concept id that request never offered —
    /// one answer for all five, because a caller only needs to know whether it may store the pair.
    /// </remarks>
    Task<bool> ExistsAsync(Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken);

    /// <summary>
    /// The concept's own title and summary, or null for every miss <see cref="ExistsAsync"/> answers false to.
    /// </summary>
    /// <remarks>
    /// Model-written text a creator chose but did not write. A caller putting it in front of another model
    /// treats it as untrusted content like any other retrieved text (ai.md).
    /// </remarks>
    Task<AiConceptReference?> FindAsync(Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiConceptLookupBusiness"/>
/// <remarks>
/// Resolves a repository and nothing else, as <see cref="AiProposalLookupBusiness"/> does and for its reason:
/// a caller wanting one lookup should not have to resolve the whole request stack to get it. The read and the
/// reader are the same two a first-draft request resolves a chosen concept through, so "this concept exists"
/// means the same thing here as it does to the capability that will draft from it.
/// </remarks>
internal sealed class AiConceptLookupBusiness(IAiOperationRepository operations) : IAiConceptLookupBusiness
{
    public async Task<bool> ExistsAsync(Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken) =>
        await FindAsync(conceptRequestId, conceptId, cancellationToken) is not null;

    public async Task<AiConceptReference?> FindAsync(
        Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken)
    {
        // Nothing to ask the database: an empty id was never issued.
        if (conceptRequestId == Guid.Empty || conceptId == Guid.Empty)
        {
            return null;
        }

        return AiConceptReader.Read(
            await operations.FindConceptChangesAsync(conceptRequestId, conceptId, cancellationToken));
    }
}
