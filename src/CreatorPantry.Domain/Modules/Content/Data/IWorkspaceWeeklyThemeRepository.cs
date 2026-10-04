using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

public interface IWorkspaceWeeklyThemeRepository
{
    /// <summary>
    /// Every theme the resolved workspace holds, live and retired, no-tracking and in no particular order. Takes
    /// no workspace id: the query filter scopes it, so another workspace's themes are indistinguishable from
    /// none at all. Unpaged because the set is capped at <c>WeeklyThemePolicy.MaxThemes</c> rows.
    /// </summary>
    Task<List<WorkspaceWeeklyTheme>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The same read, tracked, so the layer above can change the week and save it.</summary>
    Task<List<WorkspaceWeeklyTheme>> ListForUpdateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The theme with this exact key, no-tracking, or null when this workspace has never had one. Retired themes
    /// are included: a key resolves whether or not the theme is still in the week.
    /// </summary>
    Task<WorkspaceWeeklyTheme?> FindAsync(string key, CancellationToken cancellationToken);

    /// <inheritdoc cref="FindAsync"/>
    /// <remarks>Tracked, for the one caller that goes on to delete what it found.</remarks>
    Task<WorkspaceWeeklyTheme?> FindForUpdateAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stages a new theme. Nothing is saved.</summary>
    void Add(WorkspaceWeeklyTheme theme);

    /// <summary>Stages a deletion. Nothing is saved.</summary>
    void Remove(WorkspaceWeeklyTheme theme);
}

internal sealed class WorkspaceWeeklyThemeRepository(CreatorPantryDbContext context) : IWorkspaceWeeklyThemeRepository
{
    public Task<List<WorkspaceWeeklyTheme>> ListAsync(CancellationToken cancellationToken) =>
        context.WorkspaceWeeklyThemes.AsNoTracking().ToListAsync(cancellationToken);

    public Task<List<WorkspaceWeeklyTheme>> ListForUpdateAsync(CancellationToken cancellationToken) =>
        context.WorkspaceWeeklyThemes.ToListAsync(cancellationToken);

    public Task<WorkspaceWeeklyTheme?> FindAsync(string key, CancellationToken cancellationToken) =>
        context.WorkspaceWeeklyThemes
            .AsNoTracking()
            .SingleOrDefaultAsync(theme => theme.Key == key, cancellationToken);

    public Task<WorkspaceWeeklyTheme?> FindForUpdateAsync(string key, CancellationToken cancellationToken) =>
        context.WorkspaceWeeklyThemes.SingleOrDefaultAsync(theme => theme.Key == key, cancellationToken);

    public void Add(WorkspaceWeeklyTheme theme) => context.WorkspaceWeeklyThemes.Add(theme);

    public void Remove(WorkspaceWeeklyTheme theme) => context.WorkspaceWeeklyThemes.Remove(theme);
}
