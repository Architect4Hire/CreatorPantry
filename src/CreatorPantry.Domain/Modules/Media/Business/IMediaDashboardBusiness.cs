using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>The library's part of the workspace dashboard (13.11). Read-only.</summary>
public interface IMediaDashboardBusiness
{
    /// <summary>The pictures most recently added and not removed, newest first, at most <paramref name="limit"/> (1–10).</summary>
    Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class MediaDashboardBusiness(IMediaDashboardDataLayer dataLayer) : IMediaDashboardBusiness
{
    public Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        dataLayer.ListRecentAsync(Math.Clamp(limit, 1, 10), cancellationToken);
}
