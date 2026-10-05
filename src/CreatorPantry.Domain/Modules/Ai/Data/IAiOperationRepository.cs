using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>An operation and the proposal it produced, if it has produced one yet.</summary>
internal sealed record AiOperationWithProposal(AiOperation Operation, AiProposal? Proposal);

/// <summary>
/// One warning row as the outstanding-work read projects it.
/// </summary>
/// <remarks>
/// Identical in shape to <see cref="AiOutstandingWarningServiceModel"/> today and separate from it anyway, for the
/// reason every repository row here is separate from what it becomes: this is what the query returns, and the
/// other is what the module publishes. Collapsing them would put a published contract inside a projection, where
/// the next column added for a query's own convenience becomes a field somebody's client can read.
/// </remarks>
internal sealed record AiOutstandingWarningRow(AiWarningKind Kind, string Message, Guid AiProposalId);


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

    /// <summary>
    /// The stored change rows of one concept proposed by a concept-generation request.
    /// </summary>
    /// <param name="conceptRequestId">The concept request's operation id.</param>
    /// <param name="conceptId">The server-minted id the concept's change rows share.</param>
    /// <remarks>
    /// An empty list covers every miss without distinguishing them: no such request, a request belonging to
    /// another workspace, a request that ran some other task, a request with no proposal yet, and a concept id
    /// that names nothing in it. The caller turns all of them into one <c>not_found</c>, which is what
    /// tenancy.md asks for and what stops this read being used to probe for a neighbour's request ids.
    /// Turning the rows back into a concept is <see cref="AiConceptReader"/>'s job, not this one's.
    /// </remarks>
    Task<IReadOnlyList<AiConceptChangeRow>> FindConceptChangesAsync(
        Guid conceptRequestId, Guid conceptId, CancellationToken cancellationToken);

    /// <summary>
    /// What AI work about one recipe is still undecided: how many proposed changes remain
    /// <see cref="AiChangeDisposition.Pending"/>, and the warnings on the proposals holding them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The proposals are selected by being unfinished, not by being recent.</strong> A warning lives on an
    /// immutable proposal forever, so filtering by proposal age or by the operation's status would keep reporting
    /// cautions the creator has already accepted or rejected. "Still has a pending change" is the only condition
    /// that goes away when the work is done.
    /// </para>
    /// <para>
    /// Zero and empty for a recipe in another workspace or one that does not exist — the query filter makes the
    /// first indistinguishable from the second, and this read is not where a recipe's existence is settled.
    /// </para>
    /// </remarks>
    Task<(int PendingChangeCount, IReadOnlyList<Guid> ProposalIds, IReadOnlyList<AiOutstandingWarningRow> Warnings)>
        SummarizeOutstandingAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a proposal with this id exists in the resolved workspace.
    /// </summary>
    /// <remarks>
    /// Keyed by the proposal's own id rather than by an operation's, because that is the form another module
    /// stores: <c>PromptRecord.AiProposalId</c> is workspace-paired to <c>AiProposal</c>, and 12.3a validates
    /// that pin before writing an immutable row. An unknown id and another workspace's id are deliberately one
    /// answer — the query filter makes them indistinguishable, which is what stops this being used to probe a
    /// neighbour's proposal ids.
    /// </remarks>
    Task<bool> ProposalExistsAsync(Guid proposalId, CancellationToken cancellationToken);

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

            // The brand provenance too, because this is the read the published detail is mapped from and a block
            // that silently reported "no brand context" for a generation that had one would be worse than absent.
            // Deliberately not added to GetForDispositionAsync: accepting or rejecting changes never consults it,
            // and a write path should not carry a join it does not use.
            .Include(candidate => candidate.BrandContext!)
                .ThenInclude(brandContext => brandContext.Sources)
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

    /// <inheritdoc cref="GetAsync"/>
    /// <remarks>
    /// Stepped rather than joined in one go. The first read establishes that the id names a concept request in
    /// this workspace at all — a join would happily return a concept from an operation that ran some other
    /// task, which is not this route's resource — and only then is that request's proposal read.
    /// </remarks>
    public async Task<IReadOnlyList<AiConceptChangeRow>> FindConceptChangesAsync(
        Guid conceptRequestId,
        Guid conceptId,
        CancellationToken cancellationToken)
    {
        var isConceptRequest = await context.AiOperations.AsNoTracking().AnyAsync(
            candidate => candidate.Id == conceptRequestId
                && candidate.TaskType == AiTaskType.RecipeConcepts,
            cancellationToken);

        if (!isConceptRequest)
        {
            return [];
        }

        var proposalId = await context.AiProposals.AsNoTracking()
            .Where(candidate => candidate.AiOperationId == conceptRequestId)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (proposalId is null)
        {
            return [];
        }

        return await context.AiStructuredChanges.AsNoTracking()
            .Where(change => change.AiProposalId == proposalId
                && change.TargetKind == AiChangeTargetKind.RecipeConcept
                && change.TargetId == conceptId)
            .Select(change => new AiConceptChangeRow(change.ChangeKind, change.FieldName, change.AfterValue))
            .ToListAsync(cancellationToken);
    }

    public async Task<(int PendingChangeCount, IReadOnlyList<Guid> ProposalIds, IReadOnlyList<AiOutstandingWarningRow> Warnings)>
        SummarizeOutstandingAsync(Guid recipeId, CancellationToken cancellationToken)
    {
        // The proposals about this recipe that still hold an undecided change. Composed once and used by both
        // statements below, so the count and the warnings cannot describe two different sets.
        //
        // No WorkspaceId predicate: AiOperations, AiProposals, AiStructuredChanges and AiWarnings are all
        // workspace-owned, so the global query filter already scopes every join, and writing one by hand would
        // suggest the filter is optional.
        var unfinished = context.AiProposals.AsNoTracking()
            .Where(proposal =>
                context.AiOperations.Any(operation =>
                    operation.Id == proposal.AiOperationId && operation.RecipeId == recipeId)
                && context.AiStructuredChanges.Any(change =>
                    change.AiProposalId == proposal.Id
                    && change.Disposition == AiChangeDisposition.Pending));

        // Grouped by proposal so one statement answers both "how many changes" and "which proposals hold them".
        // The count alone could not be explained by a caller — a proposal may hold undecided changes and raise no
        // warning, leaving nothing to link a reader to.
        var perProposal = await context.AiStructuredChanges.AsNoTracking()
            .Where(change => change.Disposition == AiChangeDisposition.Pending
                && unfinished.Any(proposal => proposal.Id == change.AiProposalId))
            .GroupBy(change => change.AiProposalId)
            .Select(group => new { AiProposalId = group.Key, Count = group.Count() })
            .OrderBy(row => row.AiProposalId)
            .ToListAsync(cancellationToken);

        var warnings = await context.AiWarnings.AsNoTracking()
            .Where(warning => unfinished.Any(proposal => proposal.Id == warning.AiProposalId))
            // Stable across reads: a caller rendering a list must not see it reshuffle between two requests that
            // found the same work outstanding.
            .OrderBy(warning => warning.AiProposalId)
            .ThenBy(warning => warning.SortOrder)
            .Select(warning => new AiOutstandingWarningRow(warning.Kind, warning.Message, warning.AiProposalId))
            .ToListAsync(cancellationToken);

        return (perProposal.Sum(row => row.Count), [.. perProposal.Select(row => row.AiProposalId)], warnings);
    }

    /// <inheritdoc cref="GetAsync"/>
    public async Task<bool> ProposalExistsAsync(Guid proposalId, CancellationToken cancellationToken) =>
        await context.AiProposals.AsNoTracking()
            .AnyAsync(proposal => proposal.Id == proposalId, cancellationToken);

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
