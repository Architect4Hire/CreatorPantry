using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>What requesting an operation did: a new one, or the one an identical earlier request created.</summary>
public enum AiOperationRequestOutcome
{
    Created = 1,

    /// <summary>The same key and the same request. The caller gets the original operation.</summary>
    Replayed = 2,

    /// <summary>
    /// The same key describing a <em>different</em> request. Refused rather than served either operation.
    /// </summary>
    /// <remarks>
    /// One key and one payload produce one operation; one key and two payloads is a caller bug. Returning the
    /// original would silently answer a question nobody asked, and creating a second would make the key
    /// meaningless.
    /// </remarks>
    KeyReusedForDifferentRequest = 3,
}

/// <param name="Outcome">Whether the operation was created, replayed, or refused.</param>
/// <param name="Operation">The operation, or null when the key was reused for a different request.</param>
public sealed record AiOperationRequest(AiOperationRequestOutcome Outcome, AiOperation? Operation);

/// <summary>Whether a write that completes an operation was accepted.</summary>
public enum AiOperationWriteOutcome
{
    Applied = 1,

    /// <summary>No such operation in this workspace.</summary>
    NotFound = 2,

    /// <summary>
    /// The operation is no longer held by this worker — its lease lapsed and another took it, or it already
    /// reached a terminal state.
    /// </summary>
    /// <remarks>
    /// The check that stops a worker which woke up late from writing a proposal over the top of the one that
    /// replaced it. Without it, two workers completing the same operation would be a last-writer-wins race.
    /// </remarks>
    LeaseLost = 3,
}

/// <summary>What a decided disposition should record. Every value in it is Business's decision.</summary>
/// <param name="AcceptedChangeIds">
/// The changes to mark accepted. Every other change on the proposal is marked rejected — a proposal leaves
/// review with no change still pending, because "pending" on a terminal operation would mean nobody ever
/// decided.
/// </param>
/// <param name="Status">The terminal status the operation moves to.</param>
/// <param name="DecidedByMembershipId">
/// The membership of the creator who decided, stamped on every change. The workspace-scoped identity from the
/// resolved context, never a request field.
/// </param>
/// <param name="Audit">The entry recording the decision, staged in the same save.</param>
/// <param name="Feedback">The creator's own words, or <c>null</c> when they left none.</param>
internal sealed record AiDispositionInstruction(
    IReadOnlyCollection<Guid> AcceptedChangeIds,
    AiOperationStatus Status,
    Guid DecidedByMembershipId,
    AuditEntry Audit,
    AiProposalFeedback? Feedback);

/// <summary>Whether a disposition was recorded, and what it did.</summary>
internal enum AiDispositionOutcome
{
    /// <summary>Recorded. Any accepted changes were applied in the same transaction.</summary>
    Applied = 1,

    /// <summary>No such operation, or no proposal on it, within this workspace.</summary>
    NotFound = 2,

    /// <summary>
    /// The proposal is no longer awaiting a decision: another request decided it first, or it never reached
    /// <see cref="AiOperationStatus.Proposed"/>.
    /// </summary>
    NotAwaitingDecision = 3,

    /// <summary>
    /// The same decision was already recorded. Nothing was written a second time and the recorded outcome is
    /// returned — a retried request must not buy a second version of the recipe.
    /// </summary>
    Replayed = 4,

    /// <summary>
    /// Applying the accepted changes was refused by the recipe domain. Nothing was written at all: not the
    /// version, not the dispositions, not the feedback, not the status.
    /// </summary>
    ApplyRefused = 5,
}

/// <param name="RecipeVersionNumber">The version the accepted changes wrote, when they wrote one.</param>
/// <param name="Error">The recipe domain's refusal, on <see cref="AiDispositionOutcome.ApplyRefused"/>.</param>
internal sealed record AiDispositionWrite(
    AiDispositionOutcome Outcome,
    int? RecipeVersionNumber = null,
    OperationError? Error = null);

/// <summary>What accepting a first draft wrote.</summary>
/// <param name="RecipeId">
/// The recipe this created — or, on a replay, the one the earlier acceptance created.
/// </param>
/// <param name="RecipeVersionNumber">Version 1, when a recipe was created.</param>
/// <remarks>
/// <strong>A create's replay has to be able to name what it made</strong>, which is the one way this differs
/// from <see cref="AiDispositionWrite"/>. A disposition can say "no version was written this time" and leave
/// the client to re-read the recipe it already knows about; a creator retrying an acceptance does not yet know
/// the recipe's id, and an answer that cannot give it would send them looking for a recipe they cannot find.
/// </remarks>
internal sealed record AiDraftAcceptanceWrite(
    AiDispositionOutcome Outcome,
    Guid? RecipeId = null,
    int? RecipeVersionNumber = null,
    OperationError? Error = null);

/// <summary>What accepting brand guidance wrote (11A.18).</summary>
/// <param name="Written">
/// The guide version the accepted guidance produced, or <c>null</c> when it produced none — a rejection, a
/// replay, or guidance that already matched the guide word for word.
/// </param>
/// <param name="Error">The brand domain's refusal, on <see cref="AiDispositionOutcome.ApplyRefused"/>.</param>
/// <remarks>
/// <para>
/// <strong>A replay names no version, unlike <see cref="AiDraftAcceptanceWrite"/>.</strong> That one has to:
/// a creator retrying a draft acceptance does not yet know their recipe's id, so an answer without it would
/// send them looking for a recipe they cannot find. Here the caller named the guide in the request that started
/// the proposal, so the version is one read away — and recording which guide version an operation produced
/// would mean a column on <c>AiOperation</c> pointing into the brand module for a value nobody needs from here.
/// </para>
/// <para>
/// <strong>It carries the brand module's ServiceModel where <see cref="AiDraftAcceptanceWrite"/> wraps the
/// recipe module's in <see cref="AiCreatedRecipe"/>, and the asymmetry is deliberate.</strong> That wrapper
/// exists because the AI module needed two fields of <c>CreatedRecipeServiceModel</c> and had no use for the
/// rest; here every field of <see cref="BrandStyleGuideVersionCreatedServiceModel"/> is wanted, so an
/// identically-shaped local copy would be duplication with a mapper attached and two places to edit whenever a
/// count is added. A ServiceModel a facade returns is one of the three things permitted to cross a module
/// boundary, and the translation into this module's own HTTP shape still happens — once, in
/// <c>AiBrandGuideAcceptanceBusiness.Describe</c>, which is why this route publishes
/// <c>AiBrandGuideVersion</c> rather than a brand type.
/// </para>
/// </remarks>
internal sealed record AiBrandGuideAcceptanceWrite(
    AiDispositionOutcome Outcome,
    BrandStyleGuideVersionCreatedServiceModel? Written = null,
    OperationError? Error = null);

/// <summary>Composes the persistence operations the AI worker and facade need.</summary>
/// <remarks>
/// <strong>No gateway dependency, deliberately.</strong> A provider call must never happen inside a database
/// transaction, and the cleanest way to guarantee that is a data layer with no way to make one. The worker
/// sequences it: claim, then call, then store. A test asserts this type's constructor stays free of
/// <see cref="IAiCompletionGateway"/>.
/// </remarks>
internal interface IAiOperationDataLayer
{
    Task<AiOperationRequest> RequestAsync(AiOperation operation, CancellationToken cancellationToken);

    /// <summary>The operation and its proposal, for a creator polling a request.</summary>
    Task<AiOperationWithProposal?> GetWithProposalAsync(Guid operationId, CancellationToken cancellationToken);

    /// <param name="reservationId">
    /// The allowance this run was admitted under, so its charge settles in the same transaction as the attempt
    /// records and ledger entries it is computed from. Null when nothing was held — an unmeterable task, or a
    /// caller that does not admit.
    /// </param>
    Task<AiOperationWriteOutcome> StoreProposalAsync(
        Guid operationId,
        Guid leaseToken,
        AiProposal proposal,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        Guid? reservationId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="StoreProposalAsync" path="/param[@name='reservationId']"/>
    Task<AiOperationWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        AiFailureCategory category,
        string? summary,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        Guid? reservationId,
        CancellationToken cancellationToken);

    Task<AiOperationWriteOutcome> RenewLeaseAsync(
        Guid operationId,
        Guid leaseToken,
        CancellationToken cancellationToken);

    /// <summary>The operation and its proposal with every change, for Business to decide a disposition from.</summary>
    Task<AiOperationWithProposal?> GetForDispositionAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One concept proposed by a concept-generation request, or null when there is no such concept here.
    /// </summary>
    /// <remarks>
    /// The rows come from the repository and <see cref="AiConceptReader"/> turns them back into the concept
    /// they describe — composing a complete read out of a query and a projection is what this layer is for,
    /// and it keeps the flattening rule out of the repository (backend.md).
    /// </remarks>
    Task<AiConceptReference?> FindConceptAsync(
        Guid conceptRequestId,
        Guid conceptId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IAiOperationRepository.SummarizeOutstandingAsync"/>
    /// <remarks>
    /// Composed here rather than published as two reads, because the count and the warnings only mean anything
    /// together: "three changes undecided, and one of them carries a safety caution" is one answer.
    /// </remarks>
    Task<AiOutstandingSummaryServiceModel> SummarizeOutstandingAsync(
        Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a decided disposition, applying its accepted changes in the same transaction.
    /// </summary>
    /// <param name="applyAccepted">
    /// Applies the accepted changes and returns the version number they wrote, or <c>null</c> when they wrote
    /// none. Supplied by Business because it calls the recipe module's facade, which is the only route a module
    /// may take into another — and passed in as a delegate so that the call happens inside this transaction
    /// rather than beside it.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>This is the atomic boundary AIREC-007 asks for.</strong> The recipe version, the per-change
    /// dispositions, the feedback row, the operation's terminal status and the audit entry commit together or
    /// none of them do. They span two modules, and they can be one transaction because both write on the same
    /// request-scoped <c>DbContext</c> — so the recipe facade's own save enlists in the transaction opened here
    /// rather than committing on its own.
    /// </para>
    /// <para>
    /// <strong>The transaction is explicit, unlike every other write in this data layer.</strong> Elsewhere one
    /// <c>SaveChanges</c> over the pending entities already is one transaction, and an explicit one would be
    /// ceremony. Here it cannot be: the recipe facade saves for itself partway through, so without a boundary
    /// around both saves a refusal after the recipe had already committed would leave a version applied from a
    /// proposal that was never dispositioned.
    /// </para>
    /// <para>
    /// <strong>No provider call happens in here</strong>, and nothing in this type could make one — see the
    /// remarks on the interface.
    /// </para>
    /// </remarks>
    Task<AiDispositionWrite> DispositionAsync(
        Guid operationId,
        AiDispositionInstruction instruction,
        Func<CancellationToken, Task<OperationResult<int?>>> applyAccepted,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a decided first draft, creating its recipe in the same transaction.
    /// </summary>
    /// <param name="createRecipe">
    /// Creates the recipe and returns its id and version number, or <c>null</c> for a rejection, which creates
    /// nothing. Supplied by Business because it calls the recipe module's facade — the only route a module may
    /// take into another — and passed as a delegate so the call happens inside this transaction rather than
    /// beside it.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The same atomic boundary <see cref="DispositionAsync"/> owns, with one more thing inside it.</strong>
    /// The recipe, its version 1, the per-change dispositions, the feedback row, the operation's terminal
    /// status, the operation's new <c>RecipeId</c> and the audit entry commit together or none of them do.
    /// They span two modules and can be one transaction for the same reason as before: both write on the same
    /// request-scoped <c>DbContext</c>, so the recipe facade enlists here rather than committing on its own.
    /// </para>
    /// <para>
    /// <strong>This is why the recipe module needed a create that is not <c>CreateAsync</c>.</strong> That one
    /// runs inside <c>IIdempotencyDataLayer.ExecuteAsync</c>, which opens its own transaction — nesting, which
    /// throws — and clears the change tracker, which would discard the dispositions staged here.
    /// <c>CreateFromProposalAsync</c> is the same pipeline with that wrapper removed.
    /// </para>
    /// <para>
    /// <strong>Replay is the operation's own status, not an idempotency record.</strong> An operation that has
    /// left <c>Proposed</c> is re-read inside the transaction; if the same decision was already recorded, the
    /// recipe it created is returned and nothing is written a second time. That is what stops a retried
    /// acceptance producing a second recipe — and why the recipe's id is stamped onto the operation here.
    /// </para>
    /// </remarks>
    Task<AiDraftAcceptanceWrite> AcceptDraftAsync(
        Guid operationId,
        AiDispositionInstruction instruction,
        Func<CancellationToken, Task<OperationResult<AiCreatedRecipe?>>> createRecipe,
        bool carriesEdits,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a decided brand-guide proposal, writing the guide version it was accepted into in the same
    /// transaction (11A.18).
    /// </summary>
    /// <param name="createVersion">
    /// Writes the new guide version and returns what it wrote, or <c>null</c> when nothing was written — a
    /// rejection, or accepted guidance that matched the guide already. Supplied by Business because it calls the
    /// brand module's facade, which is the only route a module may take into another, and passed as a delegate
    /// so the call happens inside this transaction rather than beside it.
    /// </param>
    /// <param name="carriesEdits">
    /// Whether the creator rewrote any of the guidance. Decides what a replay may answer — see the remarks.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The same atomic boundary <see cref="AcceptDraftAsync"/> owns, over a different module.</strong>
    /// The guide version with its sections, rules and source links, the per-item dispositions, the feedback row,
    /// the operation's terminal status and both audit entries commit together or none of them do. They span two
    /// modules and can be one transaction because both write on the same request-scoped <c>DbContext</c>, so the
    /// brand facade's own save enlists in the transaction opened here rather than committing on its own.
    /// </para>
    /// <para>
    /// <strong>This is the one path from a stored <c>AiStructuredChange</c> to a brand guide, and it reaches a
    /// draft.</strong> <see cref="AiChangeTargetKind.BrandGuideSection"/> is still absent from
    /// <see cref="AiChangeApplicability"/> and still answers <c>null</c> in <see cref="AiChangeTargetPolicy"/>,
    /// so there is no path from one of these rows to a <em>recipe</em> and never was. What this adds is a route
    /// to a guide version that the creator selected item by item, that approves nothing, and that activates
    /// nothing.
    /// </para>
    /// <para>
    /// <strong>Replay is the operation's own status, not an idempotency record</strong>, as for an accepted
    /// draft: an operation that has left <see cref="AiOperationStatus.Proposed"/> is re-read inside the
    /// transaction, and if the same decision was already recorded, nothing is written a second time. That is what
    /// stops a retried acceptance producing a second guide version. A replay that carried rewrites is refused
    /// instead — nothing stores what the creator's words were, only that some were theirs, so answering "already
    /// done" would discard the words in this request while reporting success.
    /// </para>
    /// <para>
    /// <strong>No provider call happens in here</strong>, and nothing in this type could make one — see the
    /// remarks on the interface.
    /// </para>
    /// </remarks>
    Task<AiBrandGuideAcceptanceWrite> AcceptBrandGuideProposalAsync(
        Guid operationId,
        AiDispositionInstruction instruction,
        Func<CancellationToken, Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel?>>> createVersion,
        bool carriesEdits,
        CancellationToken cancellationToken);
}

/// <summary>The recipe an acceptance created, in the only terms the AI module needs to record.</summary>
internal sealed record AiCreatedRecipe(Guid RecipeId, int VersionNumber);

/// <inheritdoc cref="IAiOperationDataLayer"/>
internal sealed class AiOperationDataLayer(
    CreatorPantryDbContext context,
    IAiOperationRepository operations,
    IAuditWriter auditWriter,
    IClock clock,
    IWorkspaceContext workspaceContext,
    IAiUsageRecordingFacade usage,
    IAiQuotaAdmissionFacade quota) : IAiOperationDataLayer
{
    public async Task<AiOperationRequest> RequestAsync(
        AiOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // The bound the column used to enforce. TaskInputsJson is nvarchar(max) now -- AIREC-002's brief plus a
        // selected concept exceeds 4000 characters at the declared field bounds, and SQL Server offers nothing
        // in between -- so the limit has to be checked somewhere every writer passes through. Here, rather than
        // in one Business class: a second capability writing this column would otherwise inherit no bound at
        // all. Business refuses first, with a stable code and a message a creator can act on; this is the
        // backstop that makes AiPolicy.TaskInputsJsonMaxLength true rather than advisory.
        if (operation.TaskInputsJson is { } inputs && inputs.Length > AiPolicy.TaskInputsJsonMaxLength)
        {
            throw new ArgumentException(
                $"Task inputs exceed {AiPolicy.TaskInputsJsonMaxLength} characters. Refuse the request in "
                    + "Business rather than letting it reach persistence.",
                nameof(operation));
        }

        // Read first, because a replay is the common case for a retried HTTP request and losing the insert race
        // is the rare one. Both paths are covered: the unique index is what makes this correct under
        // concurrency, and this read is what makes it cheap.
        var existing = await operations.FindByIdempotencyKeyAsync(operation.IdempotencyKey, cancellationToken);

        if (existing is not null)
        {
            return Reconcile(existing, operation);
        }

        operations.Add(operation);

        try
        {
            await operations.SaveAsync(cancellationToken);

            return new AiOperationRequest(AiOperationRequestOutcome.Created, operation);
        }
        catch (DbUpdateException)
        {
            // Two identical requests arrived together and the unique index on (WorkspaceId, IdempotencyKey)
            // decided between them. The loser reads the winner's row, which is exactly what a replay should
            // return — so this is the same answer the read above would have given a moment later.
            var winner = await operations.FindByIdempotencyKeyAsync(operation.IdempotencyKey, cancellationToken);

            return winner is null
                ? throw new InvalidOperationException(
                    "The operation insert was rejected but no operation holds its idempotency key.")
                : Reconcile(winner, operation);
        }
    }

    /// <summary>
    /// Whether the stored operation answers the same question the new request asks.
    /// </summary>
    /// <remarks>
    /// The payload comparison the restriction requires: one key <em>and payload</em> produce one operation.
    /// Everything that defines what the operation will do is compared; nothing that merely records who asked or
    /// when, because a genuine replay of the same request may legitimately differ in neither.
    /// </remarks>
    private static AiOperationRequest Reconcile(AiOperation existing, AiOperation requested) =>
        existing.TaskType == requested.TaskType
        && existing.Scope == requested.Scope
        && existing.RecipeId == requested.RecipeId
        && existing.RecipeVersionId == requested.RecipeVersionId
        && AiTaskInputsIdentity.Of(existing.TaskType, existing.TaskInputsJson)
            == AiTaskInputsIdentity.Of(requested.TaskType, requested.TaskInputsJson)
            ? new AiOperationRequest(AiOperationRequestOutcome.Replayed, existing)
            : new AiOperationRequest(AiOperationRequestOutcome.KeyReusedForDifferentRequest, null);

    public async Task<AiOperationWithProposal?> GetWithProposalAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await operations.GetWithProposalAsync(operationId, cancellationToken);

    public async Task<AiOperationWriteOutcome> StoreProposalAsync(
        Guid operationId,
        Guid leaseToken,
        AiProposal proposal,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        Guid? reservationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(attempts);

        var operation = await operations.GetAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return AiOperationWriteOutcome.NotFound;
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return AiOperationWriteOutcome.LeaseLost;
        }

        var now = clock.UtcNow;

        operation.Status = AiOperationStatus.Proposed;
        operation.StatusChangedAt = now;

        // CompletedAt stays null, and the check constraint is what insisted on it. Proposed is not a terminal
        // state — the creator still has to accept, reject or leave it to expire — so an operation that recorded
        // a completion here would claim to be finished while the work it exists for had not happened yet.
        operation.LeasedBy = null;
        operation.LeaseExpiresAt = null;

        operations.AddProposal(proposal);
        await RecordAttemptsAsync(operation, attempts, reservationId, cancellationToken);

        // One SaveChanges over every pending entity on one context is already one transaction — the reasoning
        // IRecipeDataLayer records, including why an explicit BeginTransactionAsync would be worse under the
        // retrying execution strategy. The proposal, its changes, its warnings, the execution rows, the
        // account usage entries, the allowance settlement and the status change commit together or not at all.
        await operations.SaveAsync(cancellationToken);

        return AiOperationWriteOutcome.Applied;
    }

    public async Task<AiOperationWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        AiFailureCategory category,
        string? summary,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        Guid? reservationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var operation = await operations.GetAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return AiOperationWriteOutcome.NotFound;
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return AiOperationWriteOutcome.LeaseLost;
        }

        var now = clock.UtcNow;

        operation.Status = AiOperationStatus.Failed;
        operation.FailureCategory = category;
        operation.StatusChangedAt = now;
        operation.CompletedAt = now;
        operation.LeasedBy = null;
        operation.LeaseExpiresAt = null;

        await RecordAttemptsAsync(operation, attempts, reservationId, cancellationToken);

        await operations.SaveAsync(cancellationToken);

        return AiOperationWriteOutcome.Applied;
    }

    public async Task<AiOperationWriteOutcome> RenewLeaseAsync(
        Guid operationId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var operation = await operations.GetAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return AiOperationWriteOutcome.NotFound;
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return AiOperationWriteOutcome.LeaseLost;
        }

        operation.LeaseExpiresAt = clock.UtcNow + AiPolicy.LeaseDuration;

        await operations.SaveAsync(cancellationToken);

        return AiOperationWriteOutcome.Applied;
    }

    public async Task<AiOperationWithProposal?> GetForDispositionAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await operations.GetForDispositionAsync(operationId, cancellationToken);

    public async Task<AiOutstandingSummaryServiceModel> SummarizeOutstandingAsync(
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        var (pendingChangeCount, proposalIds, warnings) =
            await operations.SummarizeOutstandingAsync(recipeId, cancellationToken);

        return new AiOutstandingSummaryServiceModel(
            pendingChangeCount,
            proposalIds,
            [.. warnings.Select(row => new AiOutstandingWarningServiceModel(row.Kind, row.Message, row.AiProposalId))]);
    }

    public async Task<AiConceptReference?> FindConceptAsync(
        Guid conceptRequestId,
        Guid conceptId,
        CancellationToken cancellationToken) =>
        AiConceptReader.Read(
            await operations.FindConceptChangesAsync(conceptRequestId, conceptId, cancellationToken));

    public async Task<AiDispositionWrite> DispositionAsync(
        Guid operationId,
        AiDispositionInstruction instruction,
        Func<CancellationToken, Task<OperationResult<int?>>> applyAccepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(applyAccepted);

        // The pattern IdempotencyDataLayer uses: a retrying execution strategy re-runs the whole unit, so each
        // attempt has to start from a clean tracker or an entity modified on the first pass would be saved twice.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Re-read inside the transaction. The read Business decided from was taken outside it, and between
            // the two another request may have decided this proposal — which is exactly the replay case.
            var loaded = await operations.GetForDispositionAsync(operationId, cancellationToken);

            if (loaded?.Proposal is null)
            {
                return new AiDispositionWrite(AiDispositionOutcome.NotFound);
            }

            var operation = loaded.Operation;
            var changes = loaded.Proposal.Changes;

            if (operation.Status is not AiOperationStatus.Proposed)
            {
                return Decided(operation, changes, instruction);
            }

            // Applied first, and the order is load-bearing. The recipe data layer clears the change tracker when
            // it detects a conflict — so dispositions staged before this call would be discarded, and the save
            // below would silently commit a status change with no decisions under it.
            int? versionNumber = null;

            if (instruction.AcceptedChangeIds.Count > 0)
            {
                OperationResult<int?> applied;

                try
                {
                    applied = await applyAccepted(cancellationToken);
                }
                catch
                {
                    // The transaction rolls back on its own, but the aggregate would be left Modified in memory
                    // with mutated content and a new UpdatedAt. Nothing commits it today — no request-scoped save
                    // follows this — and that is exactly the kind of safety that should not depend on the host.
                    // Clearing here makes "nothing was written" a property of this method, the reasoning
                    // RecipeDataLayer.CommitAsync already records for its own conflict path.
                    context.ChangeTracker.Clear();

                    throw;
                }

                if (!applied.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    context.ChangeTracker.Clear();

                    return new AiDispositionWrite(AiDispositionOutcome.ApplyRefused, Error: applied.Error);
                }

                versionNumber = applied.Value;
            }

            var now = clock.UtcNow;

            StampDispositions(changes, instruction, now);

            operation.Status = instruction.Status;
            operation.StatusChangedAt = now;

            // Every status a disposition produces is terminal, so CompletedAt is set here — unlike storing a
            // proposal, where it stays null because the creator has not decided yet.
            operation.CompletedAt = now;

            if (instruction.Feedback is not null)
            {
                operations.AddFeedback(instruction.Feedback);
            }

            auditWriter.Record(instruction.Audit);

            try
            {
                await operations.SaveAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Two dispositions arrived together and the operation's row version decided between them. The
                // loser writes nothing; whether the winner made the same decision is answered by re-reading,
                // which is what the caller does on this outcome.
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();

                return new AiDispositionWrite(AiDispositionOutcome.NotAwaitingDecision);
            }

            await transaction.CommitAsync(cancellationToken);

            return new AiDispositionWrite(AiDispositionOutcome.Applied, versionNumber);
        });
    }

    public async Task<AiDraftAcceptanceWrite> AcceptDraftAsync(
        Guid operationId,
        AiDispositionInstruction instruction,
        Func<CancellationToken, Task<OperationResult<AiCreatedRecipe?>>> createRecipe,
        bool carriesEdits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(createRecipe);

        // The pattern DispositionAsync and IdempotencyDataLayer both use: a retrying execution strategy
        // re-runs the whole unit, so each attempt has to start from a clean tracker.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Re-read inside the transaction. The read Business decided from was taken outside it, and
            // between the two another request may have accepted this draft — which is exactly the replay case.
            var loaded = await operations.GetForDispositionAsync(operationId, cancellationToken);

            if (loaded?.Proposal is null)
            {
                return new AiDraftAcceptanceWrite(AiDispositionOutcome.NotFound);
            }

            var operation = loaded.Operation;
            var changes = loaded.Proposal.Changes;

            if (operation.Status is not AiOperationStatus.Proposed)
            {
                var decided = Decided(operation, changes, instruction);

                if (decided.Outcome is not AiDispositionOutcome.Replayed)
                {
                    return new AiDraftAcceptanceWrite(decided.Outcome);
                }

                // A replay that carried rewrites cannot be proven identical to the one that was recorded:
                // nothing stores what the creator's words were, only that some were theirs. Answering
                // "already done" would discard the words in this request while reporting success, so the
                // retry is refused and the creator is sent to the recipe that exists.
                if (carriesEdits)
                {
                    return new AiDraftAcceptanceWrite(AiDispositionOutcome.NotAwaitingDecision);
                }

                // The recipe the earlier acceptance created, read off the operation it was stamped on. A
                // replay that could not name it would send a creator looking for a recipe they cannot find —
                // and version 1 is named with it, because this seam produces no other version and a null
                // there reads, by this reply's own contract, as "nothing was created".
                return new AiDraftAcceptanceWrite(
                    decided.Outcome,
                    operation.RecipeId,
                    operation.RecipeId is null ? null : 1);
            }

            // Created first, and the order is load-bearing for the reason DispositionAsync records: the recipe
            // data layer clears the change tracker when it refuses, so dispositions staged before this call
            // would be discarded and the save below would commit a status change with no decisions under it.
            //
            // Always invoked. Whether an acceptance creates a recipe is the caller's decision and it has
            // already made it: a rejection hands over a delegate that answers null. Checking an accepted count
            // here would be this layer deciding what an accepted change id means.
            OperationResult<AiCreatedRecipe?> outcome;

            try
            {
                outcome = await createRecipe(cancellationToken);
            }
            catch
            {
                // The transaction rolls back on its own, but the tracker would be left holding a half-built
                // recipe aggregate. Nothing commits it today, and that should not depend on the host.
                context.ChangeTracker.Clear();

                throw;
            }

            if (!outcome.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();

                return new AiDraftAcceptanceWrite(AiDispositionOutcome.ApplyRefused, Error: outcome.Error);
            }

            var created = outcome.Value;

            var now = clock.UtcNow;

            StampDispositions(changes, instruction, now);

            operation.Status = instruction.Status;
            operation.StatusChangedAt = now;
            operation.CompletedAt = now;

            // What makes the replay above answerable, and the only write this seam makes to the operation's
            // own shape. The foreign key is composite and carries WorkspaceId, so an operation cannot end up
            // pointing at a recipe in another workspace even if everything above it were wrong.
            operation.RecipeId = created?.RecipeId;

            if (instruction.Feedback is not null)
            {
                operations.AddFeedback(instruction.Feedback);
            }

            auditWriter.Record(instruction.Audit);

            try
            {
                await operations.SaveAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Two acceptances arrived together and the operation's row version decided between them. The
                // loser writes nothing; whether the winner made the same decision is answered by re-reading.
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();

                return new AiDraftAcceptanceWrite(AiDispositionOutcome.NotAwaitingDecision);
            }

            await transaction.CommitAsync(cancellationToken);

            return new AiDraftAcceptanceWrite(
                AiDispositionOutcome.Applied, created?.RecipeId, created?.VersionNumber);
        });
    }

    public async Task<AiBrandGuideAcceptanceWrite> AcceptBrandGuideProposalAsync(
        Guid operationId,
        AiDispositionInstruction instruction,
        Func<CancellationToken, Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel?>>> createVersion,
        bool carriesEdits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(createVersion);

        // The pattern DispositionAsync, AcceptDraftAsync and IdempotencyDataLayer all use: a retrying execution
        // strategy re-runs the whole unit, so each attempt has to start from a clean tracker.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Re-read inside the transaction. The read Business decided from was taken outside it, and between
            // the two another request may have decided this proposal — which is exactly the replay case.
            var loaded = await operations.GetForDispositionAsync(operationId, cancellationToken);

            if (loaded?.Proposal is null)
            {
                return new AiBrandGuideAcceptanceWrite(AiDispositionOutcome.NotFound);
            }

            var operation = loaded.Operation;
            var changes = loaded.Proposal.Changes;

            if (operation.Status is not AiOperationStatus.Proposed)
            {
                var decided = Decided(operation, changes, instruction);

                // A replay that carried rewrites cannot be proven identical to the one that was recorded, for
                // the reason AcceptDraftAsync records: nothing stores what the creator's words were, only that
                // some were theirs. The retry is refused rather than answered "already done", which would
                // discard the words in this request while reporting success.
                return decided.Outcome is AiDispositionOutcome.Replayed && carriesEdits
                    ? new AiBrandGuideAcceptanceWrite(AiDispositionOutcome.NotAwaitingDecision)
                    : new AiBrandGuideAcceptanceWrite(decided.Outcome);
            }

            // Written first, and the order is load-bearing for the reason DispositionAsync records: a data layer
            // that clears the change tracker when it refuses would discard dispositions staged before the call,
            // and the save below would then commit a status change with no decisions under it.
            //
            // Always invoked. Whether an acceptance writes a version is the caller's decision and it has already
            // made it: a rejection hands over a delegate that answers null. Checking an accepted count here
            // would be this layer deciding what an accepted change id means.
            OperationResult<BrandStyleGuideVersionCreatedServiceModel?> outcome;

            try
            {
                outcome = await createVersion(cancellationToken);
            }
            catch
            {
                // The transaction rolls back on its own, but the tracker would be left holding a half-built
                // guide version. Nothing commits it today, and that should not depend on the host.
                context.ChangeTracker.Clear();

                throw;
            }

            if (!outcome.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();

                return new AiBrandGuideAcceptanceWrite(AiDispositionOutcome.ApplyRefused, Error: outcome.Error);
            }

            var now = clock.UtcNow;

            StampDispositions(changes, instruction, now);

            operation.Status = instruction.Status;
            operation.StatusChangedAt = now;
            operation.CompletedAt = now;

            // Nothing is stamped onto the operation, unlike an accepted draft. There is no RecipeId to set — this
            // task names no recipe — and no guide column to set either, deliberately: see
            // AiBrandGuideAcceptanceWrite's own remarks.
            if (instruction.Feedback is not null)
            {
                operations.AddFeedback(instruction.Feedback);
            }

            auditWriter.Record(instruction.Audit);

            try
            {
                await operations.SaveAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Two acceptances arrived together and the operation's row version decided between them. The
                // loser writes nothing; whether the winner made the same decision is answered by re-reading.
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();

                return new AiBrandGuideAcceptanceWrite(AiDispositionOutcome.NotAwaitingDecision);
            }

            await transaction.CommitAsync(cancellationToken);

            return new AiBrandGuideAcceptanceWrite(AiDispositionOutcome.Applied, outcome.Value);
        });
    }

    /// <summary>
    /// Marks every change of a proposal accepted or rejected, with who decided and when.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every change, not only the accepted ones: a proposal leaves review with nothing still
    /// <see cref="AiChangeDisposition.Pending"/>, because "pending" on a terminal operation would mean nobody
    /// ever decided.
    /// </para>
    /// <para>
    /// All three columns move together and a check constraint says so — a decided change with no actor would
    /// make the record unable to answer who took the creator's own content.
    /// </para>
    /// </remarks>
    private static void StampDispositions(
        IEnumerable<AiStructuredChange> changes, AiDispositionInstruction instruction, DateTimeOffset now)
    {
        foreach (var change in changes)
        {
            change.Disposition = instruction.AcceptedChangeIds.Contains(change.Id)
                ? AiChangeDisposition.Accepted
                : AiChangeDisposition.Rejected;

            change.DecidedAt = now;
            change.DecidedByMembershipId = instruction.DecidedByMembershipId;
        }
    }

    /// <summary>
    /// What to answer when the operation has already left <see cref="AiOperationStatus.Proposed"/>.
    /// </summary>
    /// <remarks>
    /// A retried request that asks for the decision already recorded is a replay and is told so without writing
    /// anything — which is what makes this route safe to retry after a lost response. A request asking for a
    /// <em>different</em> decision is refused: the proposal is terminal, and quietly serving the earlier outcome
    /// would tell a creator their selection had been applied when a different one had.
    /// </remarks>
    private static AiDispositionWrite Decided(
        AiOperation operation,
        IEnumerable<AiStructuredChange> changes,
        AiDispositionInstruction instruction)
    {
        if (operation.Status != instruction.Status)
        {
            return new AiDispositionWrite(AiDispositionOutcome.NotAwaitingDecision);
        }

        var accepted = changes
            .Where(change => change.Disposition is AiChangeDisposition.Accepted)
            .Select(change => change.Id)
            .ToHashSet();

        return accepted.SetEquals(instruction.AcceptedChangeIds)
            ? new AiDispositionWrite(AiDispositionOutcome.Replayed)
            : new AiDispositionWrite(AiDispositionOutcome.NotAwaitingDecision);
    }

    /// <summary>
    /// Whether this worker still holds the operation. Both halves matter: a matching token on a terminal
    /// operation is still not a licence to write, and a running operation with a different token belongs to
    /// someone else.
    /// </summary>
    private static bool HoldsLease(AiOperation operation, Guid leaseToken) =>
        operation.Status is AiOperationStatus.Running && operation.LeasedBy == leaseToken;

    /// <summary>
    /// Stages this operation's execution records, the matching per-account usage entries, and the settlement
    /// of the allowance it was admitted under — on one unit of work, for every write path that settles an
    /// attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One helper rather than three call sites, on purpose.</strong> USAGE-001 makes a settled attempt
    /// that posts no usage a defect and USAGE-004 makes one that settles no allowance another; with the writes
    /// in separate places, each defect is one forgotten line away, and only in the failure path — the one
    /// nobody demonstrates. Here neither is reachable without deleting something.
    /// </para>
    /// <para>
    /// Every part stages and none saves, so the caller's <c>SaveAsync</c> commits the diagnostic record, the
    /// account's ledger and the charge against its allowance together or none of them. See
    /// <see cref="IAiUsageRecordingFacade.StageAttemptsAsync"/> and
    /// <see cref="IAiQuotaAdmissionFacade.StageSettlementAsync"/>.
    /// </para>
    /// <para>
    /// <strong>The settlement is staged even when there are no attempts at all.</strong> A run that failed
    /// before reaching a provider has nothing to bill and still holds allowance, and leaving that hold for the
    /// expiry sweep would strand it for the length of a lease. Zero attempts is the release case, not the
    /// skip case.
    /// </para>
    /// </remarks>
    private async Task RecordAttemptsAsync(
        AiOperation operation,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        Guid? reservationId,
        CancellationToken cancellationToken)
    {
        var records = await MapAttemptsAsync(operation.Id, attempts, cancellationToken);

        operations.AddExecutionRecords(records);

        if (records.Count == 0 && reservationId is null)
        {
            return;
        }

        // The invariant, not a branch: this scope was resolved from the operation's own requester
        // (IWorkspaceResolutionFacade.ResolveForOperationAsync, called by the worker with
        // claim.RequestedByMembershipId), so the two always agree today. It throws rather than charging
        // whoever happens to hold the scope, because a usage entry billed to the wrong account is worse than
        // a failed write: the write is retried, the mis-attribution is not noticed.
        if (workspaceContext.MembershipId != operation.RequestedByMembershipId)
        {
            throw new InvalidOperationException(
                $"AI operation {operation.Id} was requested by a different membership than the one holding "
                    + "this scope; its usage cannot be attributed.");
        }

        var billed = records
            .Select(record => ToUsageAttempt(record, operation.TaskType))
            .ToList();

        if (records.Count > 0)
        {
            var attribution = new AiUsageAttributionServiceModel(
                workspaceContext.AccountId,
                operation.WorkspaceId,
                operation.Id,
                operation.TaskType);

            await usage.StageAttemptsAsync(attribution, billed, cancellationToken);
        }

        if (reservationId is { } held)
        {
            // The same attempt view the ledger was given, so the charge and the record of it cannot disagree
            // about which calls were billable or what they reported.
            await quota.StageSettlementAsync(held, billed, cancellationToken);
        }
    }

    /// <summary>
    /// The counts-and-outcome view of an attempt the ledger takes, built from the execution record so the two
    /// cannot disagree about what happened.
    /// </summary>
    private static AiUsageAttemptServiceModel ToUsageAttempt(AiExecutionMetadata record, AiTaskType taskType) =>
        new()
        {
            AttemptNumber = record.AttemptNumber,

            // The attempt's own end, not the moment this row is written: it is what a quota period is assigned
            // from, and a batch settled together would otherwise all land on one instant.
            OccurredAt = record.CompletedAt,
            ProviderName = record.ProviderName,
            ModelName = record.ModelName,
            ModelDeployment = record.ModelDeployment,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,

            // Null until a gateway reports a provider total. Never input + output: providers report totals
            // that legitimately are not the sum, and a computed one would be indistinguishable from a
            // reported one. See AccountAiUsageEntry.TotalTokens.
            TotalTokens = null,
            EstimatedCost = record.EstimatedCost,

            // Both from AiUsageAttribution rather than from here, because 9A.7's reconciliation posts to the
            // same ledger and has to answer these two questions identically. See that type for why one copy.
            Outcome = AiUsageAttribution.OutcomeOf(record.SafetyBlocked, record.FailureCategory),
            IsBillable = AiUsageAttribution.IsBillable(taskType, record.FailureCategory),
        };

    /// <summary>
    /// Numbers the execution rows continuing from whatever the operation already has, so a requeued operation's
    /// second run does not collide with its first on the unique attempt index.
    /// </summary>
    private async Task<List<AiExecutionMetadata>> MapAttemptsAsync(
        Guid operationId,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        CancellationToken cancellationToken)
    {
        var existing = await operations.CountExecutionAttemptsAsync(operationId, cancellationToken);

        return [.. attempts.Select((attempt, index) => new AiExecutionMetadata
        {
            AiOperationId = operationId,
            AttemptNumber = existing + index + 1,
            ProviderName = attempt.ProviderName,
            ModelName = attempt.ModelName,
            ModelDeployment = attempt.ModelDeployment,
            PromptTemplateId = attempt.PromptTemplateId,
            PromptTemplateVersion = attempt.PromptTemplateVersion,
            StartedAt = attempt.StartedAt,
            CompletedAt = attempt.CompletedAt,
            LatencyMilliseconds = attempt.LatencyMilliseconds,
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,
            EstimatedCost = attempt.EstimatedCost,
            SafetyBlocked = attempt.SafetyBlocked,
            FailureCategory = attempt.FailureCategory,
            FailureSummary = attempt.FailureSummary,
            CorrelationId = attempt.CorrelationId,
        })];
    }
}
