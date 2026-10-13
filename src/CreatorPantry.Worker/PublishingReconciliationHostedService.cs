using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IPublishingReconciliationWorker.RunPendingAsync"/> on a fixed interval for the life of the
/// process, in a fresh DI scope per pass (13.10c).
/// </summary>
/// <remarks>
/// A pass that finds no due record makes no provider call, so the interval costs the provider's quota nothing
/// when there is nothing outstanding.
/// </remarks>
internal sealed class PublishingReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PublishingReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PublishingLedgerPolicy.ReconcilePollingInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IPublishingReconciliationWorker>();
                var summary = await worker.RunPendingAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    logger.LogInformation(
                        "Publishing reconciliation pass: claimed {Claimed} across {Workspaces} workspace(s) with "
                            + "{Listings} listing(s); settled {Settled}, submitted {Submitted}, waiting "
                            + "{StillWaiting}, needs attention {NeedsAttention}, skipped {Skipped}, unavailable "
                            + "{Unavailable}.",
                        summary.Claimed,
                        summary.Workspaces,
                        summary.Listings,
                        summary.Records.Settled,
                        summary.Records.Submitted,
                        summary.Records.StillWaiting,
                        summary.Records.NeedsAttention,
                        summary.Records.Skipped,
                        summary.Records.Unavailable);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Publishing reconciliation pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Releases publishing claims whose worker never came back, so another may take them.
/// </summary>
/// <remarks>
/// It clears leases and nothing else. A record recovered this way is reconciled rather than resubmitted — its
/// own state says what is in doubt — so recovering one cannot produce a second post.
/// </remarks>
internal sealed class PublishingReconciliationMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PublishingReconciliationMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PublishingLedgerPolicy.ReconcileMaintenanceInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var released = await scope.ServiceProvider
                    .GetRequiredService<IPublishingReconciliationWorker>()
                    .RunMaintenanceAsync(stoppingToken);

                if (released > 0)
                {
                    logger.LogInformation("Publishing maintenance released {Released} lapsed claim(s).", released);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Publishing maintenance sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
