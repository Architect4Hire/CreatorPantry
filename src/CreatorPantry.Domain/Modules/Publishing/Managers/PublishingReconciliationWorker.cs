using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>What one reconciliation pass did.</summary>
/// <param name="Claimed">Records this pass took a claim on.</param>
/// <param name="Workspaces">How many workspaces those records belonged to.</param>
/// <param name="Listings">How many times a provider was asked to list a channel — the pass's quota cost.</param>
public sealed record PublishingReconcilePassSummary(
    int Claimed, int Workspaces, int Listings, PublishingReconcileSummary Records);

/// <summary>
/// Settles publishing records the provider has not accounted for: claims the due ones, resolves and validates
/// the workspace each belongs to, and hands them to the facade a channel at a time (13.10c, DATA-RQ-031).
/// </summary>
public interface IPublishingReconciliationWorker
{
    /// <summary>Claims and settles everything due right now, until the queue is empty.</summary>
    Task<PublishingReconcilePassSummary> RunPendingAsync(CancellationToken cancellationToken);

    /// <summary>Releases claims whose worker never came back, so another may take them.</summary>
    Task<int> RunMaintenanceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingReconciliationWorker"/>
/// <remarks>
/// <para>
/// <strong>Finding a row is not authorization.</strong> The claim returns identifiers and queue state only; a
/// fresh scope then resolves the workspace through <c>IWorkspaceResolutionFacade</c> — as the service, because
/// this work outlives whoever confirmed the publication — and every read of creator content after that is made
/// under that workspace's own query filter.
/// </para>
/// <para>
/// <strong>One scope per workspace, one call per profile.</strong> Workspace resolution is one-shot per scope
/// and a batch spans workspaces, so each gets its own; within a workspace the records are grouped by profile
/// so that one listing of a channel settles every record waiting on it.
/// </para>
/// </remarks>
internal sealed class PublishingReconciliationWorker(
    PublishingReconciliationClaimRepository claims,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<PublishingReconciliationWorker> logger) : IPublishingReconciliationWorker
{
    public async Task<PublishingReconcilePassSummary> RunPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = await claims.ClaimDueAsync(Guid.NewGuid(), clock.UtcNow, cancellationToken);

        if (claimed.Count is 0)
        {
            return new PublishingReconcilePassSummary(0, 0, 0, new PublishingReconcileSummary());
        }

        var byWorkspace = claimed.GroupBy(claim => claim.WorkspaceId).ToList();
        var records = new PublishingReconcileSummary();
        var listings = 0;

        foreach (var workspace in byWorkspace)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await using var scope = scopeFactory.CreateAsyncScope();

            var resolution = await scope.ServiceProvider
                .GetRequiredService<IWorkspaceResolutionFacade>()
                .ResolveForServiceAsync(workspace.Key, cancellationToken);

            if (!resolution.Succeeded)
            {
                // The workspace is gone. Its records went with it, or are going; either way nothing here may
                // read or write them, and the claim lapses on its own.
                records += new PublishingReconcileSummary(Skipped: workspace.Count());
                continue;
            }

            var facade = scope.ServiceProvider.GetRequiredService<IPublishingReconciliationFacade>();

            foreach (var profile in workspace.GroupBy(claim => claim.PublishingProfileId))
            {
                try
                {
                    records += await facade.ReconcileProfileAsync(
                        profile.Key, [.. profile.Select(claim => claim.RecordId)], cancellationToken);

                    listings++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One profile's problem is not the pass's. The claims it held lapse and are picked up
                    // again; nothing is concluded about them here.
                    logger.LogError(
                        exception,
                        "Reconciling profile {ProfileId} failed; its claims will lapse and be retried.",
                        profile.Key);
                }
            }
        }

        return new PublishingReconcilePassSummary(claimed.Count, byWorkspace.Count, listings, records);
    }

    public Task<int> RunMaintenanceAsync(CancellationToken cancellationToken) =>
        claims.ReleaseLapsedAsync(clock.UtcNow, cancellationToken);
}
