using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Recovers extraction leases abandoned by a worker that died holding one.
/// </summary>
/// <remarks>
/// A separate loop from <see cref="BrandSourceExtractionWorkerHostedService"/>, on its own longer interval, for
/// the reason the AI queue separates the two: a sweep that ran on every claim pass would query for lapsed leases
/// ten times a minute to find nothing, and a sweep folded into the claim would not run at all on a host whose
/// claim loop is the thing that died.
/// </remarks>
internal sealed class BrandSourceExtractionMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BrandSourceExtractionMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BrandPolicy.ExtractionMaintenanceInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IBrandSourceExtractionWorker>();
                var summary = await worker.RunMaintenanceAsync(stoppingToken);

                if (summary.Requeued > 0 || summary.Abandoned > 0)
                {
                    logger.LogInformation(
                        "Brand source extraction maintenance: requeued {Requeued}, abandoned {Abandoned}.",
                        summary.Requeued,
                        summary.Abandoned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Brand source extraction maintenance failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
