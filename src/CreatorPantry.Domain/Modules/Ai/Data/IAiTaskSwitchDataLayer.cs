using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

internal interface IAiTaskSwitchDataLayer
{
    /// <summary>The stored answers by task. A task that is absent has none.</summary>
    Task<IReadOnlyDictionary<string, bool>> ListAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IAiTaskSwitchRepository.FindEnabledAsync"/>
    Task<bool?> FindEnabledAsync(string task, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an answer for every task in <paramref name="states"/>, and the audit record, in one save.
    /// </summary>
    /// <returns>False when another writer got there first and nothing was committed.</returns>
    Task<bool> ReplaceAsync(
        IReadOnlyDictionary<string, bool> states,
        DateTimeOffset at,
        AuditEntry audit,
        CancellationToken cancellationToken);
}

internal sealed class AiTaskSwitchDataLayer(
    IAiTaskSwitchRepository switches,
    IAuditWriter auditWriter,
    CreatorPantryDbContext context) : IAiTaskSwitchDataLayer
{
    public async Task<IReadOnlyDictionary<string, bool>> ListAsync(CancellationToken cancellationToken)
    {
        var stored = await switches.ListAsync(cancellationToken);

        return stored.ToDictionary(
            taskSwitch => taskSwitch.Task, taskSwitch => taskSwitch.IsEnabled, StringComparer.OrdinalIgnoreCase);
    }

    public Task<bool?> FindEnabledAsync(string task, CancellationToken cancellationToken) =>
        switches.FindEnabledAsync(task, cancellationToken);

    public async Task<bool> ReplaceAsync(
        IReadOnlyDictionary<string, bool> states,
        DateTimeOffset at,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        var stored = await switches.ListForUpdateAsync(cancellationToken);
        var byTask = stored.ToDictionary(taskSwitch => taskSwitch.Task, StringComparer.OrdinalIgnoreCase);

        foreach (var (task, enabled) in states)
        {
            if (byTask.TryGetValue(task, out var existing))
            {
                if (existing.IsEnabled != enabled)
                {
                    existing.IsEnabled = enabled;
                    existing.UpdatedAt = at;
                }

                continue;
            }

            // WorkspaceId is left unset: stamping it is WorkspaceOwnershipInterceptor's job, from the resolved
            // context, and a value set by hand here would be the only place a client-shaped id could enter.
            switches.Add(new WorkspaceAiTaskSwitch
            {
                Id = Guid.NewGuid(),
                Task = task,
                IsEnabled = enabled,
                CreatedAt = at,
                UpdatedAt = at,
            });
        }

        auditWriter.Record(audit);

        try
        {
            // One save, one transaction: the switches and the record of who changed them commit together.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing committed, and nothing may stay staged for a later save on this scope to pick up.
            context.ChangeTracker.Clear();

            // The unique index is the guard against two Owners saving at once, but a DbUpdateException is not
            // only ever that. Ask whether a row this attempt was inserting is now there; anything else stays
            // an error.
            var now = await switches.ListAsync(cancellationToken);
            var lost = states.Keys.Any(task =>
                !byTask.ContainsKey(task)
                && now.Any(taskSwitch => string.Equals(taskSwitch.Task, task, StringComparison.OrdinalIgnoreCase)));

            if (!lost)
            {
                throw;
            }

            return false;
        }

        return true;
    }
}
