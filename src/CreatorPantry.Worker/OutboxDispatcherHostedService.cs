using CreatorPantry.Domain.Managers.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IOutboxDispatcher.DispatchDueAsync"/> on a fixed interval for the life of the process. A
/// fresh DI scope per pass gives the dispatcher (and any handler it invokes) its own
/// <c>CreatorPantryDbContext</c>, the same lifetime a request scope would give one.
/// </summary>
/// <remarks>
/// Each claim is its own guarded update (see <see cref="OutboxDispatcher"/>): a message two Worker instances
/// both read as due is claimed by one of them and left alone by the other, so running more than one is safe.
/// </remarks>
internal sealed class OutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(OutboxPolicy.PollingInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                var summary = await dispatcher.DispatchDueAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    logger.LogInformation(
                        "Outbox dispatch: claimed {Claimed}, completed {Completed}, retrying {Retrying}, poisoned {Poisoned}.",
                        summary.Claimed, summary.Completed, summary.Retrying, summary.Poisoned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
