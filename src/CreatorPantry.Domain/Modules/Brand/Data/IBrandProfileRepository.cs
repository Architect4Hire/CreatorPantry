using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandProfileRepository
{
    /// <summary>
    /// The resolved workspace's profile with its channel defaults, links and asset links, or null when it has
    /// none. No-tracking. Takes no workspace id: the query filter scopes it, so another workspace's profile is
    /// indistinguishable from none at all.
    /// </summary>
    Task<BrandProfile?> GetAsync(CancellationToken cancellationToken);

    /// <summary>The same read, tracked, so Business can change the aggregate and the layer below can save it.</summary>
    Task<BrandProfile?> GetForUpdateAsync(CancellationToken cancellationToken);

    /// <summary>Stages a new profile with its children. Nothing is saved.</summary>
    void Add(BrandProfile profile);

    /// <summary>Stages a revision row. Nothing is saved.</summary>
    void Add(BrandProfileRevision revision);

    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>The profile's stored row version right now, or null if it no longer exists.</summary>
    Task<byte[]?> CurrentRowVersionAsync(Guid profileId, CancellationToken cancellationToken);
}

internal sealed class BrandProfileRepository(CreatorPantryDbContext context) : IBrandProfileRepository
{
    public Task<BrandProfile?> GetAsync(CancellationToken cancellationToken) =>
        context.BrandProfiles
            .AsNoTracking()
            .AsSplitQuery()
            .Include(profile => profile.ChannelDefaults)
            .Include(profile => profile.Links)
            .Include(profile => profile.AssetLinks)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<BrandProfile?> GetForUpdateAsync(CancellationToken cancellationToken) =>
        context.BrandProfiles
            .AsSplitQuery()
            .Include(profile => profile.ChannelDefaults)
            .Include(profile => profile.Links)
            .Include(profile => profile.AssetLinks)
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(BrandProfile profile) => context.BrandProfiles.Add(profile);

    public void Add(BrandProfileRevision revision) => context.BrandProfileRevisions.Add(revision);

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
        context.BrandProfiles.AnyAsync(cancellationToken);

    public async Task<byte[]?> CurrentRowVersionAsync(Guid profileId, CancellationToken cancellationToken) =>
        await context.BrandProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);
}
