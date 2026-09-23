using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Stamps <see cref="IWorkspaceOwned.WorkspaceId"/> on added workspace-owned entities from the resolved
/// <see cref="IWorkspaceContext"/> and rejects writes that would let ownership be set or moved from
/// feature code: an insert that already carries a different workspace's id, or an update that changes
/// WorkspaceId at all. Feature Business code must never assign WorkspaceId itself (tenancy.md); this is
/// the one place it is set or protected, mirroring how <see cref="WorkspaceOwnershipConvention"/> is the
/// one place reads are scoped.
/// </summary>
internal sealed class WorkspaceOwnershipInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Apply(DbContext? context)
    {
        if (context is not IWorkspaceIdSource source)
        {
            return;
        }

        var currentWorkspaceId = source.CurrentWorkspaceIdOrNull;

        foreach (var entry in context.ChangeTracker.Entries<IWorkspaceOwned>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampOrRejectInsert(entry, currentWorkspaceId);
                    break;
                case EntityState.Modified:
                    RejectOwnershipChange(entry);
                    break;
            }
        }
    }

    private static void StampOrRejectInsert(EntityEntry<IWorkspaceOwned> entry, Guid? currentWorkspaceId)
    {
        if (currentWorkspaceId is not { } workspaceId)
        {
            // No workspace resolved in this scope — e.g. creating the first Workspace and its owner
            // WorkspaceMembership, the one path that necessarily happens before resolution. There is no
            // ambient workspace to stamp with or compare against, so the caller's own id stands.
            return;
        }

        if (entry.Entity.WorkspaceId == Guid.Empty)
        {
            entry.Entity.WorkspaceId = workspaceId;
            return;
        }

        if (entry.Entity.WorkspaceId != workspaceId)
        {
            throw new InvalidOperationException(
                $"Cannot insert a {entry.Entity.GetType().Name} owned by workspace {entry.Entity.WorkspaceId} " +
                $"while the resolved workspace for this scope is {workspaceId}.");
        }
    }

    private static void RejectOwnershipChange(EntityEntry<IWorkspaceOwned> entry)
    {
        var workspaceIdProperty = entry.Property(nameof(IWorkspaceOwned.WorkspaceId));
        if (!Equals(workspaceIdProperty.OriginalValue, workspaceIdProperty.CurrentValue))
        {
            throw new InvalidOperationException(
                $"Cannot change the WorkspaceId of an existing {entry.Entity.GetType().Name}; ownership is immutable.");
        }
    }
}
