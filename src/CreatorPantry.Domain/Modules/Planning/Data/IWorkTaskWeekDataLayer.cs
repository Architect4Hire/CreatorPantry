using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>Complete persistence operations for one person's week of work tasks (13A.26).</summary>
public interface IWorkTaskWeekDataLayer
{
    /// <summary>Every group of one person's week, and what each adds up to, read in one pass.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A fixed number of statements, however many tasks there are (PERF-001):</strong> one per list —
    /// in progress, waiting, all seven days together, backlog, unplanned, completed, and carried over only when
    /// asked — one for every total, and one for the labels of every row shown. Sequential, because one
    /// <c>DbContext</c> serves one request.
    /// </para>
    /// <para>
    /// Each list returns at most <see cref="WorkTaskPolicy.WeekGroupMaxRows"/> rows. Whether a group was cut is
    /// read from its total, which counts the whole group, so no list asks for a row it will not show.
    /// </para>
    /// <para>
    /// <paramref name="weekStartsAtUtc"/> and <paramref name="weekEndsAtUtc"/> are the week's first and next
    /// Monday's local midnights as instants, end exclusive: "finished this week" is a question about instants,
    /// and the zone has already been resolved.
    /// </para>
    /// </remarks>
    Task<WorkTaskWeekRead> ReadWeekAsync(
        Guid assigneeMembershipId,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        bool withCarriedOver,
        CancellationToken cancellationToken);
}

/// <summary>The rows and totals of one person's week, as read.</summary>
public sealed record WorkTaskWeekRead(
    IReadOnlyList<WorkTask> Days,
    IReadOnlyList<WorkTask> Backlog,
    IReadOnlyList<WorkTask> Unplanned,
    IReadOnlyList<WorkTask> CarriedOver,
    IReadOnlyList<WorkTask> InProgress,
    IReadOnlyList<WorkTask> Waiting,
    IReadOnlyList<WorkTask> Completed,
    WorkTaskWeekTotals Totals,
    IReadOnlyList<WorkTaskLabelRow> Labels);

internal sealed class WorkTaskWeekDataLayer(IWorkTaskWeekRepository week, IWorkTaskRepository tasks) : IWorkTaskWeekDataLayer
{
    public async Task<WorkTaskWeekRead> ReadWeekAsync(
        Guid assigneeMembershipId,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        bool withCarriedOver,
        CancellationToken cancellationToken)
    {
        const int cap = WorkTaskPolicy.WeekGroupMaxRows;

        var days = await week.ScheduledInWeekAsync(assigneeMembershipId, weekStart, cap, cancellationToken);
        var backlog = await week.PlannedForWeekAsync(assigneeMembershipId, weekStart, cap, cancellationToken);
        var unplanned = await week.UnplannedAsync(assigneeMembershipId, weekStart, cap, cancellationToken);

        // Read only for the week that carries work over; nobody pays for a group that will be empty.
        var carriedOver = withCarriedOver
            ? await week.CarriedOverAsync(assigneeMembershipId, weekStart, cap, cancellationToken)
            : [];

        var inProgress = await week.InStatusAsync(assigneeMembershipId, WorkTaskStatus.InProgress, cap, cancellationToken);
        var waiting = await week.InStatusAsync(assigneeMembershipId, WorkTaskStatus.Waiting, cap, cancellationToken);

        // The daily view's own read, over a week's instants instead of a day's: the same range rule, newest first.
        var completed = await tasks.CompletedBetweenAsync(
            assigneeMembershipId, weekStartsAtUtc, weekEndsAtUtc, cap, cancellationToken);

        var totals = await week.TotalsAsync(
            assigneeMembershipId, weekStart, weekStartsAtUtc, weekEndsAtUtc, withCarriedOver, cancellationToken);

        // The labels of every row the week shows, in one statement (13A.27).
        var labels = await tasks.LabelsForAsync(
            [.. new[] { days, backlog, unplanned, carriedOver, inProgress, waiting, completed }.SelectMany(group => group).Select(task => task.Id)],
            cancellationToken);

        return new WorkTaskWeekRead(days, backlog, unplanned, carriedOver, inProgress, waiting, completed, totals, labels);
    }
}
