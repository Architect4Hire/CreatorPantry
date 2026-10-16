namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>Bounds on what a dashboard summary of this module reads.</summary>
public static class WorkPlanDashboardPolicy
{
    /// <summary>
    /// The most tasks either list names, however many the day holds. Three: the dashboard answers "what next",
    /// and the fourth row is My Day's job (13A.25).
    /// </summary>
    public const int ListLimit = 3;
}

/// <summary>
/// One task as the dashboard names it (13A.33a): enough to recognise and to judge how much it presses, and
/// nothing else.
/// </summary>
/// <remarks>
/// No description, no label, no estimate and nothing of what it links to — those are My Day's and the task
/// drawer's (13A.25, 13A.27). The title is the person's own words, as they typed them.
/// </remarks>
public sealed record WorkPlanTaskServiceModel(
    Guid Id,
    string Title,
    WorkTaskStatus Status,
    WorkTaskPriority Priority,
    DateOnly? DueDate,
    DateOnly? ScheduledDate);

/// <summary>
/// The week the day falls in, as totals only: no day-by-day breakdown, which is My Week's (13A.26).
/// </summary>
/// <remarks>
/// <strong>An unknown stays unknown</strong>, as it does in the weekly view: a task with no estimate is in
/// <see cref="PlannedTaskCount"/> and nowhere else, and with nothing estimated <see cref="EstimatedMinutes"/> is
/// null rather than zero, because zero would claim the work takes no time.
/// </remarks>
/// <param name="PlannedTaskCount">Not started and planned for this week, whether or not for one of its days.</param>
/// <param name="InProgressCount">
/// Under way, in whatever week it was planned for: the person's now, as My Week reads it. The week's counts are
/// the weekly view's frame, so a task the day lists as late is counted here too if it is under way — the day's
/// groups are what do not overlap.
/// </param>
/// <param name="CompletedCount">Finished between this week's first and last local midnight.</param>
public sealed record WorkPlanWeekServiceModel(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int PlannedTaskCount,
    int EstimatedTaskCount,
    long? EstimatedMinutes,
    int InProgressCount,
    int WaitingCount,
    int CompletedCount);

/// <summary>
/// The caller's own work plan, for the dashboard (13A.33a, TASK-002): what is late, what today holds, what the
/// week adds up to, and what has been captured and not yet planned.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The caller's, not the workspace's.</strong> Every count and row is keyed on the caller's membership,
/// so two members of one workspace are answered differently — a to-do list is one person's.
/// </para>
/// <para>
/// <strong>The groups do not overlap</strong>, under the daily view's own precedence (13A.25): a task late and
/// planned for today is counted as late and nowhere else, so ticking one thing off does not take it out of two
/// places. Every row still carries its own status, due day and planned day.
/// </para>
/// <para>
/// <strong>Read-only, and no second account of anything.</strong> Nothing here is stored, and a count is never
/// derived from a list: each list is capped at <see cref="WorkPlanDashboardPolicy.ListLimit"/> while the count
/// beside it is the whole count.
/// </para>
/// </remarks>
/// <param name="Date">The local day this answer is about.</param>
/// <param name="TimeZoneId">The zone that day was read in — the workspace's own scheduling zone.</param>
/// <param name="OverdueCount">Not finished and past its due day, however many are listed.</param>
/// <param name="TodayCount">Not started, planned for today, and not already late.</param>
/// <param name="TopThree">
/// The tasks the caller chose as today's top three (13A.32), by id and in their order, and only those among the
/// rows above — as the daily view does, so a chosen task that is not on show here points at nothing.
/// </param>
/// <param name="UnplannedCount">
/// Not started and planned for no week at all: what quick capture has taken in and nobody has planned (13A.31).
/// My Week's Unplanned column exactly, which is the week's frame rather than the day's — a task that is late and
/// was never planned is in both <paramref name="OverdueCount"/> and here, because it is late <em>and</em> the
/// thing to do about it is to plan it.
/// </param>
public sealed record WorkPlanSummaryServiceModel(
    DateOnly Date,
    string TimeZoneId,
    int OverdueCount,
    IReadOnlyList<WorkPlanTaskServiceModel> Overdue,
    int TodayCount,
    IReadOnlyList<WorkPlanTaskServiceModel> Today,
    IReadOnlyList<Guid> TopThree,
    WorkPlanWeekServiceModel ThisWeek,
    int UnplannedCount);
