using System.Diagnostics;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What one pass over the queue did.</summary>
public sealed record AiOperationWorkerPassSummary(int Claimed, int Proposed, int Failed, int Skipped);

/// <summary>What one maintenance sweep did.</summary>
public sealed record AiOperationMaintenanceSummary(int Requeued, int Abandoned, int Expired);

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
                cancellationToken);
            return ClaimOutcome.Failed;
        }

        var context = new AiTaskExecutionContext(
            claim.OperationId,
            resolved.Value!.WorkspaceId,
            claim.LeaseToken,
            operation.Scope,
            operation.RecipeId,
            operation.RecipeVersionId,
            CorrelationId(),
            ct => operations.RenewLeaseAsync(claim.OperationId, claim.LeaseToken, ct),
            DeserializeInputs(operation.TaskInputsJson));

        var outcome = await handler.HandleAsync(context, cancellationToken);

        if (outcome.Succeeded)
        {
            var write = await operations.StoreProposalAsync(
                claim.OperationId, claim.LeaseToken, outcome.Proposal!, outcome.Attempts, cancellationToken);

            // LeaseLost means another worker's recovery pass already reclaimed this while the handler ran --
            // that worker's own outcome is authoritative, so this one is dropped rather than retried.
            return write == AiOperationWriteOutcome.Applied ? ClaimOutcome.Proposed : ClaimOutcome.Skipped;
        }

        var failWrite = await operations.FailAsync(
            claim.OperationId,
            claim.LeaseToken,
            outcome.FailureCategory ?? AiFailureCategory.Provider,
            outcome.FailureSummary,
            outcome.Attempts,
            cancellationToken);

        return failWrite == AiOperationWriteOutcome.Applied ? ClaimOutcome.Failed : ClaimOutcome.Skipped;
    }

    public async Task<AiOperationMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var (requeued, abandoned) = await claims.RecoverAbandonedLeasesAsync(now, cancellationToken);
        var expired = await claims.ExpireDueAsync(now, cancellationToken);

        return new AiOperationMaintenanceSummary(requeued, abandoned, expired);
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
