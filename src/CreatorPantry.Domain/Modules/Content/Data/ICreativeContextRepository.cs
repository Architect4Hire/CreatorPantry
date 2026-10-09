using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>EF Core access to creative contexts. Every read is inside the workspace query filter.</summary>
public interface ICreativeContextRepository
{
    void Add(CreativeContext context);

    /// <summary>The context with its channels and references, untracked, or null.</summary>
    /// <remarks>
    /// Null for an id this workspace has never held and for another workspace's context — one answer, because
    /// the global query filter means the neighbour's row is not in the set this reads over.
    /// </remarks>
    Task<CreativeContext?> FindAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>
    /// The same aggregate, tracked, for an edit.
    /// </summary>
    /// <remarks>
    /// Tracked because the row version has to be the one the database holds: it goes into the root's
    /// <c>UPDATE</c>, which is what refuses a write composed against an earlier read.
    /// </remarks>
    Task<CreativeContext?> FindForUpdateAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>Live contexts, most recently updated first, one row past the limit.</summary>
    Task<(IReadOnlyList<CreativeContextSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        CreativeContextListCriteria criteria, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextRepository"/>
internal sealed class CreativeContextRepository(CreatorPantryDbContext context) : ICreativeContextRepository
{
    public void Add(CreativeContext creativeContext) => context.CreativeContexts.Add(creativeContext);

    public Task<CreativeContext?> FindAsync(Guid contextId, CancellationToken cancellationToken) =>
        Aggregate(context.CreativeContexts.AsNoTracking())

            // No WorkspaceId predicate, and adding one would suggest the global query filter is optional. The
            // key seek happens inside the filtered set, so another workspace's context is not found rather
            // than found and rejected.
            .FirstOrDefaultAsync(candidate => candidate.Id == contextId, cancellationToken);

    public Task<CreativeContext?> FindForUpdateAsync(Guid contextId, CancellationToken cancellationToken) =>
        Aggregate(context.CreativeContexts)
            .FirstOrDefaultAsync(candidate => candidate.Id == contextId, cancellationToken);

    public async Task<(IReadOnlyList<CreativeContextSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        CreativeContextListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // Live only, which with the workspace the filter supplies is the leading edge of
        // IX_CreativeContexts_Workspace_Updated — a seek that arrives already in the order asked for.
        var contexts = context.CreativeContexts.AsNoTracking().Where(candidate => candidate.ArchivedAt == null);

        if (criteria.Position is { } position)
        {
            contexts = contexts.Where(candidate =>
                candidate.UpdatedAt < position.UpdatedAt
                || (candidate.UpdatedAt == position.UpdatedAt && candidate.Id.CompareTo(position.ContextId) < 0));
        }

        var fetched = await contexts
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenByDescending(candidate => candidate.Id)

            // One row past the limit: how the page learns whether another follows without a second query.
            .Take(criteria.Limit + 1)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.WorkingTitle,
                ChannelKeys = candidate.Channels
                    .OrderBy(channel => channel.SortOrder)
                    .Select(channel => channel.ChannelKey)
                    .ToList(),
                candidate.Day,
                candidate.WeeklyThemeKey,
                ReferenceCount = candidate.References.Count,
                candidate.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return fetched
            .Select(row => new CreativeContextSummaryRecord(
                row.Id,
                row.WorkingTitle,
                row.ChannelKeys,
                row.Day,
                row.WeeklyThemeKey,
                row.ReferenceCount,
                row.UpdatedAt))
            .ToList()
            .ToPage(criteria.Limit);
    }

    private static IQueryable<CreativeContext> Aggregate(IQueryable<CreativeContext> contexts) =>
        contexts
            .Include(candidate => candidate.Channels.OrderBy(channel => channel.SortOrder))
            .Include(candidate => candidate.References.OrderBy(reference => reference.SortOrder))

            // Two bounded child collections: one round trip each is cheaper than their cross product.
            .AsSplitQuery();
}
