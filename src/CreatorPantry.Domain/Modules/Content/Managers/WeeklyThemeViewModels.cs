namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>One day of the week the creator is describing.</summary>
/// <remarks>
/// Every field is nullable so a missing one is distinguishable from a default: <see cref="Day"/> in particular,
/// because <see cref="DayOfWeek"/>'s default is Sunday and an omitted day would otherwise arrive as a real one.
/// </remarks>
public sealed record WeeklyThemeInput
{
    public DayOfWeek? Day { get; init; }

    /// <summary>Stable and lowercase. Kept across renames, so it is the creator's to choose once.</summary>
    public string? Key { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// The creator's whole week, replacing whatever it held. Carries no workspace: that comes from the route and
/// the caller's membership.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One write for the whole set, not CRUD per theme.</strong> At most seven day-keyed rows make the week
/// a single value, which keeps the write idempotent, lets a creator clear a day by leaving it out, and keeps
/// "one theme per day" an invariant of one transaction rather than a race between three endpoints.
/// </para>
/// <para>
/// A theme already in the week is recognised by its <see cref="WeeklyThemeInput.Key"/> and kept — its day, name
/// and description are updated and it is brought back out of retirement if it was there. A live theme whose key
/// is absent is retired, not deleted, so any record that already stored its key keeps resolving.
/// </para>
/// </remarks>
public sealed record ReplaceWeeklyThemesViewModel
{
    public IReadOnlyList<WeeklyThemeInput?>? Themes { get; init; }
}
