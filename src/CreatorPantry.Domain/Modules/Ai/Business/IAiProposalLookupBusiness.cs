using CreatorPantry.Domain.Modules.Ai.Data;

namespace CreatorPantry.Domain.Modules.Ai.Business;

/// <summary>
/// Resolves a proposal id another module is about to store, inside the resolved workspace.
/// </summary>
public interface IAiProposalLookupBusiness
{
    /// <summary>
    /// Whether this workspace holds a proposal with that id.
    /// </summary>
    /// <remarks>
    /// False for an id this workspace has never held, for another workspace's proposal, and for
    /// <see cref="Guid.Empty"/> — deliberately one answer, because a caller only needs to know whether it may
    /// store the value, and three answers would tell it whose it was.
    /// </remarks>
    Task<bool> ExistsAsync(Guid aiProposalId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiProposalLookupBusiness"/>
internal sealed class AiProposalLookupBusiness(IAiOperationRepository operations) : IAiProposalLookupBusiness
{
    public Task<bool> ExistsAsync(Guid aiProposalId, CancellationToken cancellationToken) =>
        aiProposalId == Guid.Empty
            // Nothing to ask the database: an empty id was never issued, so it is no proposal of this workspace
            // or of any other. Answered here so a caller's missing value costs no query.
            ? Task.FromResult(false)
            : operations.ProposalExistsAsync(aiProposalId, cancellationToken);
}
