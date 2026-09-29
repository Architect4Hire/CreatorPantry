using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What one reconciliation pass did.</summary>
/// <param name="Posted">Ledger entries written for attempts that had none.</param>
/// <param name="Unattributable">
/// Attempts with no ledger entry and no account to charge them to. Counted rather than posted: an entry
/// attributed to nobody is a row the ledger can never answer a question about.
/// </param>
/// <param name="Drift">
/// Attempts with no ledger entry in the range the backfill may <em>not</em> touch. A defect in the recording
/// path, reported so somebody sees it — never repaired here, because repairing it would hide it.
/// </param>
/// <param name="Remaining">Whether the backfill has more to do, so a caller knows a pass was not the last.</param>
public sealed record AiUsageReconciliationSummary(
    int Posted, int Unattributable, int Drift, bool Remaining);

/// <summary>
/// Posts account usage for provider attempts written before the recording seam existed, and reports attempts
/// written since that the ledger has no entry for (USAGE-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two ranges, two behaviours, and the boundary between them is an instant rather than a switch.</strong>
/// Attempts before <see cref="AiUsageReconciliationOptions.Before"/> are backfilled; attempts at or after it
/// are only ever counted. A flag would have let a pass quietly repair drift in current traffic — masking a
/// defect in the recording path, which is the one thing the repeatable check exists to surface. A cutoff makes
/// that unreachable whatever the configuration says.
/// </para>
/// <para>
/// <strong>Never double-posts, and does not reimplement not double-posting.</strong> Entries are staged
/// through <see cref="IAiUsageRecordingFacade.StageAttemptsAsync"/>, the same call the live path uses, which
/// skips attempts already in the ledger and has the unique index behind it. A pass that runs twice writes once.
/// </para>
/// <para>
/// <strong>It never touches a quota period.</strong> No hold is taken, <c>Consumed</c> does not move, and a
/// finalized period's totals stay final. Backfilled usage is history; spend is what the admission seam records
/// at the moment it happens.
/// </para>
/// <para>
/// Driven by a timer in the worker host, like <see cref="IAiOperationWorker"/>, and reached the same way: a
/// public orchestration type over an internal repository, rather than through a facade. It serves no request
/// and belongs to no workspace.
/// </para>
/// </remarks>
public interface IAiUsageReconciler
{
    Task<AiUsageReconciliationSummary> RunPassAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReconciler"/>
internal sealed class AiUsageReconciler(
    CreatorPantryDbContext context,
    AiUsageReconciliationRepository attempts,
    IAiUsageRecordingFacade usage,
    IWorkspaceContext workspace,
    IClock clock,
    IOptions<AiUsageReconciliationOptions> options) : IAiUsageReconciler
{
    private readonly AiUsageReconciliationOptions _options = options.Value;

    public async Task<AiUsageReconciliationSummary> RunPassAsync(CancellationToken cancellationToken)
    {
        // Turns "there is no IWorkspaceContext here" from a claim in the remarks into something the code
        // enforces. This type saves the ambient scoped DbContext, and its change set is built from reads taken
        // outside the workspace filter -- so a pass running inside a resolved request scope would commit that
        // request's pending writes and stamp them against a workspace this pass never consulted. The worker
        // gives every pass a fresh scope; this is what stops the registration reaching a host that does not.
        if (workspace.IsResolved)
        {
            throw new InvalidOperationException(
                "AI usage reconciliation reads outside the workspace filter and saves the ambient unit of "
                    + "work, so it must run in a scope with no workspace resolved. Give it its own scope.");
        }

        var now = clock.UtcNow;
        var cutoff = _options.Before;

        // Refused rather than clamped, so the claim this design makes is true rather than nearly true. A
        // cutoff in the future is not a cutoff: it puts current traffic back into the backfill's range and
        // drives the drift figure to zero, which is exactly the masked recording defect the split exists to
        // prevent. Silently reinterpreting it would turn a mistake into that outcome without anyone noticing;
        // the pass refuses and the host logs it every tick until somebody fixes the configuration.
        if (cutoff >= now)
        {
            throw new InvalidOperationException(
                $"{AiUsageReconciliationOptions.SectionName}:{nameof(AiUsageReconciliationOptions.Before)} is "
                    + $"{cutoff:O}, which is not in the past. A backfill cutoff that covers current traffic "
                    + "would repair the gaps the drift check exists to report.");
        }

        // The reporting range: everything the backfill is forbidden to touch, bounded to a trailing window so
        // this stays a range seek on a table that grows with every provider attempt forever. A gap here is a
        // real defect rather than a race -- the live path commits the ledger entry in the same transaction as
        // the execution record, so there is no instant at which one exists without the other.
        var driftFrom = Later(now - _options.DriftWindow, cutoff);
        var drift = driftFrom < now
            ? await attempts.CountUnpostedAsync(driftFrom, now, cancellationToken)
            : 0;

        if (cutoff is not { } before)
        {
            // No backfill configured, which is the steady state once one has drained. Reporting only.
            return new AiUsageReconciliationSummary(0, 0, drift, Remaining: false);
        }

        var batch = await attempts.FindUnpostedAsync(
            DateTimeOffset.MinValue, before, _options.BatchSize, cancellationToken);

        if (batch.Count == 0)
        {
            return new AiUsageReconciliationSummary(0, 0, drift, Remaining: false);
        }

        var attributions = (await attempts.FindAttributionsAsync(
                [.. batch.Select(attempt => attempt.AiOperationId).Distinct()], cancellationToken))
            .ToDictionary(attribution => attribution.AiOperationId);

        var posted = 0;
        var unattributable = 0;

        foreach (var operation in batch.GroupBy(attempt => attempt.AiOperationId))
        {
            // No operation row, or a membership that no longer resolves to an account. Counted and left where
            // it is: there is nobody to charge, and guessing would put someone else's spend on a creator's
            // ledger -- which is worse than a gap, because a gap is visible and a wrong bill is not.
            if (!attributions.TryGetValue(operation.Key, out var attribution)
                || string.IsNullOrEmpty(attribution.AccountId))
            {
                unattributable += operation.Count();
                continue;
            }

            posted += await usage.StageAttemptsAsync(
                new AiUsageAttributionServiceModel(
                    attribution.AccountId,
                    attribution.WorkspaceId,
                    attribution.AiOperationId,
                    attribution.TaskType),
                [.. operation.Select(attempt => ToLedgerAttempt(attempt, attribution.TaskType))],
                cancellationToken);
        }

        // One save for the batch. The facade stages rather than saving, exactly as it does on the live path --
        // and a batch that fails here is simply found again by the next pass, because the query is its own
        // bookmark and nothing was recorded to say this batch had been attempted.
        await context.SaveChangesAsync(cancellationToken);

        // Remaining means "this pass made progress and filled its batch", not "the batch was full". The
        // difference matters when a whole batch is unattributable: the query is the bookmark, so the next pass
        // reads the same rows and posts nothing again. Reporting Remaining there would be a sweep claiming
        // progress it is not making, forever. Answering false makes the stall visible instead -- the caller
        // sees a pass that posted nothing and counted something it could not attribute, which is the truth.
        return new AiUsageReconciliationSummary(
            posted, unattributable, drift, Remaining: posted > 0 && batch.Count == _options.BatchSize);
    }

    /// <summary>
    /// One unposted attempt in the ledger's vocabulary — the same view the live path builds, so a reconciled
    /// entry and a settled one are indistinguishable once written.
    /// </summary>
    private static AiUsageAttemptServiceModel ToLedgerAttempt(UnpostedAiAttempt attempt, AiTaskType taskType) =>
        new()
        {
            AttemptNumber = attempt.AttemptNumber,

            // The attempt's own end, never the moment this row is written. A backfill run months later has to
            // land its entries in the period the work actually happened in.
            OccurredAt = attempt.CompletedAt,
            ProviderName = attempt.ProviderName,
            ModelName = attempt.ModelName,
            ModelDeployment = attempt.ModelDeployment,
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,

            // Never a sum of the two above, for the reason AccountAiUsageEntry.TotalTokens gives: a computed
            // total is indistinguishable from a reported one, and nothing reported one here.
            TotalTokens = null,

            // The estimate recorded at the time, not one recomputed from today's prices -- which would restate
            // what an old attempt cost using numbers it was never priced under.
            EstimatedCost = attempt.EstimatedCost,
            Outcome = AiUsageAttribution.OutcomeOf(attempt.SafetyBlocked, attempt.FailureCategory),
            IsBillable = AiUsageAttribution.IsBillable(taskType, attempt.FailureCategory),
        };

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset? right) =>
        right is { } value && value > left ? value : left;
}
