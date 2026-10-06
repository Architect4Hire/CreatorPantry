using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IGeneratedImageWorker.RunPendingAsync"/> on a fixed interval for the life of the
/// process, in a fresh DI scope per pass — mirroring <see cref="BrandSourceEmbeddingWorkerHostedService"/>.
/// </summary>
internal sealed class GeneratedImageWorkerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<GeneratedImageWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MediaPolicy.GenerationPollingInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>();
                var summary = await worker.RunPendingAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    logger.LogInformation(
                        "Image generation pass: claimed {Claimed}, generated {Generated}, partial {Partial}, "
                            + "requeued {Requeued}, failed {Failed}, cancelled {Cancelled}, skipped {Skipped}.",
                        summary.Claimed,
                        summary.Generated,
                        summary.PartiallyGenerated,
                        summary.Requeued,
                        summary.Failed,
                        summary.Cancelled,
                        summary.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Image generation pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
