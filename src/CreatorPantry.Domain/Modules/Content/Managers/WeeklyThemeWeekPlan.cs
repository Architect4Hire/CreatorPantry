namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a replace does to one theme: the state the creator asked for, by key.
/// </summary>
/// <param name="Key">Identifies the theme. A key the workspace already has names that row; a new one is a new theme.</param>
/// <param name="Day">The day it ends up on.</param>
/// <param name="DisplayName">The creator's words, already trimmed.</param>
/// <param name="Description">The creator's note, already trimmed, or null.</param>
/// <param name="Retire">
/// True when this theme is leaving the live week — it was not in the submitted set. Its row and its key survive;
/// see <c>WorkspaceWeeklyTheme</c>.
/// </param>
public sealed record WeeklyThemeChange(
    string Key,
    DayOfWeek Day,
    string DisplayName,
    string? Description,
    bool Retire);

/// <summary>
/// Everything one replace changes, as plain data rather than as mutations already made.
/// </summary>
/// <remarks>
/// <para>
/// Plain data on purpose. The write needs two ordered statement batches, so the data layer owns a transaction
/// and may be re-run by a retrying execution strategy; a plan of values can be applied again from a clean change
/// tracker, where a graph of entities Business had already mutated could not.
/// </para>
/// <para>
/// A theme that is unchanged is absent. An empty <see cref="Changes"/> list therefore means a replace that asked
/// for the week the workspace already has, which writes nothing at all.
/// </para>
/// </remarks>
/// <param name="At">The instant the whole replace is stamped with.</param>
public sealed record WeeklyThemeWeekPlan(DateTimeOffset At, IReadOnlyList<WeeklyThemeChange> Changes);
