using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>
/// EF Core access to one person's week of work tasks (13A.26). Every query is already scoped to the resolved
/// workspace, and names the person.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The groups are defined here, once.</strong> Each list's predicate and the classification that
/// totals them sit side by side in this file, because a total that disagreed with its list would be a number
/// nobody could check. In precedence order:
/// </para>
/// <list type="number">
/// <item>Done, and finished inside the week's instants: <em>completed</em>.</item>
/// <item>In progress, or waiting: their own group, whatever week they were planned for.</item>
/// <item>Not started, and planned for a day of the week: that <em>day</em>.</item>
/// <item>Not started, no day in the week, and planned for the week: <em>backlog</em>.</item>
/// <item>Not started, no day in the week, and planned for no week: <em>unplanned</em>.</item>
/// <item>Not started, no day in the week, and planned for an earlier week: <em>carried over</em>.</item>
/// </list>
/// <para>
/// A not-started task planned for a later week, or finished in another week, is in none of them.
/// </para>
/// </remarks>
public interface IWorkTaskWeekRepository
{
    /// <summary>One person's tasks in one status, in their own order.</summary>
    Task<IReadOnlyList<WorkTask>> InStatusAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, int take, CancellationToken cancellationToken);

    /// <summary>
    /// One person's not-started tasks planned for each day of the week, at most <paramref name="takePerDay"/> a
    /// day, in day order and then their own order.
    /// </summary>
    /// <remarks>
    /// One statement for all seven days: the cap is applied per day by partitioning on the day, so one crowded
    /// Monday cannot starve the rest of the week.
    /// </remarks>
    Task<IReadOnlyList<WorkTask>> ScheduledInWeekAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int takePerDay, CancellationToken cancellationToken);

    /// <summary>Not started, planned for the week and for none of its days, in their own order.</summary>
    Task<IReadOnlyList<WorkTask>> PlannedForWeekAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int take, CancellationToken cancellationToken);

    /// <summary>Not started and planned for no week at all, in their own order.</summary>
    Task<IReadOnlyList<WorkTask>> UnplannedAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int take, CancellationToken cancellationToken);

    /// <summary>Not started and planned for a week before this one, oldest week first, then day, then their own order.</summary>
    Task<IReadOnlyList<WorkTask>> CarriedOverAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int take, CancellationToken cancellationToken);

    /// <summary>
    /// What every group of the week adds up to, in one grouped statement over the whole of each group rather
    /// than the rows a list returns.
    /// </summary>
    /// <remarks>
    /// The statement groups by status, by the day when it is one of the week's, and by the planned week folded
    /// to "this", "none" or "earlier"; so it returns a handful of rows however many tasks there are, and each
    /// row is placed in a group by <c>WorkTaskWeekRepository.Classify</c> under the same precedence the lists use.
    /// </remarks>
    Task<WorkTaskWeekTotals> TotalsAsync(
        Guid assigneeMembershipId,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        bool withCarriedOver,
        CancellationToken cancellationToken);
}

/// <summary>What one group adds up to. An empty group is all zeros with no minutes.</summary>
public sealed record WorkTaskCount(int TaskCount, int EstimatedTaskCount, long? EstimatedMinutes)
{
    public static readonly WorkTaskCount None = new(0, 0, null);

    public WorkTaskCount Add(WorkTaskCount other) => new(
        TaskCount + other.TaskCount,
        EstimatedTaskCount + other.EstimatedTaskCount,
        EstimatedMinutes is null && other.EstimatedMinutes is null
            ? null
            : (EstimatedMinutes ?? 0) + (other.EstimatedMinutes ?? 0));
}

/// <summary>What every group of one person's week adds up to.</summary>
public sealed record WorkTaskWeekTotals(
    WorkTaskCount Completed,
    WorkTaskCount InProgress,
    WorkTaskCount Waiting,
    IReadOnlyDictionary<DateOnly, WorkTaskCount> Days,
    WorkTaskCount Backlog,
    WorkTaskCount Unplanned,
    WorkTaskCount CarriedOver)
{
    public WorkTaskCount Day(DateOnly date) => Days.GetValueOrDefault(date, WorkTaskCount.None);
}

internal sealed class WorkTaskWeekRepository(CreatorPantryDbContext context) : IWorkTaskWeekRepository
{
    /// <summary>
    /// Stands for "an earlier week" in the totals statement's grouping key, so leftovers from any number of past
    /// weeks fold into one row. No week starts on it, because no week may be planned before 1900.
    /// </summary>
    private static readonly DateOnly EarlierWeek = DateOnly.MinValue;

    public Task<IReadOnlyList<WorkTask>> InStatusAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, int take, CancellationToken cancellationToken) =>
        ListAsync(InOwnOrder(Mine(assigneeMembershipId).Where(task => task.Status == status)), take, cancellationToken);

    public async Task<IReadOnlyList<WorkTask>> ScheduledInWeekAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int takePerDay, CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);

        // Each day's rows as a capped collection of its group, which EF writes as ROW_NUMBER() partitioned by the
        // day — on both engines — rather than one statement per day.
        var days = await Mine(assigneeMembershipId)
            .Where(task => task.Status == WorkTaskStatus.Backlog
                && task.ScheduledDate >= weekStart
                && task.ScheduledDate <= weekEnd)
            .GroupBy(task => task.ScheduledDate)
            .Select(day => new
            {
                Date = day.Key,
                Tasks = day.OrderBy(task => task.SortRank).ThenBy(task => task.Id).Take(takePerDay).ToList(),
            })
            .ToListAsync(cancellationToken);

        // Within a day the rows keep the order the store gave them, so ties on rank break the same way here as in
        // every other list; only the days themselves are put in order.
        return [.. days.OrderBy(day => day.Date).SelectMany(day => day.Tasks)];
    }

    public Task<IReadOnlyList<WorkTask>> PlannedForWeekAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int take, CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);

        return ListAsync(
            InOwnOrder(NotStartedOffTheDays(assigneeMembershipId, weekStart, weekEnd)
                .Where(task => task.PlanningWeekStart == weekStart)),
            take,
            cancellationToken);
    }

    public Task<IReadOnlyList<WorkTask>> UnplannedAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int take, CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);

        return ListAsync(
            InOwnOrder(NotStartedOffTheDays(assigneeMembershipId, weekStart, weekEnd)
                .Where(task => task.PlanningWeekStart == null)),
            take,
            cancellationToken);
    }

    public Task<IReadOnlyList<WorkTask>> CarriedOverAsync(
        Guid assigneeMembershipId, DateOnly weekStart, int take, CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);

        return ListAsync(
            NotStartedOffTheDays(assigneeMembershipId, weekStart, weekEnd)
                .Where(task => task.PlanningWeekStart < weekStart)
                .OrderBy(task => task.PlanningWeekStart)
                .ThenBy(task => task.ScheduledDate)
                .ThenBy(task => task.SortRank)
                .ThenBy(task => task.Id),
            take,
            cancellationToken);
    }

    public async Task<WorkTaskWeekTotals> TotalsAsync(
        Guid assigneeMembershipId,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        bool withCarriedOver,
        CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);
        var earlier = EarlierWeek;

        var buckets = await Mine(assigneeMembershipId)
            .Where(task =>
                (task.Status == WorkTaskStatus.Done
                    && task.CompletedAt >= weekStartsAtUtc
                    && task.CompletedAt < weekEndsAtUtc)
                || task.Status == WorkTaskStatus.InProgress
                || task.Status == WorkTaskStatus.Waiting
                || (task.Status == WorkTaskStatus.Backlog
                    && ((task.ScheduledDate >= weekStart && task.ScheduledDate <= weekEnd)
                        || task.PlanningWeekStart == null
                        || task.PlanningWeekStart == weekStart
                        || (withCarriedOver && task.PlanningWeekStart < weekStart))))
            .GroupBy(task => new
            {
                task.Status,
                Day = task.Status == WorkTaskStatus.Backlog && task.ScheduledDate >= weekStart && task.ScheduledDate <= weekEnd
                    ? task.ScheduledDate
                    : null,
                Week = task.Status != WorkTaskStatus.Backlog
                    ? (DateOnly?)null
                    : task.PlanningWeekStart < weekStart ? (DateOnly?)earlier : task.PlanningWeekStart,
            })
            .Select(bucket => new
            {
                bucket.Key.Status,
                bucket.Key.Day,
                bucket.Key.Week,
                Tasks = bucket.Count(),
                Estimated = bucket.Count(task => task.EstimateMinutes != null),
                Minutes = bucket.Sum(task => (long?)task.EstimateMinutes),
            })
            .ToListAsync(cancellationToken);

        var completed = WorkTaskCount.None;
        var inProgress = WorkTaskCount.None;
        var waiting = WorkTaskCount.None;
        var backlog = WorkTaskCount.None;
        var unplanned = WorkTaskCount.None;
        var carriedOver = WorkTaskCount.None;
        var days = new Dictionary<DateOnly, WorkTaskCount>();

        foreach (var bucket in buckets)
        {
            // LINQ's Sum over nullable values is zero when every value is null, and EF keeps that meaning in
            // SQL. Zero would claim the work takes no time, so a bucket with nothing estimated has no minutes.
            var count = new WorkTaskCount(bucket.Tasks, bucket.Estimated, bucket.Estimated == 0 ? null : bucket.Minutes);

            switch (Classify(bucket.Status, bucket.Day, bucket.Week, weekStart))
            {
                case WeekGroup.Completed: completed = completed.Add(count); break;
                case WeekGroup.InProgress: inProgress = inProgress.Add(count); break;
                case WeekGroup.Waiting: waiting = waiting.Add(count); break;
                case WeekGroup.Day: days[bucket.Day!.Value] = days.GetValueOrDefault(bucket.Day.Value, WorkTaskCount.None).Add(count); break;
                case WeekGroup.Backlog: backlog = backlog.Add(count); break;
                case WeekGroup.Unplanned: unplanned = unplanned.Add(count); break;
                case WeekGroup.CarriedOver: carriedOver = carriedOver.Add(count); break;
            }
        }

        return new WorkTaskWeekTotals(completed, inProgress, waiting, days, backlog, unplanned, carriedOver);
    }

    /// <summary>
    /// The group a totals row belongs to, under the precedence in this type's remarks. <paramref name="day"/> is
    /// set only for a not-started task on one of the week's days, and <paramref name="week"/> is the planned week
    /// folded so that every earlier one is <see cref="EarlierWeek"/>.
    /// </summary>
    internal static WeekGroup Classify(WorkTaskStatus status, DateOnly? day, DateOnly? week, DateOnly weekStart) =>
        status switch
        {
            WorkTaskStatus.Done => WeekGroup.Completed,
            WorkTaskStatus.InProgress => WeekGroup.InProgress,
            WorkTaskStatus.Waiting => WeekGroup.Waiting,
            _ when day is not null => WeekGroup.Day,
            _ when week == weekStart => WeekGroup.Backlog,
            _ when week is null => WeekGroup.Unplanned,
            _ => WeekGroup.CarriedOver,
        };

    internal enum WeekGroup
    {
        Completed,
        InProgress,
        Waiting,
        Day,
        Backlog,
        Unplanned,
        CarriedOver,
    }

    /// <summary>
    /// One person's tasks in the resolved workspace. The workspace itself is the global filter's, so this names
    /// only the person — and naming them is what keeps one member's week out of another's.
    /// </summary>
    private IQueryable<WorkTask> Mine(Guid assigneeMembershipId) =>
        context.WorkTasks.AsNoTracking().Where(task => task.AssigneeMembershipId == assigneeMembershipId);

    /// <summary>
    /// Not started and on none of the week's days: the pool backlog, unplanned and carried over divide by week.
    /// A null day is on none of them; EF compares with C# null semantics, so it does not drop out of the negation.
    /// </summary>
    private IQueryable<WorkTask> NotStartedOffTheDays(Guid assigneeMembershipId, DateOnly weekStart, DateOnly weekEnd) =>
        Mine(assigneeMembershipId).Where(task => task.Status == WorkTaskStatus.Backlog
            && !(task.ScheduledDate >= weekStart && task.ScheduledDate <= weekEnd));

    /// <summary>The person's own arrangement: the place they gave each task, then the id to break a tie.</summary>
    private static IQueryable<WorkTask> InOwnOrder(IQueryable<WorkTask> tasks) =>
        tasks.OrderBy(task => task.SortRank).ThenBy(task => task.Id);

    private static async Task<IReadOnlyList<WorkTask>> ListAsync(
        IQueryable<WorkTask> query, int take, CancellationToken cancellationToken) =>
        await query.Take(take).ToListAsync(cancellationToken);
}
