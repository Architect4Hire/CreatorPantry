using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IAiOperationWorker.RunMaintenanceAsync"/> on its own, slower interval: recovers operations
/// whose lease lapsed because a worker died mid-flight, and expires requests and proposals nobody acted on in
/// time. Cross-workspace and content-free, like the claim itself.
/// </summary>
internal sealed class AiOperationMaintenanceHostedService(
    IServiceScopeFactory scopeFactory, ILogger<AiOperationMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AiPolicy.MaintenancePollingInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IAiOperationWorker>();
                var summary = await worker.RunMaintenanceAsync(stoppingToken);

                if (summary.Requeued > 0 || summary.Abandoned > 0 || summary.Expired > 0)
                {
                    logger.LogInformation(
                        "AI operation maintenance: requeued {Requeued}, abandoned {Abandoned}, expired {Expired}.",
                        summary.Requeued, summary.Abandoned, summary.Expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI operation maintenance pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
