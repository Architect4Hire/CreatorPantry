using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Queues again the payment provider events whose messages gave up while the provider could not be read (AP.6.2), on
/// a fixed interval for the life of the process, a fresh DI scope per pass.
/// </summary>
/// <remarks>
/// Safe to run on more than one Worker: two sweeps that queue the same event twice queue work that is idempotent, and
/// the count each adds is bounded by <see cref="BillingWebhookPolicy.MaxRedrives"/>.
/// </remarks>
internal sealed class BillingProviderEventRedriveHostedService(
    IServiceScopeFactory scopeFactory, ILogger<BillingProviderEventRedriveHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BillingWebhookPolicy.RedriveInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var (requeued, givenUp) = await scope.ServiceProvider
                    .GetRequiredService<IBillingWebhookFacade>()
                    .RedriveStrandedAsync(stoppingToken);

                if (requeued > 0 || givenUp > 0)
                {
                    logger.LogWarning(
                        "Payment provider events left unprocessed: queued {Requeued} again, gave up on {GivenUp}.",
                        requeued, givenUp);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Payment provider event sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
