using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>What one retention sweep did, across every workspace it reached.</summary>
public sealed record StagedImageRetentionPassSummary(
    int Workspaces, int Expired, int Purged, int Orphans, int Skipped);

/// <summary>
/// Expires staged images nobody chose, removes the bytes of images nobody will read again, and reconciles
/// staging storage against the rows that own it (IMG-005, IMG-006).
/// </summary>
public interface IStagedImageRetentionWorker
{
    Task<StagedImageRetentionPassSummary> RunAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IStagedImageRetentionWorker"/>
/// <remarks>
/// <para>
/// The same shape as the generation worker: find work across workspaces by identifier, then resolve and
/// validate each workspace before anything of that workspace is read or written (tenancy.md). Nothing here
/// deletes anything itself — it resolves a workspace and lets that workspace's own facade do the work
/// under its own query filter and its own storage prefix.
/// </para>
/// <para>
/// <strong>Idempotent by construction.</strong> Every step is a query for work that still needs doing: an
/// image already expired is not in the expiry set, bytes already purged are not in the purge set, and an
/// object already deleted is not in the listing. Running this twice in a row does the work once and then
/// finds nothing, which is also what makes a crash halfway through cost nothing.
/// </para>
/// </remarks>
internal sealed class StagedImageRetentionWorker(
    GeneratedImageClaimRepository work,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<StagedImageRetentionWorker> logger) : IStagedImageRetentionWorker
{
    public async Task<StagedImageRetentionPassSummary> RunAsync(CancellationToken cancellationToken)
    {
        var workspaces = await work.FindWorkspacesWithRetentionWorkAsync(
            clock.UtcNow, MediaPolicy.RetentionBatchSize, cancellationToken);

        var expired = 0;
        var purged = 0;
        var orphans = 0;
        var skipped = 0;
        var reached = 0;

        foreach (var workspaceId in workspaces)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // A fresh scope per workspace: resolution is one-shot per scope, and the gateway below reads
            // its storage prefix from the resolved context.
            await using var scope = scopeFactory.CreateAsyncScope();

            try
            {
                if (!await ResolveAsync(scope.ServiceProvider, workspaceId, cancellationToken))
                {
                    skipped++;

                    continue;
                }

                var summary = await scope.ServiceProvider.GetRequiredService<IStagedImageFacade>()
                    .RunRetentionAsync(cancellationToken);

                reached++;
                expired += summary.Expired;
                purged += summary.Purged;
                orphans += summary.Orphans;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One workspace's failure is not the sweep's. Retention is not urgent and every step is
                // idempotent, so the next pass picks this workspace up again. Logged by exception type
                // and workspace id only: a message here could carry a key, and a key names an operation.
                logger.LogError(
                    "Retention for workspace {WorkspaceId} failed ({ExceptionType}); the next pass will try again.",
                    workspaceId,
                    exception.GetType().Name);

                skipped++;
            }
        }

        return new StagedImageRetentionPassSummary(reached, expired, purged, orphans, skipped);
    }

    /// <summary>
    /// ResolveForServiceAsync: retention must run whoever has since left the workspace, and it acts for
    /// nobody. It populates the ambient context, so every read, write and storage prefix after it is that
    /// workspace's.
    /// </summary>
    private static async Task<bool> ResolveAsync(
        IServiceProvider scoped, Guid workspaceId, CancellationToken cancellationToken) =>
        (await scoped.GetRequiredService<IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(workspaceId, cancellationToken)).Succeeded;
}
