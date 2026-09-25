using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>An operation and the proposal it produced, if it has produced one yet.</summary>
internal sealed record AiOperationWithProposal(AiOperation Operation, AiProposal? Proposal);

/// <summary>
/// EF Core access to the AI operation aggregate, within the resolved workspace.
/// </summary>
/// <remarks>
/// No <c>WorkspaceId</c> predicates and never <c>IgnoreQueryFilters</c>: workspace scope comes from the global
/// query filter, so a query written here cannot forget it. The one place that reads across workspaces is
/// <see cref="AiOperationClaimRepository"/>, which exists separately for exactly that reason.
/// </remarks>
internal interface IAiOperationRepository
{
    void Add(AiOperation operation);

    Task<AiOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken);

    Task<AiOperation?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The operation with its proposal, changes and warnings, for a creator-facing read.</summary>
    Task<AiOperationWithProposal?> GetWithProposalAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// The operation and its proposal with every change, tracked, for a disposition to decide about.
    /// </summary>
    /// <remarks>
    /// Tracked, unlike <see cref="GetWithProposalAsync"/>: the dispositions are written onto these very rows.
    /// Warnings are loaded too, so the audit entry can record how many accepted changes carried a safety caution
    /// — ai.md asks for uncertainty to be surfaced, and a record that cannot say whether a caution was attached
    /// to what the creator took cannot answer that afterwards.
    /// </remarks>
    Task<AiOperationWithProposal?> GetForDispositionAsync(Guid operationId, CancellationToken cancellationToken);

    void AddProposal(AiProposal proposal);

    void AddFeedback(AiProposalFeedback feedback);

    void AddExecutionRecords(IEnumerable<AiExecutionMetadata> records);

    Task<int> CountExecutionAttemptsAsync(Guid operationId, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiOperationRepository"/>
internal sealed class AiOperationRepository(CreatorPantryDbContext context) : IAiOperationRepository
{
    public void Add(AiOperation operation) => context.AiOperations.Add(operation);

    /// <remarks>
    /// No <c>WorkspaceId</c> predicate and no <c>IgnoreQueryFilters</c>: the global query filter is what makes
    /// an operation from another workspace invisible here, and restating the condition would only hide whether
    /// the filter was working.
    /// </remarks>
    public async Task<AiOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken) =>
        await context.AiOperations.FirstOrDefaultAsync(
            operation => operation.Id == operationId, cancellationToken);

    /// <inheritdoc cref="GetAsync"/>
    public async Task<AiOperation?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await context.AiOperations.FirstOrDefaultAsync(
            operation => operation.IdempotencyKey == idempotencyKey, cancellationToken);

    /// <inheritdoc cref="GetAsync"/>
    /// <remarks>
    /// No-tracking with the children loaded: this answers a poll, and nothing written here comes back through
    /// it. The proposal is a left join because a request that has not produced one yet is the common case, not
    /// an error.
    /// </remarks>
    public async Task<AiOperationWithProposal?> GetWithProposalAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await context.AiOperations.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);

        if (operation is null)
        {
            return null;
        }

        var proposal = await context.AiProposals.AsNoTracking()
            .Include(candidate => candidate.Changes)
            .Include(candidate => candidate.Warnings)
            .FirstOrDefaultAsync(candidate => candidate.AiOperationId == operationId, cancellationToken);

        return new AiOperationWithProposal(operation, proposal);
    }

    /// <inheritdoc cref="GetAsync"/>
    /// <remarks>
    /// Tracked, and that is the difference from <see cref="GetWithProposalAsync"/>: the creator's decision is
    /// written onto these change rows, so they must be the entities the context knows about rather than
    /// detached copies of them.
    /// </remarks>
    public async Task<AiOperationWithProposal?> GetForDispositionAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await context.AiOperations.FirstOrDefaultAsync(
            candidate => candidate.Id == operationId, cancellationToken);

        if (operation is null)
        {
            return null;
        }

        var proposal = await context.AiProposals
            .Include(candidate => candidate.Changes)
            .Include(candidate => candidate.Warnings)
            .FirstOrDefaultAsync(candidate => candidate.AiOperationId == operationId, cancellationToken);

        return new AiOperationWithProposal(operation, proposal);
    }

    public void AddProposal(AiProposal proposal) => context.AiProposals.Add(proposal);

    public void AddFeedback(AiProposalFeedback feedback) => context.AiProposalFeedback.Add(feedback);

    public void AddExecutionRecords(IEnumerable<AiExecutionMetadata> records) =>
        context.AiExecutionMetadata.AddRange(records);

    /// <summary>How many execution rows this operation already has, so attempt numbers keep counting up.</summary>
    /// <remarks>
    /// Read rather than derived from <c>AiOperation.Attempts</c>: a claim increments the attempt count, and one
    /// claim can make several provider calls. The two numbers answer different questions and must not be
    /// conflated.
    /// </remarks>
    public async Task<int> CountExecutionAttemptsAsync(Guid operationId, CancellationToken cancellationToken) =>
        await context.AiExecutionMetadata.CountAsync(
            record => record.AiOperationId == operationId, cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken) =>
        await context.SaveChangesAsync(cancellationToken);
}
