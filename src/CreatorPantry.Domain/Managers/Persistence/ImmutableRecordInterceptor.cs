using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Blocks every update and delete of an <see cref="IImmutableRecord"/> row, whatever code path reaches
/// <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// Discovery is by interface, not by a list of types — the same reason
/// <see cref="WorkspaceOwnershipConvention"/> scans the model instead of naming entities. A rule that has to
/// be extended by hand for each new entity is a rule that is eventually not extended.
/// </remarks>
internal sealed class ImmutableRecordInterceptor : SaveChangesInterceptor
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

        foreach (var entry in context.ChangeTracker.Entries<IImmutableRecord>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"A {entry.Entity.GetType().Name} row cannot be "
                        + $"{(entry.State == EntityState.Deleted ? "deleted" : "modified")}; it is immutable once written.");
            }
        }
    }
}
