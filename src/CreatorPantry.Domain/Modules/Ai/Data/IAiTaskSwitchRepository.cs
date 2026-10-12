using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>
/// EF access to a workspace's AI task switches. Every query here runs under the workspace query filter, so
/// none names a workspace and none can read another's.
/// </summary>
internal interface IAiTaskSwitchRepository
{
    Task<List<WorkspaceAiTaskSwitch>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Tracked, for the replace that is about to change them.</summary>
    Task<List<WorkspaceAiTaskSwitch>> ListForUpdateAsync(CancellationToken cancellationToken);

    /// <summary>This workspace's stored answer for one task, or null when nobody has chosen.</summary>
    Task<bool?> FindEnabledAsync(string task, CancellationToken cancellationToken);

    void Add(WorkspaceAiTaskSwitch taskSwitch);
}

internal sealed class AiTaskSwitchRepository(CreatorPantryDbContext context) : IAiTaskSwitchRepository
{
    public Task<List<WorkspaceAiTaskSwitch>> ListAsync(CancellationToken cancellationToken) =>
        context.WorkspaceAiTaskSwitches.AsNoTracking().ToListAsync(cancellationToken);

    public Task<List<WorkspaceAiTaskSwitch>> ListForUpdateAsync(CancellationToken cancellationToken) =>
        context.WorkspaceAiTaskSwitches.ToListAsync(cancellationToken);

    public Task<bool?> FindEnabledAsync(string task, CancellationToken cancellationToken) =>
        context.WorkspaceAiTaskSwitches
            .AsNoTracking()
            .Where(taskSwitch => taskSwitch.Task == task)
            .Select(taskSwitch => (bool?)taskSwitch.IsEnabled)
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(WorkspaceAiTaskSwitch taskSwitch) => context.WorkspaceAiTaskSwitches.Add(taskSwitch);
}
