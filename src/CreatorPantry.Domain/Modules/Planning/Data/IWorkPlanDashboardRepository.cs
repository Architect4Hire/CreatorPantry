using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>The one counting read the dashboard's work plan makes (13A.33a).</summary>
public interface IWorkPlanDashboardRepository
{
    /// <summary>
    /// Every task of the person's that the plan could count, grouped by the facts the plan groups by: a handful
    /// of rows however many tasks they have, and nothing counted per task (PERF-001). Which group each bucket
    /// belongs to is <see cref="WorkPlanTotals"/>'s to say, not this statement's.
    /// </summary>
    /// <remarks>
    /// <paramref name="weekStartsAtUtc"/> and <paramref name="weekEndsAtUtc"/> are the week's first and next
    /// Monday's local midnights as instants, end exclusive: "finished this week" is a question about instants,
    /// and the zone has already been resolved by the caller.
    /// </remarks>
    Task<IReadOnlyList<WorkPlanBucket>> BucketAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        CancellationToken cancellationToken);
}

/// <remarks>
/// Names the person and relies on the workspace query filter for the workspace, as every task read does: naming
/// them is what keeps one member's plan out of another's, and the filter is what keeps one workspace's out of
/// another's.
/// </remarks>
internal sealed class WorkPlanDashboardRepository(CreatorPantryDbContext context) : IWorkPlanDashboardRepository
{
    public async Task<IReadOnlyList<WorkPlanBucket>> BucketAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(WorkTaskWeek.Length - 1);

        return await context.WorkTasks.AsNoTracking()
            .Where(task => task.AssigneeMembershipId == assigneeMembershipId)

            // A task finished before this week is in no group the dashboard shows, and there is no bound on how
            // many of those a person has: left out of the statement rather than counted and thrown away.
            .Where(task => task.Status != WorkTaskStatus.Done
                || (task.CompletedAt >= weekStartsAtUtc && task.CompletedAt < weekEndsAtUtc))
            .GroupBy(task => new
            {
                task.Status,
                Overdue = task.DueDate != null && task.DueDate < today,
                ScheduledToday = task.ScheduledDate == today,
                PlannedThisWeek = task.PlanningWeekStart == weekStart
                    || (task.ScheduledDate >= weekStart && task.ScheduledDate <= weekEnd),

                // Waiting to be planned is My Week's own Unplanned column, and asking only about the week is
                // enough to match it: CK_WorkTasks_ScheduledDate_InWeek makes a day without the week holding it
                // impossible, so no week at all also means no day, and this group can never share a task with
                // PlannedThisWeek above.
                NoWeek = task.PlanningWeekStart == null,
            })
            .Select(bucket => new WorkPlanBucket(
                bucket.Key.Status,
                bucket.Key.Overdue,
                bucket.Key.ScheduledToday,
                bucket.Key.PlannedThisWeek,
                bucket.Key.NoWeek,
                bucket.Count(),
                bucket.Count(task => task.EstimateMinutes != null),
                bucket.Sum(task => (long?)task.EstimateMinutes)))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>The dashboard's work-plan reads, composed (13A.33a). Read-only: it owns no transaction and writes nothing.</summary>
public interface IWorkPlanDashboardDataLayer
{
    /// <summary>
    /// The counts, the two lists and today's top three: four statements, however much the person has planned.
    /// A list is asked for only when its count says there is something in it.
    /// </summary>
    Task<WorkPlanDashboardRead> ReadAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>The rows and counts of one person's work plan, as read.</summary>
public sealed record WorkPlanDashboardRead(
    WorkPlanCounts Counts,
    IReadOnlyList<WorkTask> Overdue,
    IReadOnlyList<WorkTask> Today,
    IReadOnlyList<Guid> TopThree);

internal sealed class WorkPlanDashboardDataLayer(
    IWorkPlanDashboardRepository counts,
    IWorkTaskRepository tasks,
    IWorkTaskFocusRepository focus) : IWorkPlanDashboardDataLayer
{
    public async Task<WorkPlanDashboardRead> ReadAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateOnly weekStart,
        DateTimeOffset weekStartsAtUtc,
        DateTimeOffset weekEndsAtUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var totals = WorkPlanTotals.From(await counts.BucketAsync(
            assigneeMembershipId, today, weekStart, weekStartsAtUtc, weekEndsAtUtc, cancellationToken));

        // The daily view's own reads, capped at the dashboard's limit: one order for a task's urgency, in one
        // place (13A.25). Overdue excludes nothing late; today's list excludes what is already late.
        IReadOnlyList<WorkTask> overdue = totals.Overdue == 0
            ? []
            : await tasks.OverdueAsync(assigneeMembershipId, today, limit, cancellationToken);

        IReadOnlyList<WorkTask> scheduled = totals.Today == 0
            ? []
            : await tasks.ScheduledOnAsync(assigneeMembershipId, today, limit, cancellationToken);

        var chosen = await focus.ChosenAsync(assigneeMembershipId, today, cancellationToken);

        return new WorkPlanDashboardRead(totals, overdue, scheduled, chosen);
    }
}
