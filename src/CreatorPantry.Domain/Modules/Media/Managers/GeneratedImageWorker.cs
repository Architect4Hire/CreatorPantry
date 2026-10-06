using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>Recovers lapsed leases: requeued with a backoff, or settled once attempts run out.</summary>
    Task<GeneratedImageMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageWorker"/>
internal sealed class GeneratedImageWorker(
    GeneratedImageClaimRepository claims,
    IServiceScopeFactory scopeFactory,
    IClock clock,
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
            var claim = await claims.ClaimNextAsync(Guid.NewGuid(), clock.UtcNow, cancellationToken);

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
        var (requeued, abandoned) = await claims.RecoverAbandonedLeasesAsync(clock.UtcNow, cancellationToken);

        return new GeneratedImageMaintenanceSummary(requeued, abandoned);
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
