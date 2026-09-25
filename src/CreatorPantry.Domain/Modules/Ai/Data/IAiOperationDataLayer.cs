using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
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

    Task<AiOperationWriteOutcome> StoreProposalAsync(
        Guid operationId,
        Guid leaseToken,
        AiProposal proposal,
        IReadOnlyCollection<AiAttemptRecord> attempts,
        CancellationToken cancellationToken);

    Task<AiOperationWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        AiFailureCategory category,
        string? summary,
        IReadOnlyCollection<AiAttemptRecord> attempts,
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
}

/// <inheritdoc cref="IAiOperationDataLayer"/>
internal sealed class AiOperationDataLayer(
    CreatorPantryDbContext context,
    IAiOperationRepository operations,
    IAuditWriter auditWriter,
    IClock clock) : IAiOperationDataLayer
{
    public async Task<AiOperationRequest> RequestAsync(
        AiOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

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
        operations.AddExecutionRecords(await MapAttemptsAsync(operationId, attempts, cancellationToken));

        // One SaveChanges over every pending entity on one context is already one transaction — the reasoning
        // IRecipeDataLayer records, including why an explicit BeginTransactionAsync would be worse under the
        // retrying execution strategy. The proposal, its changes, its warnings, the execution rows and the
        // status change commit together or not at all.
        await operations.SaveAsync(cancellationToken);

        return AiOperationWriteOutcome.Applied;
    }

    public async Task<AiOperationWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        AiFailureCategory category,
        string? summary,
        IReadOnlyCollection<AiAttemptRecord> attempts,
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

        operations.AddExecutionRecords(await MapAttemptsAsync(operationId, attempts, cancellationToken));

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

            foreach (var change in changes)
            {
                change.Disposition = instruction.AcceptedChangeIds.Contains(change.Id)
                    ? AiChangeDisposition.Accepted
                    : AiChangeDisposition.Rejected;

                // All three columns move together, and a check constraint says so. A decided change with no
                // actor would make the record unable to answer who took the creator's own content.
                change.DecidedAt = now;
                change.DecidedByMembershipId = instruction.DecidedByMembershipId;
            }

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
