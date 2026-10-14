using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Business;

/// <summary>The publishing module's part of the workspace dashboard (13.11). Read-only: it reports the ledger and changes nothing.</summary>
public interface IPublishingDashboardBusiness
{
    /// <summary>Counts per state, what needs attention, and what is scheduled from <paramref name="from"/> up to <paramref name="before"/>.</summary>
    Task<PublishingSummaryServiceModel> GetSummaryAsync(
        DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken);
}

internal sealed class PublishingDashboardBusiness(IPublishingDashboardDataLayer dataLayer) : IPublishingDashboardBusiness
{
    public Task<PublishingSummaryServiceModel> GetSummaryAsync(
        DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, before);

        return dataLayer.GetSummaryAsync(from, before, Math.Clamp(limit, 1, 10), cancellationToken);
    }
}
