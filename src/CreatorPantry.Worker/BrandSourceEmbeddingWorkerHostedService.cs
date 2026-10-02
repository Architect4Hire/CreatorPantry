using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IBrandSourceEmbeddingWorker.RunPendingAsync"/> on a fixed interval for the life of the
/// process, in a fresh DI scope per pass — mirroring <see cref="BrandSourceExtractionWorkerHostedService"/>.
/// </summary>
internal sealed class BrandSourceEmbeddingWorkerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BrandSourceEmbeddingWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BrandPolicy.EmbeddingPollingInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingWorker>();
                var summary = await worker.RunPendingAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    logger.LogInformation(
                        "Brand source embedding pass: claimed {Claimed}, embedded {Embedded}, requeued "
                            + "{Requeued}, failed {Failed}, cancelled {Cancelled}, skipped {Skipped}.",
                        summary.Claimed,
                        summary.Embedded,
                        summary.Requeued,
                        summary.Failed,
                        summary.Cancelled,
                        summary.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Brand source embedding pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
