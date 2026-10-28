using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Blocks every delete of an <see cref="IRetainedRecord"/> row, and every update the row's own
/// <see cref="IRetainedRecord.RefuseChange"/> refuses, whatever code path reaches <c>SaveChanges</c>. A retained
/// row is kept, not frozen: the updates it allows pass.
/// </summary>
/// <remarks>
/// Discovery is by interface, not by a list of types, for the reason <see cref="ImmutableRecordInterceptor"/>
/// gives: a rule that has to be extended by hand for each new entity is a rule that is eventually not extended.
/// </remarks>
internal sealed class RetainedRecordInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        RejectDeletion(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        RejectDeletion(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void RejectDeletion(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<IRetainedRecord>())
        {
            if (entry.State is EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"A {entry.Entity.GetType().Name} row cannot be deleted; it is kept once written.");
            }

            if (entry.State is EntityState.Modified
                && entry.Entity.RefuseChange(name => entry.OriginalValues[name]) is { } refused)
            {
                throw new InvalidOperationException(
                    $"That change to a {entry.Entity.GetType().Name} row is refused: {refused}");
            }
        }
    }
}
