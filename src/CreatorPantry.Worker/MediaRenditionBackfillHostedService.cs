using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Requests renditions for stored pictures that have none, a bounded batch at a time (B-28, AF.5.5).
/// </summary>
/// <remarks>
/// The renditions themselves are made by the outbox dispatcher delivering those requests; this only finds
/// the pictures. Declarative, like every other host here: the policy and the work are the domain's.
/// </remarks>
internal sealed class MediaRenditionBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MediaRenditionBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MediaPolicy.RenditionBackfillInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IMediaRenditionBackfillWorker>();
                var requested = await worker.RunAsync(stoppingToken);

                if (requested > 0)
                {
                    logger.LogInformation("Rendition backfill: requested renditions for {Requested} pictures.", requested);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Rendition backfill pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
