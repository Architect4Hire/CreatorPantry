using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IAiOperationWorker.RunPendingAsync"/> on a fixed interval for the life of the process. A
/// fresh DI scope per pass gives it (and every operation it processes) its own <c>CreatorPantryDbContext</c>,
/// the same lifetime a request scope would give one — mirroring <c>OutboxDispatcherHostedService</c>.
/// </summary>
internal sealed class AiOperationWorkerHostedService(
    IServiceScopeFactory scopeFactory, ILogger<AiOperationWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AiPolicy.WorkerPollingInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IAiOperationWorker>();
                var summary = await worker.RunPendingAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    logger.LogInformation(
                        "AI operation pass: claimed {Claimed}, proposed {Proposed}, failed {Failed}, skipped {Skipped}.",
                        summary.Claimed, summary.Proposed, summary.Failed, summary.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI operation pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
