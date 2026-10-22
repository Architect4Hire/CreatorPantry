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

/// <summary>One request about a recipe still waiting on the creator, as <see cref="IAiOperationRepository.ListOpenAsync"/> reads it.</summary>
internal sealed record AiOpenProposalRow(
    Guid OperationId,
    AiTaskType TaskType,
    AiOperationStatus Status,
    DateTimeOffset RequestedAt,
    Guid? SourceVersionId,
    int PendingChangeCount,
    bool HasSafetyCaution);


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

    /// <summary>
    /// How many of this workspace's operations are queued or running, and how many of those one member asked for.
    /// </summary>
    /// <remarks>
    /// Inside the workspace filter like every read here, so a neighbour's queue is never counted and never
    /// visible in a refusal (15.3). Read from the rows rather than kept as a counter, so it is exact whatever a
    /// worker did since — finished, failed, or crashed and requeued.
    /// </remarks>
    Task<(int Workspace, int Member)> CountInFlightAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// This workspace's most recent operations of one task whose stored inputs contain a piece of text, newest
    /// first, each with the id of its proposal when it has one.
    /// </summary>
    /// <remarks>
    /// The text narrows the read and proves nothing: a caller that means "the operations for this draft" reads
    /// the inputs of each row it is given and decides for itself. Scoped by the workspace query filter like
    /// every read here.
    /// </remarks>
    Task<IReadOnlyList<(AiOperation Operation, Guid? ProposalId)>> ListByTaskInputAsync(
        AiTaskType taskType, string inputText, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The warnings stored on these proposals, in each proposal's own order. Workspace-filtered like every
    /// read here: a proposal that is not this workspace's contributes nothing.
    /// </summary>
    Task<IReadOnlyList<AiWarning>> ListWarningsAsync(IReadOnlyCollection<Guid> proposalIds, CancellationToken cancellationToken);

    /// <summary>The operation with its proposal, changes and warnings, for a creator-facing read.</summary>
    Task<AiOperationWithProposal?> GetWithProposalAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// One operation's stored proposal with the provenance a landing step needs, untracked.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetWithProposalAsync"/> because of what it loads and what it does not: the
    /// creative-context provenance, which is where a landed revision's recipe pin, context version and package
    /// checksum come from, and no warnings, which a landing never reads. Adding the join to the poll's read
    /// instead would make every poll pay for it.
    /// </remarks>
    Task<AiProposal?> FindProposalForLandingAsync(Guid operationId, CancellationToken cancellationToken);

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
    /// The proposal requests about one recipe still waiting on the creator, newest first, at most
    /// <paramref name="limit"/>: still being written, or proposed with a change still pending — the same "unfinished"
    /// <see cref="SummarizeOutstandingAsync"/> counts. Advisory requests are never listed.
    /// </summary>
    /// <remarks>Empty for a recipe in another workspace, through the query filter on every table it reads.</remarks>
    Task<IReadOnlyList<AiOpenProposalRow>> ListOpenAsync(Guid recipeId, int limit, CancellationToken cancellationToken);

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

    /// <summary>
    /// One proposal's template provenance and the recipe its operation was about, or null when this workspace
    /// holds no proposal with that id.
    /// </summary>
    /// <remarks>
    /// The same single answer <see cref="ProposalExistsAsync"/> gives for an unknown id and a neighbour's, and
    /// for the same reason: the query filter means a neighbour's row is not in the set this reads over. It
    /// projects rather than returning the entity, so the provider, the model, the token counts and the
    /// workspace id are never read on a path whose caller is another module (12.3a).
    /// </remarks>
    Task<AiProposalLineageServiceModel?> FindProposalLineageAsync(
        Guid proposalId, CancellationToken cancellationToken);

    /// <summary>
    /// Which of these recipes this workspace created by accepting an AI first draft, and when.
    /// </summary>
    /// <remarks>
    /// A recipe that was written by hand, one whose draft was rejected, and another workspace's recipe are all
    /// simply absent from the answer — the query filter makes the last indistinguishable from the first. An
    /// accepted draft's operation is terminal, so an entry here never goes away.
    /// </remarks>
    Task<List<AiAcceptedDraftServiceModel>> ListAcceptedDraftsAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken cancellationToken);

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

    public async Task<(int Workspace, int Member)> CountInFlightAsync(
        Guid membershipId, CancellationToken cancellationToken)
    {
        var inFlight = context.AiOperations
            .AsNoTracking()
            .Where(operation => operation.Status == AiOperationStatus.Requested
                || operation.Status == AiOperationStatus.Running);

        var workspace = await inFlight.CountAsync(cancellationToken);
        var member = workspace == 0
            ? 0
            : await inFlight.CountAsync(operation => operation.RequestedByMembershipId == membershipId, cancellationToken);

        return (workspace, member);
    }

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
    public async Task<IReadOnlyList<AiWarning>> ListWarningsAsync(
        IReadOnlyCollection<Guid> proposalIds, CancellationToken cancellationToken) =>
        proposalIds.Count == 0
            ? []
            : await context.AiWarnings.AsNoTracking()
                .Where(warning => proposalIds.Contains(warning.AiProposalId))
                .OrderBy(warning => warning.AiProposalId)
                .ThenBy(warning => warning.SortOrder)
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<(AiOperation Operation, Guid? ProposalId)>> ListByTaskInputAsync(
        AiTaskType taskType, string inputText, int limit, CancellationToken cancellationToken)
    {
        var rows = await context.AiOperations.AsNoTracking()
            .Where(operation => operation.TaskType == taskType
                && operation.TaskInputsJson != null
                && operation.TaskInputsJson.Contains(inputText))
            .OrderByDescending(operation => operation.RequestedAt)
            .ThenByDescending(operation => operation.Id)
            .Take(limit)
            .Select(operation => new
            {
                Operation = operation,
                ProposalId = context.AiProposals
                    .Where(proposal => proposal.AiOperationId == operation.Id)
                    .Select(proposal => (Guid?)proposal.Id)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => (row.Operation, row.ProposalId))];
    }

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

    /// <inheritdoc cref="FindProposalForLandingAsync"/>
    public async Task<AiProposal?> FindProposalForLandingAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await context.AiProposals.AsNoTracking()
            .Include(candidate => candidate.Changes)
            .Include(candidate => candidate.BrandContext!)
            .Include(candidate => candidate.CreativeContext!)
            .FirstOrDefaultAsync(candidate => candidate.AiOperationId == operationId, cancellationToken);

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

    public async Task<IReadOnlyList<AiOpenProposalRow>> ListOpenAsync(
        Guid recipeId, int limit, CancellationToken cancellationToken)
    {
        // No WorkspaceId predicate, for the reason SummarizeOutstandingAsync gives: every table here is filtered.
        var rows = await context.AiOperations.AsNoTracking()
            .Where(operation => operation.RecipeId == recipeId
                && operation.Scope != AiOperationScope.Advisory
                && (operation.Status == AiOperationStatus.Requested
                    || operation.Status == AiOperationStatus.Running
                    || (operation.Status == AiOperationStatus.Proposed
                        && context.AiStructuredChanges.Any(change =>
                            change.Disposition == AiChangeDisposition.Pending
                            && context.AiProposals.Any(proposal =>
                                proposal.Id == change.AiProposalId && proposal.AiOperationId == operation.Id)))))
            .Select(operation => new AiOpenProposalRow(
                operation.Id,
                operation.TaskType,
                operation.Status,
                operation.RequestedAt,
                operation.RecipeVersionId,
                context.AiStructuredChanges.Count(change =>
                    change.Disposition == AiChangeDisposition.Pending
                    && context.AiProposals.Any(proposal =>
                        proposal.Id == change.AiProposalId && proposal.AiOperationId == operation.Id)),
                context.AiWarnings.Any(warning =>
                    warning.Kind == AiWarningKind.SafetyCaution
                    && context.AiProposals.Any(proposal =>
                        proposal.Id == warning.AiProposalId && proposal.AiOperationId == operation.Id))))
            .ToListAsync(cancellationToken);

        // Ordered here rather than in SQL: a recipe has a handful of open requests, and SQLite cannot order a
        // DateTimeOffset. The id breaks a tie, so a list read twice never reshuffles.
        return [.. rows.OrderByDescending(row => row.RequestedAt).ThenBy(row => row.OperationId).Take(limit)];
    }

    /// <inheritdoc cref="GetAsync"/>
    public async Task<bool> ProposalExistsAsync(Guid proposalId, CancellationToken cancellationToken) =>
        await context.AiProposals.AsNoTracking()
            .AnyAsync(proposal => proposal.Id == proposalId, cancellationToken);

    /// <inheritdoc cref="GetAsync"/>
    public Task<AiProposalLineageServiceModel?> FindProposalLineageAsync(
        Guid proposalId, CancellationToken cancellationToken) =>
        context.AiProposals.AsNoTracking()
            .Where(proposal => proposal.Id == proposalId)

            // Joined to the operation because the recipe pin lives there, not on the proposal: a proposal
            // records the version it was computed against, and the operation records what the creator asked
            // about. Both sets are filtered by workspace, so the join cannot reach across one.
            .Join(
                context.AiOperations.AsNoTracking(),
                proposal => proposal.AiOperationId,
                operation => operation.Id,
                (proposal, operation) => new AiProposalLineageServiceModel(
                    operation.TaskType,
                    proposal.PromptTemplateId,
                    proposal.PromptTemplateVersion,
                    proposal.PromptTemplateBodyChecksum,
                    operation.RecipeId,
                    operation.RecipeVersionId))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc cref="GetAsync"/>
    public Task<List<AiAcceptedDraftServiceModel>> ListAcceptedDraftsAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken cancellationToken) =>
        context.AiOperations.AsNoTracking()
            .Where(operation =>
                operation.TaskType == AiTaskType.RecipeFirstDraft
                && (operation.Status == AiOperationStatus.Accepted
                    || operation.Status == AiOperationStatus.PartiallyAccepted)
                && operation.RecipeId != null
                && recipeIds.Contains(operation.RecipeId.Value))
            .Select(operation => new AiAcceptedDraftServiceModel(operation.RecipeId!.Value, operation.StatusChangedAt))
            .ToListAsync(cancellationToken);

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
