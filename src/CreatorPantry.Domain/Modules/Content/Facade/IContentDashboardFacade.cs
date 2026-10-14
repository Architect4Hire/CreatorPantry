using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>What the dashboard asks the content module (13.11): the board's totals, what is planned soonest, and card names.</summary>
/// <remarks>Read by every member; the workspace query filter scopes it. Not cached.</remarks>
public interface IContentDashboardFacade
{
    /// <inheritdoc cref="IContentDashboardBusiness.GetBoardAsync"/>
    Task<ContentBoardSummaryServiceModel> GetBoardAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentDashboardBusiness.ListTitlesAsync"/>
    Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken);
}

internal sealed class ContentDashboardFacade(IContentDashboardBusiness business) : IContentDashboardFacade
{
    public Task<ContentBoardSummaryServiceModel> GetBoardAsync(
        DateOnly from, DateOnly through, int limit, CancellationToken cancellationToken) =>
        business.GetBoardAsync(from, through, limit, cancellationToken);

    public Task<IReadOnlyList<ContentCardTitleServiceModel>> ListTitlesAsync(
        IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken) =>
        business.ListTitlesAsync(cardIds, cancellationToken);
}
