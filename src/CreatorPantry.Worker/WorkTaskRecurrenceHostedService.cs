using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Worker;

/// <summary>
/// Makes routines' occurrences a week ahead (13A.30): a pass every <see cref="WorkTaskTemplatePolicy.PollingInterval"/>,
/// each in a scope of its own. A pass that fails is logged and the next one carries on: every occurrence is one
/// save, so nothing is left half-made.
/// </summary>
internal sealed class WorkTaskRecurrenceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkTaskRecurrenceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(WorkTaskTemplatePolicy.PollingInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<IWorkTaskTemplateWorker>();
                var summary = await worker.RunPendingAsync(stoppingToken);

                if (summary.Claimed > 0)
                {
                    // Counts only: a routine's title is its person's words.
                    logger.LogInformation(
                        "Routine pass: claimed {Claimed} across {Workspaces} workspace(s); made {Made} task(s), "
                            + "{Ended} ended, {NeedsAttention} need attention, {Skipped} skipped.",
                        summary.Claimed,
                        summary.Workspaces,
                        summary.Made,
                        summary.Ended,
                        summary.NeedsAttention,
                        summary.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Routine pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
