namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>One unplanned task the daily view offers, and why (13A.25).</summary>
/// <remarks>
/// <strong>An offer, never an act.</strong> Suggesting a task schedules nothing, moves nothing and changes no
/// status; the creator plans it or ignores it. It is a type of its own, and arrives in an array of its own,
/// so a client cannot render a suggestion as though it were already part of the day by forgetting a flag.
/// </remarks>
public sealed record SuggestedWorkTaskServiceModel
{
    public required WorkTaskServiceModel Task { get; init; }

    /// <summary>Why it is worth a look, in one ordinary sentence. Derived from the task's own fields.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// One person's day in the resolved workspace (13A.25, TASK-002): what is late, what is planned for today,
/// what is under way, what is held up, and what they have finished since midnight.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The groups do not overlap.</strong> A task is placed by the first rule it matches — completed
/// today, then overdue, then in progress, then waiting, then scheduled today — so the day is not
/// double-counted and ticking one thing off does not make it vanish from two places. The precedence hides
/// nothing: every row carries its own status, due date and planned day, so a task in
/// <see cref="Overdue"/> still says it is in progress.
/// </para>
/// <para>
/// <strong>A task finished on an earlier day is in none of them</strong>, which is what makes this a view of
/// today rather than of everything.
/// </para>
/// </remarks>
public sealed record TodayWorkTasksServiceModel
{
    /// <summary>The local day this answer is about, in <see cref="TimeZoneId"/>.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>
    /// The zone the day was read in — the workspace's own scheduling zone. Always named, so a client never
    /// has to infer what "today" meant, and so a later per-person zone can change the answer visibly.
    /// </summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Not finished, and past its due date. The first thing the day asks about.</summary>
    public required IReadOnlyList<WorkTaskServiceModel> Overdue { get; init; }

    public required bool OverdueHasMore { get; init; }

    /// <summary>Not started, planned for today, and not already late.</summary>
    public required IReadOnlyList<WorkTaskServiceModel> ScheduledToday { get; init; }

    public required bool ScheduledTodayHasMore { get; init; }

    public required IReadOnlyList<WorkTaskServiceModel> InProgress { get; init; }

    public required bool InProgressHasMore { get; init; }

    /// <summary>Started, and held up by something outside the task.</summary>
    public required IReadOnlyList<WorkTaskServiceModel> Waiting { get; init; }

    public required bool WaitingHasMore { get; init; }

    /// <summary>Finished since local midnight. Newest first, because it reads as a record of the day.</summary>
    public required IReadOnlyList<WorkTaskServiceModel> CompletedToday { get; init; }

    public required bool CompletedTodayHasMore { get; init; }

    /// <summary>
    /// Unplanned backlog worth a look, when the caller asked for suggestions. Empty when they did not.
    /// </summary>
    public required IReadOnlyList<SuggestedWorkTaskServiceModel> Suggested { get; init; }

    /// <summary>
    /// The tasks the caller chose as today's top three (13A.32), by id and in their order. Empty until they choose.
    /// </summary>
    /// <remarks>
    /// Ids, never copies: each task is in exactly one group above, and this says which of them to put first. Only a
    /// task shown in a group is named, so a chosen task since handed to someone else, or finished on another day,
    /// drops out rather than pointing at nothing.
    /// </remarks>
    public required IReadOnlyList<Guid> TopThree { get; init; }
}
