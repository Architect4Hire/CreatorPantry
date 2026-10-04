namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// One of the workspace's weekly themes as the application reports it.
/// </summary>
/// <param name="Day">The day it runs on. A retired theme keeps the day it last held.</param>
/// <param name="Key">The stable value other records store.</param>
/// <param name="DisplayName">The creator's own words.</param>
/// <param name="Description">The creator's own note, or null.</param>
/// <param name="RetiredAt">
/// When the theme left the live week, or null while it is in it. A retired theme is reported rather than hidden,
/// so a client can both resolve a key a record stored months ago and offer to bring the theme back.
/// </param>
public sealed record WeeklyThemeServiceModel(
    DayOfWeek Day,
    string Key,
    string DisplayName,
    string? Description,
    DateTimeOffset? RetiredAt,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The workspace's weekly themes: the live week first, Monday through Sunday, then the retired ones.
/// </summary>
/// <remarks>
/// Not paged, and a workspace with none answers with an empty list rather than a 404 — an empty week is a real
/// state a creator starts in, and a consumer reading it degrades to no theme for the day.
/// </remarks>
public sealed record WeeklyThemeWeekServiceModel(IReadOnlyList<WeeklyThemeServiceModel> Themes);
