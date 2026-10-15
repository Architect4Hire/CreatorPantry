namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>
/// Which seven days a planning week is. One place, because the week is written onto every task that has one
/// and a second opinion about where a week starts would put two tasks of the same week in different weeks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Monday, fixed (13A.24).</strong> ISO-8601, and a constant rather than a setting for now. The week
/// is <em>stored</em> on the row rather than derived when it is read, so if a later prompt makes the start
/// day a workspace preference, nothing already planned moves: those tasks keep the week they were captured
/// into, and only new ones follow the new start.
/// </para>
/// <para>
/// There is no zone here. A week is seven calendar dates; which date is "today" is the caller's to decide
/// from the workspace's own zone before asking anything of this type.
/// </para>
/// </remarks>
public static class WorkTaskWeek
{
    /// <summary>The weekday every planning week begins on.</summary>
    public const DayOfWeek StartsOn = DayOfWeek.Monday;

    /// <summary>The number of days in a planning week, so the last day is <c>Start + Length - 1</c>.</summary>
    public const int Length = 7;

    /// <summary>The first day of the week holding <paramref name="date"/>.</summary>
    public static DateOnly StartOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)StartsOn + Length) % Length));

    /// <summary>True when <paramref name="date"/> is itself the first day of a week.</summary>
    public static bool IsStart(DateOnly date) => date.DayOfWeek == StartsOn;

    /// <summary>True when <paramref name="date"/> falls inside the week beginning <paramref name="weekStart"/>.</summary>
    public static bool Holds(DateOnly weekStart, DateOnly date) =>
        date >= weekStart && date <= weekStart.AddDays(Length - 1);
}
