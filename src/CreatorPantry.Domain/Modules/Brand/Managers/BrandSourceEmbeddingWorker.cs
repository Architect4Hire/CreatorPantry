using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>What one pass over the embedding queue did.</summary>
public sealed record BrandSourceEmbeddingPassSummary(
    int Claimed, int Embedded, int Requeued, int Failed, int Cancelled, int Skipped);

/// <summary>What one maintenance sweep did.</summary>
public sealed record BrandSourceEmbeddingMaintenanceSummary(
    int Requeued, int Abandoned, int Retired, int Enqueued);

/// <summary>
/// Runs queued embeddings to completion: claims one, resolves and validates the workspace it belongs to, and
/// hands it to the facade. The mechanism that claims and settles an operation outside a request, as
/// <see cref="IBrandSourceExtractionWorker"/> is for extraction.
/// </summary>
public interface IBrandSourceEmbeddingWorker
{
    /// <summary>Claims and runs every embedding due right now, until the queue is empty.</summary>
    Task<BrandSourceEmbeddingPassSummary> RunPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Recovers lapsed leases, deletes retired and abandoned chunk sets, and queues re-embedding for any
    /// artifact the configured deployment has not embedded.
    /// </summary>
    Task<BrandSourceEmbeddingMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandSourceEmbeddingWorker"/>
internal sealed class BrandSourceEmbeddingWorker(
    BrandSourceEmbeddingClaimRepository claims,
    IBrandSourceEmbeddingGateway provider,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<BrandSourceEmbeddingWorker> logger) : IBrandSourceEmbeddingWorker
{
    public async Task<BrandSourceEmbeddingPassSummary> RunPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = 0;
        var embedded = 0;
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
                    case BrandSourceEmbeddingRunOutcome.Embedded:
                        embedded++;
                        break;
                    case BrandSourceEmbeddingRunOutcome.Requeued:
                        requeued++;
                        break;
                    case BrandSourceEmbeddingRunOutcome.Failed:
                        failed++;
                        break;
                    case BrandSourceEmbeddingRunOutcome.Cancelled:
                        cancelled++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Not recorded as a failed operation: this is the worker's own fault rather than a fact about
                // the document. The lease is left to expire and RunMaintenanceAsync recovers it, bounded by
                // BrandPolicy.EmbeddingMaxAttempts — which is what stops a poison document cycling. Logged by
                // exception type and operation id only, never the exception itself: its message can quote a
                // passage, and ai.md keeps creator content out of logs.
                logger.LogError(
                    "Embedding {OperationId} failed unexpectedly ({ExceptionType}); its lease will expire and be recovered.",
                    claim.OperationId,
                    exception.GetType().Name);

                skipped++;
            }
        }

        return new BrandSourceEmbeddingPassSummary(claimed, embedded, requeued, failed, cancelled, skipped);
    }

    private async Task<BrandSourceEmbeddingRunOutcome> RunClaimedAsync(
        IServiceProvider scoped, BrandSourceEmbeddingClaim claim, CancellationToken cancellationToken)
    {
        if (!await ResolveAsync(scoped, claim.WorkspaceId, cancellationToken))
        {
            logger.LogWarning(
                "Embedding {OperationId}'s workspace could not be resolved; its lease will expire and be recovered.",
                claim.OperationId);

            return BrandSourceEmbeddingRunOutcome.Skipped;
        }

        return await scoped.GetRequiredService<IBrandSourceEmbeddingFacade>()
            .ExecuteAsync(claim.OperationId, claim.LeaseToken, cancellationToken);
    }

    public async Task<BrandSourceEmbeddingMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var (requeued, abandoned) = await claims.RecoverAbandonedLeasesAsync(now, cancellationToken);

        var retired = 0;

        foreach (var due in await claims.FindRetirableSetsAsync(now, cancellationToken))
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            if (await ResolveAsync(scope.ServiceProvider, due.WorkspaceId, cancellationToken)
                && await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingFacade>()
                    .RetireSetAsync(due.SetId, cancellationToken))
            {
                retired++;
            }
        }

        var enqueued = 0;
        var model = provider.Describe();

        // Without an identified deployment there is no model to compare against, and queueing work now would
        // only produce operations that fail for it.
        if (model.Status is BrandSourceEmbeddingModelStatus.Identified)
        {
            foreach (var missing in await claims.FindBackfillAsync(model.ModelId!, cancellationToken))
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                if (await ResolveAsync(scope.ServiceProvider, missing.WorkspaceId, cancellationToken)
                    && await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingFacade>()
                        .EnqueueAsync(missing.ExtractionId, cancellationToken))
                {
                    enqueued++;
                }
            }
        }

        return new BrandSourceEmbeddingMaintenanceSummary(requeued, abandoned, retired, enqueued);
    }

    /// <summary>
    /// ResolveForServiceAsync, not an operation resolution: an embedding must still run when the member who
    /// uploaded the file has since left. It resolves the ambient workspace context as part of succeeding, so
    /// every read after it is filtered by it.
    /// </summary>
    private static async Task<bool> ResolveAsync(
        IServiceProvider scoped, Guid workspaceId, CancellationToken cancellationToken) =>
        (await scoped.GetRequiredService<IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(workspaceId, cancellationToken)).Succeeded;
}
