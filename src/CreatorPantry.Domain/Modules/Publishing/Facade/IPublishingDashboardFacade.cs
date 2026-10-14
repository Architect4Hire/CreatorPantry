using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Facade;

/// <summary>What the dashboard asks the publishing module (13.11): where the ledger stands.</summary>
/// <remarks>Read by every member; the workspace query filter scopes it. Not cached.</remarks>
public interface IPublishingDashboardFacade
{
    /// <inheritdoc cref="IPublishingDashboardBusiness.GetSummaryAsync"/>
    Task<PublishingSummaryServiceModel> GetSummaryAsync(
        DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken);
}

internal sealed class PublishingDashboardFacade(IPublishingDashboardBusiness business) : IPublishingDashboardFacade
{
    public Task<PublishingSummaryServiceModel> GetSummaryAsync(
        DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken) =>
        business.GetSummaryAsync(from, before, limit, cancellationToken);
}
