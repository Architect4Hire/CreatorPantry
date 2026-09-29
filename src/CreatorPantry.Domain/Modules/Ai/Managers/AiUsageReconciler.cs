using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// How many times a batch restages after losing <c>UX_AccountAiUsageEntries_Operation_Attempt</c> to a
    /// concurrent pass before this pass gives up and leaves the batch for the next one.
    /// </summary>
    private const int ContendedWriteAttempts = 3;

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

        var unattributable = 0;
        var attributed = new List<(AiUsageAttributionServiceModel Attribution, List<UnpostedAiAttempt> Attempts)>();

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

            attributed.Add((
                new AiUsageAttributionServiceModel(
                    attribution.AccountId,
                    attribution.WorkspaceId,
                    attribution.AiOperationId,
                    attribution.TaskType),
                [.. operation]));
        }

        var posted = await StageAndSaveAsync(attributed, cancellationToken);

        // Remaining means "this pass made progress and filled its batch", not "the batch was full". The
        // difference matters when a whole batch is unattributable: the query is the bookmark, so the next pass
        // reads the same rows and posts nothing again. Reporting Remaining there would be a sweep claiming
        // progress it is not making, forever. Answering false makes the stall visible instead -- the caller
        // sees a pass that posted nothing and counted something it could not attribute, which is the truth.
        return new AiUsageReconciliationSummary(
            posted, unattributable, drift, Remaining: posted > 0 && batch.Count == _options.BatchSize);
    }

    /// <summary>
    /// Stages every attributed operation's attempts and saves the batch in one transaction, restaging against
    /// a fresh read when a concurrent pass wins the unique index first.
    /// </summary>
    /// <remarks>
    /// <see cref="IAiUsageRecordingFacade.StageAttemptsAsync"/> only skips an attempt number its own read
    /// already saw posted, so two passes reading the same "not yet posted" answer for the same row before
    /// either saves is a genuine race, not a defect -- nothing here holds a lock across that read, on purpose,
    /// because the live path does not either. One of the two then loses
    /// <c>UX_AccountAiUsageEntries_Operation_Attempt</c> at <c>SaveChangesAsync</c>, and the batch's insert is
    /// one transaction, so an uncaught exception here would rewind every other, uncontested row the loser
    /// staged along with it. Forgetting what was staged and restaging is what turns that into "this pass posts
    /// the batch minus the row the winner already committed" instead of failing the whole batch on a race the
    /// design already expects.
    /// </remarks>
    private async Task<int> StageAndSaveAsync(
        IReadOnlyList<(AiUsageAttributionServiceModel Attribution, List<UnpostedAiAttempt> Attempts)> attributed,
        CancellationToken cancellationToken)
    {
        if (attributed.Count == 0)
        {
            return 0;
        }

        for (var attempt = 0; attempt < ContendedWriteAttempts; attempt++)
        {
            var posted = 0;

            foreach (var (attribution, attempts) in attributed)
            {
                posted += await usage.StageAttemptsAsync(
                    attribution,
                    [.. attempts.Select(entry => ToLedgerAttempt(entry, attribution.TaskType))],
                    cancellationToken);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return posted;
            }
            catch (DbUpdateException)
            {
                // Forget first. The staged entries ride the shared scoped DbContext, and leaving them tracked
                // would let a later SaveChangesAsync in the same request commit rows this attempt just lost.
                context.ChangeTracker.Clear();
            }
        }

        // Every attempt lost the race. Posting nothing here is not a loss: the query that built this batch is
        // its own bookmark, and the next scheduled pass reads the same unposted rows and tries again.
        return 0;
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
