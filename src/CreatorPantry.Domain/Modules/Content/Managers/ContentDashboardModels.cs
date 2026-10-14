namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>One card planned for a day in the window.</summary>
public sealed record UpcomingContentCardServiceModel(
    Guid Id, string Title, ContentCardColumn Column, DateOnly PlannedDate, string? ChannelKey);

/// <summary>The board for the dashboard: every column's count, and the cards planned soonest.</summary>
/// <param name="Columns">All four columns, each with its count — zero included.</param>
/// <param name="UpcomingCount">Cards planned for a day in the window, however many are listed.</param>
/// <param name="Upcoming">The soonest of them, at most the limit asked for.</param>
public sealed record ContentBoardSummaryServiceModel(
    IReadOnlyList<ContentCardColumnCountServiceModel> Columns,
    int UpcomingCount,
    IReadOnlyList<UpcomingContentCardServiceModel> Upcoming);

/// <summary>A card's title, for a summary that names cards by id.</summary>
public sealed record ContentCardTitleServiceModel(Guid Id, string Title);
