using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>What one pass over the generation queue did.</summary>
public sealed record GeneratedImagePassSummary(
    int Claimed, int Generated, int PartiallyGenerated, int Requeued, int Failed, int Cancelled, int Skipped);

/// <summary>What one maintenance sweep did.</summary>
public sealed record GeneratedImageMaintenanceSummary(int Requeued, int Abandoned);

/// <summary>
/// Runs queued image generations to completion: claims one, resolves and validates the workspace it belongs
/// to, and hands it to the facade. The mechanism that claims and settles an operation outside a request.
/// </summary>
public interface IGeneratedImageWorker
{
    /// <summary>Claims and runs every generation due right now, until the queue is empty.</summary>
    Task<GeneratedImagePassSummary> RunPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Recovers lapsed leases: requeued with a backoff, or — once attempts run out — settled, charging the
    /// pictures already made (DL.7).
    /// </summary>
    Task<GeneratedImageMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageWorker"/>
internal sealed class GeneratedImageWorker(
    GeneratedImageClaimRepository claims,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<LimitsOptions> limits,
    ILogger<GeneratedImageWorker> logger) : IGeneratedImageWorker
{
    public async Task<GeneratedImagePassSummary> RunPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = 0;
        var generated = 0;
        var partial = 0;
        var requeued = 0;
        var failed = 0;
        var cancelled = 0;
        var skipped = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var claim = await claims.ClaimNextAsync(
                Guid.NewGuid(),
                clock.UtcNow,
                cancellationToken,
                limits.Value.Enabled ? limits.Value.ImageRunningPerWorkspace : int.MaxValue,
                runningPerAccount: limits.Value.Enabled);

            if (claim is null)
            {
                break;
            }

            claimed++;

            // A fresh scope per claimed operation: workspace resolution is one-shot per scope, and two
            // operations in one pass may belong to two different workspaces.
            await using var scope = scopeFactory.CreateAsyncScope();

            try
            {
                switch (await RunClaimedAsync(scope.ServiceProvider, claim, cancellationToken))
                {
                    case GeneratedImageRunOutcome.Generated:
                        generated++;
                        break;
                    case GeneratedImageRunOutcome.PartiallyGenerated:
                        partial++;
                        break;
                    case GeneratedImageRunOutcome.Requeued:
                        requeued++;
                        break;
                    case GeneratedImageRunOutcome.Failed:
                        failed++;
                        break;
                    case GeneratedImageRunOutcome.Cancelled:
                        cancelled++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The facade already released the lease uncharged on its way out. Stop the pass.
                cancelled++;

                break;
            }
            catch (Exception exception)
            {
                // Not recorded as a failed operation: this is the worker's own fault rather than a fact
                // about the request. The lease is left to expire and RunMaintenanceAsync recovers it,
                // bounded by MediaPolicy.GenerationMaxAttempts — which is what stops a poison request
                // cycling, and every cycle here is a paid provider call. Logged by exception type and
                // operation id only: the message can quote a prompt, and ai.md keeps those out of logs.
                logger.LogError(
                    "Generation {OperationId} failed unexpectedly ({ExceptionType}); its lease will expire and be recovered.",
                    claim.OperationId,
                    exception.GetType().Name);

                skipped++;
            }
        }

        return new GeneratedImagePassSummary(
            claimed, generated, partial, requeued, failed, cancelled, skipped);
    }

    private async Task<GeneratedImageRunOutcome> RunClaimedAsync(
        IServiceProvider scoped, GeneratedImageClaim claim, CancellationToken cancellationToken)
    {
        if (!await ResolveAsync(scoped, claim.WorkspaceId, cancellationToken))
        {
            logger.LogWarning(
                "Generation {OperationId}'s workspace could not be resolved; its lease will expire and be recovered.",
                claim.OperationId);

            return GeneratedImageRunOutcome.Skipped;
        }

        return await scoped.GetRequiredService<IGeneratedImageGenerationFacade>()
            .ExecuteAsync(claim.OperationId, claim.LeaseToken, cancellationToken);
    }

    public Task<GeneratedImageMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken) =>
        RecoverAsync(cancellationToken);

    private async Task<GeneratedImageMaintenanceSummary> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovery = await claims.RecoverAbandonedLeasesAsync(clock.UtcNow, cancellationToken);
        var abandoned = 0;

        foreach (var claim in recovery.Exhausted)
        {
            if (await SettleAbandonedAsync(claim, cancellationToken))
            {
                abandoned++;
            }
        }

        return new GeneratedImageMaintenanceSummary(recovery.Requeued, abandoned);
    }

    /// <summary>
    /// Ends one generation that ran out of attempts, charging what it made (DL.7) — or, once its hold has lapsed
    /// and it still cannot be settled, ending it uncharged so no sweep leases it forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved like a run, in a scope of its own: the charge is AiUsage's, reached through Media's facade under
    /// the operation's own workspace, and the account it lands on is read from the hold rather than from this
    /// service identity.
    /// </para>
    /// <para>
    /// <strong>A settlement that fails is tried again rather than given up.</strong> Ending the operation
    /// uncharged on a passing database error would forgo pictures the provider billed for. So while the hold can
    /// still be charged, the settlement lease is left to lapse and the next sweep hands the operation out again;
    /// once the hold has lapsed, settling would charge nothing, and ending it uncharged costs nothing. That is
    /// also the bound: a hold lasts <see cref="MediaPolicy.GenerationHoldDuration"/>.
    /// </para>
    /// </remarks>
    /// <returns>Whether the operation was ended by this sweep.</returns>
    private async Task<bool> SettleAbandonedAsync(
        GeneratedImageSettlementClaim claim, CancellationToken cancellationToken)
    {
        var resolved = false;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            resolved = await ResolveAsync(scope.ServiceProvider, claim.WorkspaceId, cancellationToken);

            if (resolved)
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
                    .SettleAbandonedAsync(claim.OperationId, claim.LeaseToken, cancellationToken);

                if (outcome is GeneratedImageRunOutcome.PartiallyGenerated or GeneratedImageRunOutcome.Failed)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Abandoned generation {OperationId} could not be settled ({ExceptionType}).",
                claim.OperationId,
                exception.GetType().Name);
        }

        // Nothing is written against a workspace this worker could not resolve. Finding the row is not
        // authorization (tenancy.md's queue-claim carve-out), and the claim gave this sweep identifiers
        // only — so a workspace that will not resolve is left alone and said out loud, rather than ended
        // by a write that skipped the check every other path makes.
        if (!resolved)
        {
            logger.LogWarning(
                "Abandoned generation {OperationId} was left alone: its workspace {WorkspaceId} could not be resolved.",
                claim.OperationId,
                claim.WorkspaceId);

            return false;
        }

        if (clock.UtcNow < claim.HoldLapsesAt)
        {
            logger.LogWarning(
                "Abandoned generation {OperationId} is left for the next sweep to settle while its hold stands.",
                claim.OperationId);

            return false;
        }

        // Guarded by the settlement lease, so an operation a failed-looking settle did in fact end is left alone.
        var ended = await claims.AbandonUnsettledAsync(
            claim.OperationId, claim.LeaseToken, clock.UtcNow, cancellationToken);

        if (ended)
        {
            logger.LogWarning(
                "Abandoned generation {OperationId} was ended without settling; its hold had already lapsed.",
                claim.OperationId);
        }

        return ended;
    }

    /// <summary>
    /// ResolveForServiceAsync, not an operation resolution: a generation a creator paid for must still
    /// finish when that creator has since left the workspace. It resolves the ambient workspace context as
    /// part of succeeding, so every read and write after it is filtered by it — which is what makes the
    /// identifiers-only claim above safe (tenancy.md).
    /// </summary>
    private static async Task<bool> ResolveAsync(
        IServiceProvider scoped, Guid workspaceId, CancellationToken cancellationToken) =>
        (await scoped.GetRequiredService<IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(workspaceId, cancellationToken)).Succeeded;
}
