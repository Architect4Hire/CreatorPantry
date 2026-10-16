namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>What every group of the dashboard's work plan holds (13A.33a). An empty plan is all zeros, no minutes.</summary>
/// <param name="EstimatedMinutesThisWeek">Null when nothing planned for the week carries an estimate.</param>
public sealed record WorkPlanCounts(
    int Overdue,
    int Today,
    int PlannedThisWeek,
    int EstimatedThisWeek,
    long? EstimatedMinutesThisWeek,
    int InProgress,
    int Waiting,
    int CompletedThisWeek,
    int Unplanned)
{
    public static readonly WorkPlanCounts None = new(0, 0, 0, 0, null, 0, 0, 0, 0);
}

/// <summary>
/// One group of a person's tasks as the database counts them: the facts a task is grouped by, and how many tasks
/// share them. A read returns a handful of these however much the person has planned, and the rules that turn
/// them into the plan's totals are <see cref="WorkPlanTotals"/>'s, not the statement's.
/// </summary>
/// <param name="Estimated">How many of <paramref name="Tasks"/> carry an estimate at all.</param>
/// <param name="Minutes">The estimates summed, which is null only when none of them carries one.</param>
public sealed record WorkPlanBucket(
    WorkTaskStatus Status,
    bool Overdue,
    bool ScheduledToday,
    bool PlannedThisWeek,
    bool NoWeek,
    int Tasks,
    int Estimated,
    long? Minutes);

/// <summary>
/// The one place that says which group of the dashboard's work plan a task counts in. A pure fold over the
/// buckets a read returns, so the rule is named, testable on its own, and stated once: it used to sit inside the
/// counting statement, where it was a product decision in a repository and a second copy of the daily view's
/// precedence.
/// </summary>
public static class WorkPlanTotals
{
    /// <summary>
    /// The plan's totals. The daily view's precedence holds (13A.25): late first, and a task counted as late is
    /// counted nowhere else. "Planned this week" and "waiting to be planned" are the same two groups My Week
    /// shows, and a task is in at most one of them.
    /// </summary>
    public static WorkPlanCounts From(IEnumerable<WorkPlanBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        var overdue = 0;
        var today = 0;
        var planned = 0;
        var estimated = 0;
        long? minutes = null;
        var inProgress = 0;
        var waiting = 0;
        var completed = 0;
        var unplanned = 0;

        foreach (var bucket in buckets)
        {
            var unfinished = bucket.Status != WorkTaskStatus.Done;
            var notStarted = bucket.Status == WorkTaskStatus.Backlog;

            if (unfinished && bucket.Overdue)
            {
                overdue += bucket.Tasks;
            }
            else if (notStarted && bucket.ScheduledToday)
            {
                today += bucket.Tasks;
            }

            if (notStarted && bucket.PlannedThisWeek)
            {
                planned += bucket.Tasks;
                estimated += bucket.Estimated;

                // Summing nullable minutes gives zero when every value is null, in LINQ and in SQL alike. Zero
                // would claim the work takes no time, so nothing estimated leaves this null.
                if (bucket.Estimated > 0)
                {
                    minutes = (minutes ?? 0) + (bucket.Minutes ?? 0);
                }
            }

            if (notStarted && bucket.NoWeek)
            {
                unplanned += bucket.Tasks;
            }

            switch (bucket.Status)
            {
                case WorkTaskStatus.InProgress: inProgress += bucket.Tasks; break;
                case WorkTaskStatus.Waiting: waiting += bucket.Tasks; break;

                // Anything still counted here was finished inside the week; the read leaves the rest out.
                case WorkTaskStatus.Done: completed += bucket.Tasks; break;
            }
        }

        return new WorkPlanCounts(
            overdue, today, planned, estimated, minutes, inProgress, waiting, completed, unplanned);
    }
}
