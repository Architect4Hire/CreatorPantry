using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Runs <see cref="IAiQuotaMaintenanceFacade.SweepAsync"/> on the same slow interval as AI operation
/// maintenance: applies settlements a worker did not live long enough to apply, releases holds whose lease
/// lapsed, and closes periods whose totals can no longer move.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Beside <see cref="AiOperationMaintenanceHostedService"/> rather than inside it.</strong> The two
/// sweep different modules' tables and reach them through different facades, and a worker that recovered an
/// abandoned lease but not the allowance it was holding — or the reverse — would be worse than either running
/// alone. Separate services fail independently and say which one failed.
/// </para>
/// <para>
/// <c>AiPolicy.MaintenancePollingInterval</c> is deliberately the same number: a hold lapses with the lease it
/// was taken under, so checking for lapsed holds on a different cadence than lapsed leases would only decide
/// which of the two is noticed second.
/// </para>
/// </remarks>
internal sealed class AiQuotaMaintenanceHostedService(
    IServiceScopeFactory scopeFactory, ILogger<AiQuotaMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AiPolicy.MaintenancePollingInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var quota = scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>();
                var summary = await quota.SweepAsync(stoppingToken);

                if (summary.Posted > 0 || summary.Expired > 0 || summary.Finalized > 0)
                {
                    logger.LogInformation(
                        "AI quota maintenance: posted {Posted}, expired {Expired}, finalized {Finalized}.",
                        summary.Posted, summary.Expired, summary.Finalized);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI quota maintenance pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
