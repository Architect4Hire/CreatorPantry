using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>
/// The bounded reads the dashboard makes of the content board (13.11). The column counts are one grouped
/// query over <c>IX_ContentCards_Workspace_Column_Rank</c>; the window is <c>IX_ContentCards_Workspace_PlannedDate</c>.
/// </summary>
public interface IContentDashboardRepository
{
    Task<IReadOnlyDictionary<ContentCardColumn, int>> CountByColumnAsync(CancellationToken cancellationToken);

    Task<int> CountPlannedAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken);

    Task<IReadOnlyList<UpcomingContentCardServiceModel>> ListPlannedAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken);
}

internal sealed class ContentDashboardRepository(CreatorPantryDbContext context) : IContentDashboardRepository
{
    public async Task<IReadOnlyDictionary<ContentCardColumn, int>> CountByColumnAsync(CancellationToken cancellationToken) =>
        await context.ContentCards.AsNoTracking()
            .GroupBy(card => card.Column)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

    public Task<int> CountPlannedAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
        context.ContentCards.AsNoTracking()
            .CountAsync(card => card.PlannedDate >= from && card.PlannedDate <= through, cancellationToken);

    public async Task<IReadOnlyList<UpcomingContentCardServiceModel>> ListPlannedAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken) =>
        await context.ContentCards.AsNoTracking()
            .Where(card => card.PlannedDate >= from && card.PlannedDate <= through)
            .OrderBy(card => card.PlannedDate)
            .ThenBy(card => card.Column)
            .ThenBy(card => card.SortRank)
            .ThenBy(card => card.Id)
            .Take(limit)
            .Select(card => new UpcomingContentCardServiceModel(card.Id, card.Title, card.Column, card.PlannedDate!.Value, card.ChannelKey))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken) =>
        cardIds.Count == 0
            ? []
            : await context.ContentCards.AsNoTracking()
                .Where(card => cardIds.Contains(card.Id))
                .Select(card => new ContentCardTitleServiceModel(card.Id, card.Title))
                .ToListAsync(cancellationToken);
}

public interface IContentDashboardDataLayer
{
    Task<ContentBoardSummaryServiceModel> GetBoardAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken);
}

internal sealed class ContentDashboardDataLayer(IContentDashboardRepository repository) : IContentDashboardDataLayer
{
    public async Task<ContentBoardSummaryServiceModel> GetBoardAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken)
    {
        var counts = await repository.CountByColumnAsync(cancellationToken);

        return new ContentBoardSummaryServiceModel(
            [.. Enum.GetValues<ContentCardColumn>().Order().Select(column =>
                new ContentCardColumnCountServiceModel(column, counts.TryGetValue(column, out var count) ? count : 0))],
            await repository.CountPlannedAsync(from, through, cancellationToken),
            await repository.ListPlannedAsync(from, through, limit, cancellationToken));
    }

    public Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken) =>
        repository.ListTitlesAsync(cardIds, cancellationToken);
}
