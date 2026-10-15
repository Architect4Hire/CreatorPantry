namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>Where one person's piece of work stands (13A.23).</summary>
/// <remarks>
/// <para>
/// <strong>The person's work, not the content's.</strong> Nothing here is an editorial or publishing state —
/// no review, approval, schedule or delivery — and none may be added. The content board's columns and a
/// publication's lifecycle own those; a task about a card says what someone is doing about it, and moving the
/// task moves neither.
/// </para>
/// <para>
/// <strong>Which column a not-started task shows in is not a status.</strong> A <see cref="Backlog"/> task with
/// a day is in that day's column of the week and on that day's list; one without is in the backlog. The day
/// decides, so planning a task never means changing its status.
/// </para>
/// <para>
/// <strong>Append only; never renumber.</strong> The stored values are these numbers.
/// </para>
/// </remarks>
public enum WorkTaskStatus
{
    /// <summary>Not started. Where every task begins (13A.24).</summary>
    Backlog = 1,

    InProgress = 2,

    /// <summary>Started, and held up by something outside the task.</summary>
    Waiting = 3,

    /// <summary>Completed (13A.28b). Reopening restores the status it was completed from.</summary>
    Done = 4,
}

/// <summary>How much a task matters next to the person's others. A sort and an emphasis, never a deadline.</summary>
/// <remarks>Append only; never renumber.</remarks>
public enum WorkTaskPriority
{
    Low = 1,

    Normal = 2,

    High = 3,
}
