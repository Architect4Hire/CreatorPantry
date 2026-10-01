using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IBrandSourceExtractionWorker.RunPendingAsync"/> on a fixed interval for the life of the
/// process. A fresh DI scope per pass gives it (and every operation it processes) its own
/// <c>CreatorPantryDbContext</c>, the same lifetime a request scope would give one — mirroring
/// <see cref="AiOperationWorkerHostedService"/>.
/// </summary>
internal sealed class BrandSourceExtractionWorkerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BrandSourceExtractionWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BrandPolicy.ExtractionPollingInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IBrandSourceExtractionWorker>();
                var summary = await worker.RunPendingAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    logger.LogInformation(
                        "Brand source extraction pass: claimed {Claimed}, extracted {Extracted}, reviewed "
                            + "{Reviewed}, requeued {Requeued}, failed {Failed}, cancelled {Cancelled}, skipped {Skipped}.",
                        summary.Claimed,
                        summary.Extracted,
                        summary.Reviewed,
                        summary.Requeued,
                        summary.Failed,
                        summary.Cancelled,
                        summary.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Brand source extraction pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
