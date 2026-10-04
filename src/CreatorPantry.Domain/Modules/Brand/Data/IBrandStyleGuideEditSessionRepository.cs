using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandStyleGuideEditSessionRepository
{
    /// <summary>
    /// The given user's draft of one guide in the resolved workspace, no-tracking, or null.
    /// </summary>
    /// <remarks>
    /// Three things scope it, and all three are server-side: the workspace filter, the guide from the route,
    /// and the user id Business takes from the resolved context. A request names none of them.
    /// </remarks>
    Task<BrandStyleGuideEditSession?> GetAsync(Guid guideId, string userId, CancellationToken cancellationToken);

    /// <summary>The same read, tracked, for a write.</summary>
    Task<BrandStyleGuideEditSession?> GetForUpdateAsync(
        Guid guideId, string userId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid guideId, string userId, CancellationToken cancellationToken);

    /// <summary>Stages a new draft. Nothing is saved.</summary>
    void Add(BrandStyleGuideEditSession session);

    /// <summary>Stages the removal of a tracked draft. Nothing is saved.</summary>
    void Remove(BrandStyleGuideEditSession session);

    /// <summary>The stored row version right now, or null if the row is gone.</summary>
    Task<byte[]?> CurrentRowVersionAsync(Guid sessionId, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideEditSessionRepository(CreatorPantryDbContext context)
    : IBrandStyleGuideEditSessionRepository
{
    public Task<BrandStyleGuideEditSession?> GetAsync(
        Guid guideId, string userId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideEditSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                session => session.BrandStyleGuideId == guideId && session.UserId == userId, cancellationToken);

    public Task<BrandStyleGuideEditSession?> GetForUpdateAsync(
        Guid guideId, string userId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideEditSessions
            .SingleOrDefaultAsync(
                session => session.BrandStyleGuideId == guideId && session.UserId == userId, cancellationToken);

    public Task<bool> ExistsAsync(Guid guideId, string userId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideEditSessions
            .AnyAsync(session => session.BrandStyleGuideId == guideId && session.UserId == userId, cancellationToken);

    public void Add(BrandStyleGuideEditSession session) => context.BrandStyleGuideEditSessions.Add(session);

    public void Remove(BrandStyleGuideEditSession session) => context.BrandStyleGuideEditSessions.Remove(session);

    public async Task<byte[]?> CurrentRowVersionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await context.BrandStyleGuideEditSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => session.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);
}
