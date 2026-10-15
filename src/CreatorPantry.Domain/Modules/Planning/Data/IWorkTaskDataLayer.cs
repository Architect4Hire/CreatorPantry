using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>Complete persistence operations for work tasks, and the transaction boundary around them.</summary>
public interface IWorkTaskDataLayer
{
    /// <summary>
    /// Writes a new task at the bottom of its group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The place is decided here, inside the operation that writes it</strong>: one gap below the
    /// group's lowest task, read immediately before the save. Two captures racing into one group may read the
    /// same lowest task and take the same rank; both are written, and the order — rank, then id — still reads
    /// them one way every time. That is what the gapped rank is for, so there is no retry.
    /// </para>
    /// <para>
    /// The unit of work is the request's own <c>DbContext</c>, so an idempotency record staged by the caller
    /// commits with this row or not at all. False means nothing was written and nothing is left staged, which
    /// is what lets the caller say a refusal is safe to send again.
    /// </para>
    /// <para>
    /// <strong>Whether the row landed is answered by looking, not by reading the exception.</strong> EF wraps
    /// a dropped connection or a timeout in the same <c>DbUpdateException</c> as a broken constraint, and the
    /// first two can be raised over a commit that in fact succeeded. Reporting that as "nothing written"
    /// would invite a retry that captures the task twice, so the row is read back before anything is claimed.
    /// A read that cannot be answered either is left to propagate: the honest answer is then that nobody
    /// knows, and a 500 promises nothing about retrying where a 409 would.
    /// </para>
    /// </remarks>
    Task<bool> CreateAsync(WorkTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Every group of one person's day, read in one pass (13A.25).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Six indexed reads, each asking for one more row than it will return so the caller can say the group
    /// was cut without a second counting query. The groups are made not to overlap in the queries
    /// themselves, so a group that is cut is cut from rows that genuinely belong to it.
    /// </para>
    /// <para>
    /// <paramref name="dayStartsAtUtc"/> and <paramref name="dayEndsAtUtc"/> bound the local day as instants,
    /// which is the only form "finished today" can take: a completion is an instant, and the day it falls in
    /// is a question about a zone the caller has already resolved.
    /// </para>
    /// </remarks>
    Task<WorkTaskDay> ReadDayAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateTimeOffset dayStartsAtUtc,
        DateTimeOffset dayEndsAtUtc,
        bool withSuggestions,
        CancellationToken cancellationToken);
}

/// <summary>
/// One person's day as the store holds it: each group with one row more than will be shown, so the caller
/// can tell a full group from a cut one.
/// </summary>
public sealed record WorkTaskDay(
    IReadOnlyList<WorkTask> Overdue,
    IReadOnlyList<WorkTask> ScheduledToday,
    IReadOnlyList<WorkTask> InProgress,
    IReadOnlyList<WorkTask> Waiting,
    IReadOnlyList<WorkTask> CompletedToday,
    IReadOnlyList<WorkTask> Unplanned);

internal sealed class WorkTaskDataLayer(IWorkTaskRepository repository, CreatorPantryDbContext db) : IWorkTaskDataLayer
{
    public async Task<bool> CreateAsync(WorkTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var bottom = await repository.HighestRankAsync(
            task.AssigneeMembershipId, task.Status, task.ScheduledDate, cancellationToken);

        task.SortRank = (bottom ?? 0) + WorkTaskPolicy.RankGap;

        repository.Add(task);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException)
        {
            // Nothing of this one is left staged, whichever way it failed.
            db.ChangeTracker.Clear();

            if (await repository.FindAsync(task.Id, cancellationToken) is not { } committed)
            {
                // A check constraint or a lost race: the row is not there, so neither is the idempotency
                // record that shared the save, and the caller may offer the request again as it stands.
                return false;
            }

            // It did land. The in-memory copy never received the row version the store generated, and the
            // token the caller is about to be handed has to be the committed one or their next edit is
            // refused against a version that never existed.
            task.RowVersion = committed.RowVersion;

            return true;
        }
    }

    public async Task<WorkTaskDay> ReadDayAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateTimeOffset dayStartsAtUtc,
        DateTimeOffset dayEndsAtUtc,
        bool withSuggestions,
        CancellationToken cancellationToken)
    {
        // One past the cap, so a full group and a cut one are distinguishable without counting.
        const int probe = WorkTaskPolicy.TodayGroupMaxRows + 1;

        // Sequential rather than concurrent: one DbContext serves one request and cannot be used by two
        // reads at once. Each is a single indexed seek, so the cost is in the round trips, not the work.
        var overdue = await repository.OverdueAsync(assigneeMembershipId, today, probe, cancellationToken);
        var scheduled = await repository.ScheduledOnAsync(assigneeMembershipId, today, probe, cancellationToken);
        var inProgress = await repository.ByStatusNotOverdueAsync(
            assigneeMembershipId, WorkTaskStatus.InProgress, today, probe, cancellationToken);
        var waiting = await repository.ByStatusNotOverdueAsync(
            assigneeMembershipId, WorkTaskStatus.Waiting, today, probe, cancellationToken);
        var completed = await repository.CompletedBetweenAsync(
            assigneeMembershipId, dayStartsAtUtc, dayEndsAtUtc, probe, cancellationToken);

        // The sixth read happens only when suggestions were asked for: nobody pays for an offer they did
        // not want.
        var unplanned = withSuggestions
            ? await repository.UnplannedBacklogAsync(
                assigneeMembershipId, today, WorkTaskPolicy.TodaySuggestionCount, cancellationToken)
            : [];

        return new WorkTaskDay(overdue, scheduled, inProgress, waiting, completed, unplanned);
    }
}
