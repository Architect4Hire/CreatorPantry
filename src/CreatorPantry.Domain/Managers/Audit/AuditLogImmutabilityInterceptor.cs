using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>
/// Blocks any ordinary attempt to update or delete an <see cref="CreatorPantry.Domain.Managers.Audit.AuditLog"/> row. There is no documented
/// erasure/retention path for audit rows yet (tenancy.md's <c>IgnoreQueryFilters</c> carve-out is the model
/// such a path would eventually follow); until one exists, every write path goes through this.
/// </summary>
internal sealed class AuditLogImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        RejectMutation(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        RejectMutation(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void RejectMutation(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<AuditLog>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"An AuditLog row cannot be {(entry.State == EntityState.Deleted ? "deleted" : "modified")}; it is immutable once written.");
            }
        }
    }
}
