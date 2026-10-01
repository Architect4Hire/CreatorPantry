using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>What one pass over the extraction queue did.</summary>
public sealed record BrandSourceExtractionPassSummary(
    int Claimed, int Extracted, int Reviewed, int Requeued, int Failed, int Cancelled, int Skipped);

/// <summary>What one maintenance sweep did.</summary>
public sealed record BrandSourceExtractionMaintenanceSummary(int Requeued, int Abandoned);

/// <summary>
/// Runs queued document extractions to completion: claims one, resolves and validates the workspace it belongs
/// to, and hands it to the facade.
/// </summary>
/// <remarks>
/// The Worker host's hosted services call this on a timer. Nothing about how it runs is HTTP-shaped, and it is
/// not reached through Facade/Business/DataLayer the way a request is — it is the mechanism that claims and
/// settles an operation outside of one, the same relationship <c>IAiOperationWorker</c> has to the AI queue.
/// </remarks>
public interface IBrandSourceExtractionWorker
{
    /// <summary>Claims and runs every extraction due right now, until the queue is empty.</summary>
    Task<BrandSourceExtractionPassSummary> RunPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Recovers leases abandoned by a worker that died holding one. Cross-workspace, like the claim itself —
    /// see <see cref="BrandSourceExtractionClaimRepository"/>.
    /// </summary>
    Task<BrandSourceExtractionMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandSourceExtractionWorker"/>
internal sealed class BrandSourceExtractionWorker(
    BrandSourceExtractionClaimRepository claims,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<BrandSourceExtractionWorker> logger) : IBrandSourceExtractionWorker
{
    public async Task<BrandSourceExtractionPassSummary> RunPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = 0;
        var extracted = 0;
        var reviewed = 0;
        var requeued = 0;
        var failed = 0;
        var cancelled = 0;
        var skipped = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Its own scope, on the DbContext this instance already holds — unresolved, and deliberately so:
            // the claim step is cross-workspace and runs before any workspace is known.
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
                    case BrandSourceExtractionRunOutcome.Extracted:
                        extracted++;
                        break;
                    case BrandSourceExtractionRunOutcome.Reviewed:
                        reviewed++;
                        break;
                    case BrandSourceExtractionRunOutcome.Requeued:
                        requeued++;
                        break;
                    case BrandSourceExtractionRunOutcome.Failed:
                        failed++;
                        break;
                    case BrandSourceExtractionRunOutcome.Cancelled:
                        cancelled++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not recorded as a failed operation: this is the worker's or a parser's own fault rather than a
                // fact about the document, and the failure categories deliberately have no word for "the worker
                // crashed". The lease is left to expire — RunMaintenanceAsync is the backstop, bounded by
                // BrandPolicy.ExtractionMaxAttempts like any other crash, restart or duplicate delivery. That
                // bound is what makes a poison document stop instead of cycling.
                //
                // The exception is logged and the document is not: a parser's complaint can quote the bytes it
                // choked on, and the operation id is the handle an operator needs (ai.md).
                logger.LogError(
                    exception,
                    "Extraction {OperationId} failed unexpectedly; its lease will expire and be recovered.",
                    claim.OperationId);

                skipped++;
            }
        }

        return new BrandSourceExtractionPassSummary(
            claimed, extracted, reviewed, requeued, failed, cancelled, skipped);
    }

    private async Task<BrandSourceExtractionRunOutcome> RunClaimedAsync(
        IServiceProvider scoped, BrandSourceExtractionClaim claim, CancellationToken cancellationToken)
    {
        var resolution = scoped.GetRequiredService<IWorkspaceResolutionFacade>();

        // ResolveForServiceAsync, not ResolveForOperationAsync: an extraction must still run when the member who
        // uploaded the file has since left the workspace. Nothing about reading a document as text depends on a
        // person, and tying it to one would leave documents permanently unextracted after a departure.
        var resolved = await resolution.ResolveForServiceAsync(claim.WorkspaceId, cancellationToken);

        if (!resolved.Succeeded)
        {
            // In practice only a workspace deleted after the version committed. Nothing can be written: the
            // operation row is workspace-owned and cannot be read without a resolved context. Left to the same
            // lease-expiry backstop as an unexpected exception.
            logger.LogWarning(
                "Extraction {OperationId}'s workspace could not be resolved; its lease will expire and be recovered.",
                claim.OperationId);

            return BrandSourceExtractionRunOutcome.Skipped;
        }

        // The ambient IWorkspaceContext is already resolved: ResolveForServiceAsync does that itself as part of
        // succeeding, because IWorkspaceContextResolver is not a facade type this module may cross to reach
        // directly (ModuleBoundaryTests). From here on every read is filtered by it — including the read of the
        // operation row itself, which is why a claim that named another workspace's work would find nothing.
        var extractions = scoped.GetRequiredService<IBrandSourceExtractionFacade>();

        return await extractions.ExecuteAsync(claim.OperationId, claim.LeaseToken, cancellationToken);
    }

    public async Task<BrandSourceExtractionMaintenanceSummary> RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var (requeued, abandoned) = await claims.RecoverAbandonedLeasesAsync(clock.UtcNow, cancellationToken);

        return new BrandSourceExtractionMaintenanceSummary(requeued, abandoned);
    }
}
