namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>How much work a group holds, and how much of it anyone has estimated (13A.26).</summary>
/// <remarks>
/// <strong>An unknown stays unknown.</strong> A task with no estimate is counted in <see cref="TaskCount"/>
/// and nowhere else, so <c>TaskCount - EstimatedTaskCount</c> tasks are of unknown size. When none has an
/// estimate, <see cref="EstimatedMinutes"/> is null rather than zero: zero would claim the work takes no time.
/// </remarks>
public sealed record WorkTaskTotalsServiceModel
{
    /// <summary>Every task in the group, including any beyond the rows returned.</summary>
    public required int TaskCount { get; init; }

    public required int EstimatedTaskCount { get; init; }

    /// <summary>The sum of the estimates there are. Null when no task in the group has one.</summary>
    public long? EstimatedMinutes { get; init; }
}

/// <summary>One group of the week: its first rows, whether there were more, and what the whole group adds up to.</summary>
public sealed record WorkTaskGroupServiceModel
{
    public required IReadOnlyList<WorkTaskServiceModel> Tasks { get; init; }

    /// <summary>True when <see cref="Totals"/> counts more tasks than <see cref="Tasks"/> returns.</summary>
    public required bool HasMore { get; init; }

    public required WorkTaskTotalsServiceModel Totals { get; init; }
}

/// <summary>One local day of the week: the not-started tasks planned for it, in the person's own order.</summary>
public sealed record WorkTaskWeekDayServiceModel
{
    public required DateOnly Date { get; init; }

    public required DayOfWeek DayOfWeek { get; init; }

    /// <summary>Whether this is today in the workspace's zone.</summary>
    public required bool IsToday { get; init; }

    public required IReadOnlyList<WorkTaskServiceModel> Tasks { get; init; }

    public required bool HasMore { get; init; }

    public required WorkTaskTotalsServiceModel Totals { get; init; }
}

/// <summary>
/// One person's week in the resolved workspace (13A.26): each day's plan, the week's unscheduled backlog,
/// what is under way and held up, and what was finished — with what each adds up to where estimates exist.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The groups do not overlap</strong>, and none is a content-board column: this is the person's work,
/// and moving a task here moves no card.
/// </para>
/// <para>
/// <strong>In progress and waiting are the person's now, in every week.</strong> A task started last week is
/// still under way, so those two groups are not tied to the week being read.
/// </para>
/// </remarks>
public sealed record WeekWorkTasksServiceModel
{
    /// <summary>The Monday the week starts on.</summary>
    public required DateOnly WeekStart { get; init; }

    /// <summary>The Sunday it ends on.</summary>
    public required DateOnly WeekEnd { get; init; }

    /// <summary>Today in <see cref="TimeZoneId"/>, whether or not it falls in this week.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>The zone the week's edges were read in — the workspace's own scheduling zone.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Whether this is the week holding <see cref="Today"/>, and so the one that carries work over.</summary>
    public required bool IsCurrentWeek { get; init; }

    /// <summary>
    /// The week's own planned work: the seven days and <see cref="Backlog"/>. Not the unplanned or carried-over
    /// tasks, which nobody has planned for this week, and not the status groups, which are not tied to it.
    /// </summary>
    public required WorkTaskTotalsServiceModel Planned { get; init; }

    /// <summary>Monday to Sunday, always seven.</summary>
    public required IReadOnlyList<WorkTaskWeekDayServiceModel> Days { get; init; }

    /// <summary>Not started, planned for this week, and not for any one day of it.</summary>
    public required WorkTaskGroupServiceModel Backlog { get; init; }

    /// <summary>Not started and planned for no week at all — captured where no calendar was set, or never planned.</summary>
    public required WorkTaskGroupServiceModel Unplanned { get; init; }

    /// <summary>
    /// Not started and planned for an earlier week, oldest first. Only in the current week; empty in any other,
    /// so a week in the past still shows what was planned for it and a week ahead is not filled with leftovers.
    /// </summary>
    public required WorkTaskGroupServiceModel CarriedOver { get; init; }

    public required WorkTaskGroupServiceModel InProgress { get; init; }

    /// <summary>Started, and held up by something outside the task.</summary>
    public required WorkTaskGroupServiceModel Waiting { get; init; }

    /// <summary>Finished between the week's first and last local midnight, newest first.</summary>
    public required WorkTaskGroupServiceModel Completed { get; init; }
}
