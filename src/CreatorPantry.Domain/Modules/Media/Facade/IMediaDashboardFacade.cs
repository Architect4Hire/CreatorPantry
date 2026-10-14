using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>What the dashboard asks the library (13.11): what was added most recently.</summary>
/// <remarks>Read by every member; the workspace query filter scopes it. Not cached.</remarks>
public interface IMediaDashboardFacade
{
    /// <inheritdoc cref="IMediaDashboardBusiness.ListRecentAsync"/>
    Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class MediaDashboardFacade(IMediaDashboardBusiness business) : IMediaDashboardFacade
{
    public Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        business.ListRecentAsync(limit, cancellationToken);
}
