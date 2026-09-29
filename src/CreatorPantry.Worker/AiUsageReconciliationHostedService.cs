using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Worker;

/// <summary>
/// Posts account usage for provider attempts written before the recording seam existed, and reports attempts
/// written since that the ledger has no entry for (USAGE-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Drift is logged as a warning, and that is the point.</strong> The backfill repairs only what
/// happened before its configured cutoff; anything after it is counted and left alone, because a pass that
/// quietly fixed a gap in current traffic would hide a defect in the recording path — the one thing this check
/// exists to make visible. A warning that nobody has to go looking for is the whole deliverable.
/// </para>
/// <para>
/// <strong>Resumable by having no state.</strong> What still needs posting is the query itself, so a crash, a
/// redeploy and a duplicate delivery are the same case: the next pass finds what is left. A pass that still
/// has work says so and the next tick continues, rather than one long run holding rows against live traffic.
/// </para>
/// </remarks>
internal sealed class AiUsageReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AiUsageReconciliationOptions> options,
    ILogger<AiUsageReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reconciliation = scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>();
                var summary = await reconciliation.RunPassAsync(stoppingToken);

                if (summary.Posted > 0 || summary.Unattributable > 0)
                {
                    logger.LogInformation(
                        "AI usage reconciliation: posted {Posted}, unattributable {Unattributable}, "
                            + "more to do: {Remaining}.",
                        summary.Posted, summary.Unattributable, summary.Remaining);
                }

                if (summary.Posted == 0 && summary.Unattributable > 0)
                {
                    // A backfill that cannot move. The query is its own bookmark, so a batch it can attribute
                    // none of is a batch every later pass reads again -- correct, and useless. Said out loud
                    // rather than left as a timer quietly achieving nothing every fifteen minutes.
                    logger.LogWarning(
                        "AI usage reconciliation could attribute none of the {Unattributable} attempt(s) it "
                            + "read, so the backfill cannot advance past them. They have no account to charge "
                            + "and will not be posted.",
                        summary.Unattributable);
                }

                if (summary.Drift > 0)
                {
                    // Not repaired, and not an Information line. Every attempt should have posted its entry in
                    // the same transaction as its execution record, so a non-zero figure here means that did
                    // not happen -- which is a defect to investigate rather than a number to watch drift up.
                    logger.LogWarning(
                        "AI usage reconciliation found {Drift} provider attempt(s) in the last {Window} with "
                            + "no account ledger entry. These are not backfilled: the recording path should "
                            + "have written them, and repairing them here would hide why it did not.",
                        summary.Drift,
                        options.Value.DriftWindow);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI usage reconciliation pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
