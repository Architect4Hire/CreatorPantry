using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The planning module's part of the workspace dashboard (13A.33a).</summary>
public interface IWorkPlanDashboardBusiness
{
    /// <summary>
    /// The caller's own plan for one local day, and what the week holding it adds up to.
    /// </summary>
    /// <param name="today">The day, already decided in <paramref name="timeZoneId"/> by the caller.</param>
    /// <param name="timeZoneId">The workspace's scheduling zone, which the caller has already validated.</param>
    Task<WorkPlanSummaryServiceModel> GetAsync(DateOnly today, string timeZoneId, CancellationToken cancellationToken);
}

/// <remarks>
/// <para>
/// <strong>The day is the caller's to decide.</strong> This takes the day and the zone rather than reading the
/// brand profile again: the dashboard has already resolved both, and one request must not be able to answer
/// about two different "todays".
/// </para>
/// <para>
/// <strong>Which week is derived, not stored.</strong> <see cref="WorkTaskWeek.StartOf"/> settles it, so the
/// dashboard and My Week agree about where a week begins (13A.26), and the week's edges become instants in that
/// same zone for the one question that is about instants — what was finished.
/// </para>
/// <para>
/// <strong>No role bar, and none is needed</strong>, as in the daily view: a resolved workspace context is the
/// authorization for the lowest role, and the answer is the caller's own work and nobody else's. A Viewer handed
/// work before being demoted can still see it, which is the right answer.
/// </para>
/// </remarks>
internal sealed class WorkPlanDashboardBusiness(
    IWorkPlanDashboardDataLayer data,
    IWorkspaceContext workspace,
    ITimeZoneConverter zones) : IWorkPlanDashboardBusiness
{
    public async Task<WorkPlanSummaryServiceModel> GetAsync(
        DateOnly today, string timeZoneId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var weekStart = WorkTaskWeek.StartOf(today);
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);
        var weekStartsAt = zones.ToUtc(weekStart, TimeOnly.MinValue, timeZoneId).Utc;
        var weekEndsAt = zones.ToUtc(weekEnd.AddDays(1), TimeOnly.MinValue, timeZoneId).Utc;

        var read = await data.ReadAsync(
            workspace.MembershipId,
            today,
            weekStart,
            weekStartsAt,
            weekEndsAt,
            WorkPlanDashboardPolicy.ListLimit,
            cancellationToken);

        var overdue = read.Overdue.Select(ToTask).ToList();
        var scheduled = read.Today.Select(ToTask).ToList();

        // Only a task on show is named, as the daily view names them (13A.32): a chosen task that is not in
        // either list — handed on, finished, or past the three rows — would otherwise point at nothing.
        var shown = overdue.Concat(scheduled).Select(task => task.Id).ToHashSet();

        return new WorkPlanSummaryServiceModel(
            today,
            timeZoneId,
            read.Counts.Overdue,
            overdue,
            read.Counts.Today,
            scheduled,
            [.. read.TopThree.Where(shown.Contains)],
            new WorkPlanWeekServiceModel(
                weekStart,
                weekEnd,
                read.Counts.PlannedThisWeek,
                read.Counts.EstimatedThisWeek,
                read.Counts.EstimatedMinutesThisWeek,
                read.Counts.InProgress,
                read.Counts.Waiting,
                read.Counts.CompletedThisWeek),
            read.Counts.Unplanned);
    }

    private static WorkPlanTaskServiceModel ToTask(WorkTask task) =>
        new(task.Id, task.Title, task.Status, task.Priority, task.DueDate, task.ScheduledDate);
}
