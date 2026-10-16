using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// The rule that says which group of the dashboard's work plan a task counts in (13A.33a). It used to live
/// inside the counting statement, where it was a product decision in a repository and a second copy of the
/// daily view's precedence; here it is one named function, tested without a database.
/// </summary>
public sealed class WorkPlanTotalsTests
{
    private static WorkPlanBucket Bucket(
        WorkTaskStatus status,
        bool overdue = false,
        bool scheduledToday = false,
        bool plannedThisWeek = false,
        bool noWeek = false,
        int tasks = 1,
        int estimated = 0,
        long? minutes = null) =>
        new(status, overdue, scheduledToday, plannedThisWeek, noWeek, tasks, estimated, minutes);

    [Fact]
    public void An_empty_plan_is_all_zeros_and_no_minutes() =>
        Assert.Equal(WorkPlanCounts.None, WorkPlanTotals.From([]));

    [Fact]
    public void A_late_task_is_counted_late_and_nowhere_else()
    {
        // The daily view's precedence (13A.25): a task that is late and also sitting on today is one thing to
        // do, not two, and "late" is the one that tells the creator what to do about it.
        var totals = WorkPlanTotals.From([
            Bucket(WorkTaskStatus.Backlog, overdue: true, scheduledToday: true),
        ]);

        Assert.Equal(1, totals.Overdue);
        Assert.Equal(0, totals.Today);
    }

    [Fact]
    public void A_task_in_progress_or_waiting_is_never_counted_as_today_however_it_is_planned()
    {
        var totals = WorkPlanTotals.From([
            Bucket(WorkTaskStatus.InProgress, scheduledToday: true, plannedThisWeek: true),
            Bucket(WorkTaskStatus.Waiting, scheduledToday: true, plannedThisWeek: true),
        ]);

        Assert.Equal(0, totals.Today);
        Assert.Equal(0, totals.PlannedThisWeek);
        Assert.Equal(1, totals.InProgress);
        Assert.Equal(1, totals.Waiting);
    }

    [Fact]
    public void Nothing_estimated_leaves_the_minutes_unknown_rather_than_claiming_no_time()
    {
        var totals = WorkPlanTotals.From([
            Bucket(WorkTaskStatus.Backlog, plannedThisWeek: true, tasks: 3, estimated: 0, minutes: null),
        ]);

        Assert.Equal(3, totals.PlannedThisWeek);
        Assert.Equal(0, totals.EstimatedThisWeek);
        Assert.Null(totals.EstimatedMinutesThisWeek);
    }

    [Fact]
    public void Estimates_are_summed_across_the_groups_that_carry_them()
    {
        var totals = WorkPlanTotals.From([
            Bucket(WorkTaskStatus.Backlog, plannedThisWeek: true, tasks: 2, estimated: 2, minutes: 90),
            Bucket(WorkTaskStatus.Backlog, plannedThisWeek: true, tasks: 4, estimated: 1, minutes: 30),
        ]);

        Assert.Equal(6, totals.PlannedThisWeek);
        Assert.Equal(3, totals.EstimatedThisWeek);
        Assert.Equal(120, totals.EstimatedMinutesThisWeek);
    }

    [Fact]
    public void Planned_and_waiting_to_be_planned_never_claim_the_same_task()
    {
        // The read decides this: a task with no week of its own but a day inside this week is planned, and so
        // is excluded from NoWeek. Were both ever true at once, the two columns would double-count it.
        var totals = WorkPlanTotals.From([
            Bucket(WorkTaskStatus.Backlog, plannedThisWeek: true, noWeek: false),
            Bucket(WorkTaskStatus.Backlog, plannedThisWeek: false, noWeek: true),
        ]);

        Assert.Equal(1, totals.PlannedThisWeek);
        Assert.Equal(1, totals.Unplanned);
    }

    [Fact]
    public void A_finished_task_counts_only_as_finished()
    {
        var totals = WorkPlanTotals.From([
            Bucket(WorkTaskStatus.Done, overdue: true, scheduledToday: true, plannedThisWeek: true, noWeek: true),
        ]);

        Assert.Equal(1, totals.CompletedThisWeek);
        Assert.Equal(0, totals.Overdue);
        Assert.Equal(0, totals.Today);
        Assert.Equal(0, totals.PlannedThisWeek);
        Assert.Equal(0, totals.Unplanned);
    }
}
