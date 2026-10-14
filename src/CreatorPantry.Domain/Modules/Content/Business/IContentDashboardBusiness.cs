using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>The content module's part of the workspace dashboard (13.11). Read-only.</summary>
public interface IContentDashboardBusiness
{
    /// <summary>Every column's count, and the cards planned from <paramref name="from"/> through <paramref name="through"/>.</summary>
    Task<ContentBoardSummaryServiceModel> GetBoardAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The titles of up to ten cards. One the resolved workspace cannot see is absent, not an error, so asking
    /// cannot be used to learn that another workspace's card exists.
    /// </summary>
    Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken);
}

internal sealed class ContentDashboardBusiness(IContentDashboardDataLayer dataLayer) : IContentDashboardBusiness
{
    private const int MaxTitles = 10;

    public Task<ContentBoardSummaryServiceModel> GetBoardAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, through);

        return dataLayer.GetBoardAsync(from, through, Math.Clamp(limit, 1, 10), cancellationToken);
    }

    public Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardIds);

        return dataLayer.ListTitlesAsync([.. cardIds.Distinct().Take(MaxTitles)], cancellationToken);
    }
}
