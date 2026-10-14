using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>The bounded read the dashboard makes of the library (13.11). Served by <c>IX_MediaAssets_Workspace_Created</c>.</summary>
public interface IMediaDashboardRepository
{
    Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class MediaDashboardRepository(CreatorPantryDbContext context) : IMediaDashboardRepository
{
    public async Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await context.MediaAssets.AsNoTracking()
            .Where(asset => asset.DeletedAt == null)
            .OrderByDescending(asset => asset.CreatedAt)
            .ThenByDescending(asset => asset.Id)
            .Take(limit)

            // The asset row only: no version, so no stored bytes, size or address is even joined.
            .Select(asset => new RecentMediaUploadServiceModel(asset.Id, asset.Title, asset.Kind, asset.CreatedAt))
            .ToListAsync(cancellationToken);
}

public interface IMediaDashboardDataLayer
{
    Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class MediaDashboardDataLayer(IMediaDashboardRepository repository) : IMediaDashboardDataLayer
{
    public Task<IReadOnlyList<RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        repository.ListRecentAsync(limit, cancellationToken);
}
