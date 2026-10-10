using System.Diagnostics;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What one pass over the queue did.</summary>
public sealed record AiOperationWorkerPassSummary(int Claimed, int Proposed, int Failed, int Skipped);

/// <summary>What one maintenance sweep did.</summary>
/// <param name="Landed">
/// How many stored proposals of a landing task this sweep re-ran the landing step for. Most are no-ops: the
/// worker lands a proposal as soon as it commits, and this is the retry that heals the gap if it could not.
/// </param>
public sealed record AiOperationMaintenanceSummary(int Requeued, int Abandoned, int Expired, int Landed = 0);

/// <summary>
/// Runs queued AI operations to completion: claims one, resolves and validates the workspace it belongs to,
/// dispatches it to the handler registered for its task type, and stores the outcome.
/// </summary>
/// <remarks>
/// The Worker host's own hosted services call this on a timer; nothing about how it runs is HTTP-shaped, and
/// it is not reached through Facade/Business/DataLayer the way a request is — it is the mechanism that reads
/// and writes an <c>AiOperation</c> outside of one, the same relationship <c>IOutboxDispatcher</c> has to the
/// generic outbox.
/// </remarks>
public interface IAiOperationWorker
{
    /// <summary>Claims and runs every operation due right now, until the queue is empty.</summary>
    Task<AiOperationWorkerPassSummary> RunPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Recovers abandoned leases and expires overdue requests and proposals. Cross-workspace, like the claim
    /// itself — see <see cref="AiOperationClaimRepository"/>.
    /// </summary>
    Task<AiOperationMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiOperationWorker"/>
internal sealed class AiOperationWorker(
    AiOperationClaimRepository claims,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<AiOperationWorker> logger) : IAiOperationWorker
{
    private enum ClaimOutcome { Proposed, Failed, Skipped }

    public async Task<AiOperationWorkerPassSummary> RunPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = 0;
        var proposed = 0;
        var failed = 0;
        var skipped = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Its own scope, on the DbContext this instance already holds -- unresolved, and deliberately so:
            // the claim step is cross-workspace and runs before any workspace is known.
            var claim = await claims.ClaimNextAsync(Guid.NewGuid(), clock.UtcNow, cancellationToken);
            if (claim is null)
            {
                break;
            }

            claimed++;

            // A fresh scope per claimed operation: IWorkspaceContextResolver.Resolve is one-shot per scope, and
            // two operations in one pass may belong to two different workspaces.
            await using var scope = scopeFactory.CreateAsyncScope();

            try
            {
                switch (await RunClaimedAsync(scope.ServiceProvider, claim, cancellationToken))
                {
                    case ClaimOutcome.Proposed:
                        proposed++;
                        break;
                    case ClaimOutcome.Failed:
                        failed++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not written as a Failed operation: this is the worker's own fault, not the task's, and
                // AiFailureCategory has nothing that means "the worker crashed". The lease is left to expire --
                // RunMaintenanceAsync's RecoverAbandonedLeasesAsync is the backstop, bounded by
                // AiPolicy.MaxAttempts like any other worker crash, restart, or duplicate delivery.
                logger.LogError(
                    ex,
                    "AI operation {OperationId} failed unexpectedly; its lease will expire and be recovered.",
                    claim.OperationId);
                skipped++;
            }
        }

        return new AiOperationWorkerPassSummary(claimed, proposed, failed, skipped);
    }

    private async Task<ClaimOutcome> RunClaimedAsync(
        IServiceProvider scoped, AiOperationClaim claim, CancellationToken cancellationToken)
    {
        var resolution = scoped.GetRequiredService<IWorkspaceResolutionFacade>();
        var resolved = await resolution.ResolveForOperationAsync(
            claim.WorkspaceId, claim.RequestedByMembershipId, cancellationToken);

        if (!resolved.Succeeded)
        {
            // The workspace, or the exact membership that requested this, no longer resolves -- in practice
            // only a workspace deleted after the request was queued. Nothing can be written: the operation row
            // itself is workspace-owned and cannot be read without a resolved context. Left to the same
            // lease-expiry backstop as an unexpected exception.
            logger.LogWarning(
                "AI operation {OperationId}'s workspace could not be resolved; its lease will expire and be recovered.",
                claim.OperationId);
            return ClaimOutcome.Skipped;
        }

        // The ambient IWorkspaceContext is already resolved: ResolveForOperationAsync does that itself as
        // part of succeeding, because IWorkspaceContextResolver is not a facade type this module may cross
        // to reach directly (ModuleBoundaryTests).
        var operations = scoped.GetRequiredService<IAiOperationDataLayer>();
        var loaded = await operations.GetWithProposalAsync(claim.OperationId, cancellationToken);

        if (loaded is null)
        {
            // Claimed a moment ago; not reachable in practice, since operations are never deleted. Answered the
            // same conservative way as any other vanished row.
            return ClaimOutcome.Skipped;
        }

        var operation = loaded.Operation;
        var handler = scoped.GetKeyedService<IAiTaskHandler>(operation.TaskType);

        if (handler is null)
        {
            // A deployment/config mismatch -- a task type enabled with no handler registered for it. Retrying
            // will not fix it, so it is failed outright rather than left to exhaust the lease-recovery bound.
            await operations.FailAsync(
                claim.OperationId,
                claim.LeaseToken,
                AiFailureCategory.TemplateUnavailable,
                $"No handler is registered for task type '{operation.TaskType}'.",
                [],
                null,
                cancellationToken);
            return ClaimOutcome.Failed;
        }

        // Before the handler, because the handler is what calls a provider (USAGE-006). This is the
        // authoritative check even though 9A.6 refuses at request time too: an account can be suspended, or
        // spend the last of its allowance on another run, in the time a request waits in the queue.
        var quota = scoped.GetRequiredService<IAiQuotaAdmissionFacade>();
        var admission = await AdmitAsync(scoped, quota, claim, operation, cancellationToken);

        if (admission.Outcome is AiQuotaAdmissionOutcome.Busy)
        {
            // Nothing is known about the balance -- competing writers simply kept the period moving. Failing
            // the run would report an exhausted allowance that was never measured, so the lease is left to
            // lapse and maintenance requeues it with backoff, bounded like any other requeue.
            logger.LogInformation(
                "AI operation {OperationId} could not be admitted against a contended quota period; its lease "
                    + "will expire and be recovered.",
                claim.OperationId);

            return ClaimOutcome.Skipped;
        }

        if (admission.Outcome is AiQuotaAdmissionOutcome.Exhausted or AiQuotaAdmissionOutcome.Suspended)
        {
            // Terminal, not requeued. A requeue would burn the attempt bound and then report LeaseAbandoned,
            // which names the wrong reason -- and would re-ask a question whose answer does not change until
            // the period resets. No provider was called, so there are no attempts and nothing to bill.
            var refusal = await operations.FailAsync(
                claim.OperationId,
                claim.LeaseToken,
                admission.Outcome is AiQuotaAdmissionOutcome.Suspended
                    ? AiFailureCategory.AccountSuspended
                    : AiFailureCategory.Quota,
                admission.Outcome is AiQuotaAdmissionOutcome.Suspended
                    ? "AI access is switched off for this account."
                    : "This account's AI allowance for the current period is spent.",
                [],
                null,
                cancellationToken);

            return refusal == AiOperationWriteOutcome.Applied ? ClaimOutcome.Failed : ClaimOutcome.Skipped;
        }

        var reservationId = admission.ReservationId;

        var context = new AiTaskExecutionContext(
            claim.OperationId,
            resolved.Value!.WorkspaceId,
            claim.LeaseToken,
            operation.Scope,
            operation.RecipeId,
            operation.RecipeVersionId,
            CorrelationId(),
            ct => RenewAsync(operations, quota, claim, reservationId, ct),
            DeserializeInputs(operation.TaskInputsJson));

        var outcome = await handler.HandleAsync(context, cancellationToken);

        AiOperationWriteOutcome write;

        if (outcome.Succeeded)
        {
            write = await operations.StoreProposalAsync(
                claim.OperationId,
                claim.LeaseToken,
                outcome.Proposal!,
                outcome.Attempts,
                reservationId,
                cancellationToken);
        }
        else
        {
            write = await operations.FailAsync(
                claim.OperationId,
                claim.LeaseToken,
                outcome.FailureCategory ?? AiFailureCategory.Provider,
                outcome.FailureSummary,
                outcome.Attempts,
                reservationId,
                cancellationToken);
        }

        // LeaseLost means another worker's recovery pass already reclaimed this while the handler ran -- that
        // worker's own outcome is authoritative, so this one is dropped rather than retried. The settlement
        // went with it: nothing was written at all, and the hold lapses with the lease that lost it.
        if (write != AiOperationWriteOutcome.Applied)
        {
            return ClaimOutcome.Skipped;
        }

        // The settlement has committed; this is what moves it onto the period. Deliberately after the commit
        // and outside its transaction -- see AccountAiQuotaReservation for why -- and deliberately not fatal:
        // the maintenance sweep finds anything this does not.
        if (reservationId is { } held)
        {
            await PostAsync(quota, held, claim.OperationId, cancellationToken);
        }

        // After the commit, and it has to be: a landing writes rows that name the proposal, and the proposal's
        // id exists only once it is stored. Not fatal either, and the sweep re-lands what this could not --
        // see IAiProposalLandingHandler.
        if (outcome.Succeeded)
        {
            await LandAsync(scoped, operation.TaskType, claim.OperationId, cancellationToken);
        }

        return outcome.Succeeded ? ClaimOutcome.Proposed : ClaimOutcome.Failed;
    }

    /// <summary>
    /// Admits the run against its account's allowance, or says why not.
    /// </summary>
    /// <remarks>
    /// The account comes from the resolved workspace context, which the worker resolved from this operation's
    /// own requesting membership — never from the operation row, a request field, or anything a model supplied
    /// (USAGE-001). <c>IsMeterable</c> is decided here because this module is the one that knows a diagnostic
    /// task never reaches a provider; the quota module is told, and does not infer.
    /// </remarks>
    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        IServiceProvider scoped,
        IAiQuotaAdmissionFacade quota,
        AiOperationClaim claim,
        AiOperation operation,
        CancellationToken cancellationToken)
    {
        var accountId = scoped.GetRequiredService<IWorkspaceContext>().AccountId;

        return await quota.AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                accountId,
                claim.OperationId,
                claim.LeaseToken,
                operation.TaskType,
                operation.TaskType is not AiTaskType.Diagnostic,
                operation.LeaseExpiresAt ?? clock.UtcNow + AiPolicy.LeaseDuration),
            cancellationToken);
    }

    /// <summary>
    /// Extends the lease and the hold together, so the run cannot outlive the allowance it is spending.
    /// </summary>
    /// <remarks>
    /// The hold is extended only once the lease actually was: renewing it against a lease this worker has
    /// already lost would keep allowance held for a run whose writes will all be refused.
    /// </remarks>
    private async Task RenewAsync(
        IAiOperationDataLayer operations,
        IAiQuotaAdmissionFacade quota,
        AiOperationClaim claim,
        Guid? reservationId,
        CancellationToken cancellationToken)
    {
        var renewed = await operations.RenewLeaseAsync(claim.OperationId, claim.LeaseToken, cancellationToken);

        if (renewed is AiOperationWriteOutcome.Applied && reservationId is { } held)
        {
            await quota.RenewAsync(held, clock.UtcNow + AiPolicy.LeaseDuration, cancellationToken);
        }
    }

    /// <remarks>
    /// Logged and swallowed rather than thrown. The charge is already durably recorded on the reservation, so
    /// failing the run here would report a settled attempt as a worker crash and requeue work that has already
    /// been done and paid for. <c>IAiQuotaMaintenanceFacade</c> applies what this could not.
    /// </remarks>
    private async Task PostAsync(
        IAiQuotaAdmissionFacade quota,
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await quota.PostAsync(reservationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "AI operation {OperationId} settled but its allowance was not applied to the period; the "
                    + "maintenance sweep will apply it.",
                operationId);
        }
    }

    public async Task<AiOperationMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var (requeued, abandoned) = await claims.RecoverAbandonedLeasesAsync(now, cancellationToken);
        var expired = await claims.ExpireDueAsync(now, cancellationToken);
        var landed = await RelandAsync(now, cancellationToken);

        return new AiOperationMaintenanceSummary(requeued, abandoned, expired, landed);
    }

    /// <summary>
    /// Re-runs the landing step for recently stored proposals of a landing task.
    /// </summary>
    /// <remarks>
    /// Almost always a no-op, and that is the design: landing is idempotent, so this costs a question and
    /// answers it with "already done" for every proposal the worker landed itself. What it buys is the one
    /// case that would otherwise be lost for good — a worker that stored a proposal and died before landing
    /// it. Each candidate gets its own scope and its own resolved workspace, exactly like a claimed run.
    /// </remarks>
    private async Task<int> RelandAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await claims.FindProposalsToLandAsync(
            AiTaskCatalog.LandedTasks, now - AiPolicy.ProposalLandingWindow, cancellationToken);
        var landed = 0;

        foreach (var candidate in candidates)
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            try
            {
                var resolution = scope.ServiceProvider.GetRequiredService<IWorkspaceResolutionFacade>();
                var resolved = await resolution.ResolveForOperationAsync(
                    candidate.WorkspaceId, candidate.RequestedByMembershipId, cancellationToken);

                if (!resolved.Succeeded)
                {
                    // In practice a workspace deleted since the proposal was stored. Logged rather than
                    // passed over in silence: this is the one path on which a proposal that never landed
                    // stops being recoverable, and nothing else would say so.
                    logger.LogWarning(
                        "AI operation {OperationId}'s workspace could not be resolved, so the records its "
                            + "proposal produces cannot be written.",
                        candidate.OperationId);

                    continue;
                }

                // The task type is read back inside the resolved workspace rather than carried out of the
                // cross-workspace query: a landing handler is keyed by it, and the candidate row is allowed to
                // carry identifiers only.
                var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
                var operation = await operations.GetWithProposalAsync(candidate.OperationId, cancellationToken);

                if (operation is null)
                {
                    continue;
                }

                await LandAsync(
                    scope.ServiceProvider, operation.Operation.TaskType, candidate.OperationId, cancellationToken);
                landed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "The landing step for AI operation {OperationId} could not be re-run.", candidate.OperationId);
            }
        }

        return landed;
    }

    /// <summary>
    /// Runs the landing step registered for a task type, if there is one.
    /// </summary>
    /// <remarks>
    /// Logged and swallowed, the same reasoning <see cref="PostAsync"/> records: the proposal is stored and
    /// paid for, so reporting a failed landing as a crashed run would requeue work that has already been done
    /// and spend the allowance again. The creator can read the proposal either way.
    /// </remarks>
    private async Task LandAsync(
        IServiceProvider scoped, AiTaskType taskType, Guid operationId, CancellationToken cancellationToken)
    {
        var landing = scoped.GetKeyedService<IAiProposalLandingHandler>(taskType);

        if (landing is null)
        {
            return;
        }

        try
        {
            await landing.LandAsync(operationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "AI operation {OperationId} stored its proposal but the records it produces were not written; "
                    + "the maintenance sweep will try again.",
                operationId);
        }
    }

    /// <summary>
    /// An operation's stored brief, as the declared-field dictionary a handler reads. Written once by
    /// Business at request time and never by the model; a row with none stored (a recipe-bound task, or one
    /// that declares no fields) reads as no inputs at all rather than an empty one.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? DeserializeInputs(string? taskInputsJson) =>
        taskInputsJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(taskInputsJson);

    /// <summary>
    /// The ambient request's trace id, as a <see cref="Guid"/> -- the same reasoning
    /// <c>AiProposalBusiness.CorrelationId</c> records. A new id when there is no activity, so a run outside a
    /// traced request is at least correlatable with itself.
    /// </summary>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default
            ? Guid.ParseExact(id.ToHexString(), "N")
            : Guid.NewGuid();
    }
}
