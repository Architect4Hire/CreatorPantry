using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Recovers image generations whose lease lapsed, on a fixed interval.
/// </summary>
/// <remarks>
/// Separate from the pass that claims, because a worker that dies holding a lease is exactly the case the
/// claiming pass cannot see: the operation is <c>Running</c>, so nothing due-dates it back into the queue
/// until this sweep does.
/// </remarks>
internal sealed class GeneratedImageMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<GeneratedImageMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MediaPolicy.GenerationMaintenanceInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>();
                var summary = await worker.RunMaintenanceAsync(stoppingToken);

                if (summary.Requeued > 0 || summary.Abandoned > 0)
                {
                    logger.LogInformation(
                        "Image generation maintenance: requeued {Requeued}, abandoned {Abandoned}.",
                        summary.Requeued,
                        summary.Abandoned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Image generation maintenance failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
