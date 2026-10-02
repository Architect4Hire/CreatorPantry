using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandSetupSessionRepository
{
    /// <summary>
    /// The given user's session in the resolved workspace, no-tracking, or null. The workspace filter scopes
    /// it; the user id is the caller's own, supplied by Business from the resolved context.
    /// </summary>
    Task<BrandSetupSession?> GetAsync(string userId, CancellationToken cancellationToken);

    /// <summary>The same read, tracked, for a write.</summary>
    Task<BrandSetupSession?> GetForUpdateAsync(string userId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Stages a new session. Nothing is saved.</summary>
    void Add(BrandSetupSession session);

    /// <summary>Stages the removal of a tracked session. Nothing is saved.</summary>
    void Remove(BrandSetupSession session);

    /// <summary>The stored row version right now, or null if the row is gone.</summary>
    Task<byte[]?> CurrentRowVersionAsync(Guid sessionId, CancellationToken cancellationToken);
}

internal sealed class BrandSetupSessionRepository(CreatorPantryDbContext context) : IBrandSetupSessionRepository
{
    public Task<BrandSetupSession?> GetAsync(string userId, CancellationToken cancellationToken) =>
        context.BrandSetupSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(session => session.UserId == userId, cancellationToken);

    public Task<BrandSetupSession?> GetForUpdateAsync(string userId, CancellationToken cancellationToken) =>
        context.BrandSetupSessions.SingleOrDefaultAsync(session => session.UserId == userId, cancellationToken);

    public Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken) =>
        context.BrandSetupSessions.AnyAsync(session => session.UserId == userId, cancellationToken);

    public void Add(BrandSetupSession session) => context.BrandSetupSessions.Add(session);

    public void Remove(BrandSetupSession session) => context.BrandSetupSessions.Remove(session);

    public async Task<byte[]?> CurrentRowVersionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await context.BrandSetupSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => session.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);
}
