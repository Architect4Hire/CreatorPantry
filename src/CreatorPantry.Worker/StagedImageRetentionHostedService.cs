using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IStagedImageRetentionWorker.RunAsync"/> on a fixed interval for the life of the process,
/// in a fresh DI scope per pass.
/// </summary>
internal sealed class StagedImageRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<StagedImageRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MediaPolicy.RetentionInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IStagedImageRetentionWorker>();
                var summary = await worker.RunAsync(stoppingToken);

                if (summary.Workspaces > 0 || summary.Skipped > 0)
                {
                    logger.LogInformation(
                        "Staged image retention: {Workspaces} workspaces, expired {Expired}, purged "
                            + "{Purged}, orphans {Orphans}, skipped {Skipped}.",
                        summary.Workspaces,
                        summary.Expired,
                        summary.Purged,
                        summary.Orphans,
                        summary.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Staged image retention pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
