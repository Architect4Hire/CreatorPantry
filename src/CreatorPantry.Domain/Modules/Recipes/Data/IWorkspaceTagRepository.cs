using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for the workspace's own tag vocabulary. Persistence only.
/// </summary>
public interface IWorkspaceTagRepository
{
    /// <summary>
    /// Finds the workspace's existing tags whose normalized names are among those given.
    /// </summary>
    /// <remarks>
    /// Scoped by the global query filter, so this can only ever see the resolved workspace's own vocabulary —
    /// which is what makes two workspaces able to hold a "weeknight" tag each without either seeing the
    /// other's.
    /// </remarks>
    Task<IReadOnlyList<WorkspaceTag>> FindByNormalizedNamesAsync(
        IReadOnlyCollection<string> normalizedNames,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the workspace's tags whose ids are among those given, for reads that display a tag by name.
    /// </summary>
    /// <returns>
    /// Only the ids that exist in the resolved workspace, in no particular order. Fewer rows than ids asked
    /// for is a normal answer, not an error — the caller knows which links it holds and decides what a missing
    /// one means.
    /// </returns>
    Task<IReadOnlyList<WorkspaceTag>> FindByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the workspace's active tags by name, for a picker that offers them as new choices.
    /// </summary>
    /// <remarks>
    /// Retired tags are left out: they stay readable on whatever already carries them, and simply leave the
    /// pickers. Ordered by the normalized name with the id as a tie-break, so the cut at
    /// <paramref name="limit"/> is the same cut on every read.
    /// </remarks>
    Task<IReadOnlyList<WorkspaceTagRecord>> ListActiveAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Stages a new tag for insertion, without saving.</summary>
    void Add(WorkspaceTag tag);
}

internal sealed class WorkspaceTagRepository(CreatorPantryDbContext context) : IWorkspaceTagRepository
{
    public async Task<IReadOnlyList<WorkspaceTag>> FindByNormalizedNamesAsync(
        IReadOnlyCollection<string> normalizedNames,
        CancellationToken cancellationToken)
    {
        if (normalizedNames.Count == 0)
        {
            return [];
        }

        // Tracked rather than AsNoTracking: the caller may attach links to these rows in the same unit of
        // work, and a detached principal would make EF try to insert it again.
        return await context.WorkspaceTags
            .Where(tag => normalizedNames.Contains(tag.NormalizedName))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceTag>> FindByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        // AsNoTracking, unlike the lookup above: this serves a read, and nothing will attach a link to these
        // rows. The workspace filter is what keeps another workspace's identically named tag out of the answer.
        return await context.WorkspaceTags
            .AsNoTracking()
            .Where(tag => ids.Contains(tag.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceTagRecord>> ListActiveAsync(
        int limit, CancellationToken cancellationToken) =>

        // No WorkspaceId predicate and no IgnoreQueryFilters: the global query filter is the scope, which is
        // what keeps another workspace's vocabulary out of the answer. Projected in SQL to the two columns a
        // picker shows, so the normalized name and the workspace id are never read into memory at all.
        await context.WorkspaceTags
            .AsNoTracking()
            .Where(tag => tag.IsActive)
            .OrderBy(tag => tag.NormalizedName)
            .ThenBy(tag => tag.Id)
            .Take(limit)
            .Select(tag => new WorkspaceTagRecord(tag.Id, tag.Name))
            .ToListAsync(cancellationToken);

    public void Add(WorkspaceTag tag) => context.WorkspaceTags.Add(tag);
}
