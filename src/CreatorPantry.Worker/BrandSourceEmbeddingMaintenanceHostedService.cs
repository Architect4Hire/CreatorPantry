using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IBrandSourceEmbeddingWorker.RunMaintenanceAsync"/> on its own slower interval: recovering
/// lapsed leases, deleting retired chunk sets, and queueing re-embedding after a model or chunker change.
/// </summary>
internal sealed class BrandSourceEmbeddingMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BrandSourceEmbeddingMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BrandPolicy.EmbeddingMaintenanceInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingWorker>();
                var summary = await worker.RunMaintenanceAsync(stoppingToken);

                if (summary.Requeued > 0 || summary.Abandoned > 0 || summary.Retired > 0 || summary.Enqueued > 0)
                {
                    logger.LogInformation(
                        "Brand source embedding maintenance: requeued {Requeued}, abandoned {Abandoned}, "
                            + "retired {Retired} set(s), queued {Enqueued} re-embedding(s).",
                        summary.Requeued,
                        summary.Abandoned,
                        summary.Retired,
                        summary.Enqueued);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Brand source embedding maintenance failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
