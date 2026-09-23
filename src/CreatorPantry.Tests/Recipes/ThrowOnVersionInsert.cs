using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Fails a save that is inserting a <see cref="RecipeVersion"/>, standing in for anything that can go wrong
/// in application code while the create unit is being written.
/// </summary>
/// <remarks>
/// The weaker of the two rollback injections, and worth knowing why: throwing from <c>SavingChanges</c>
/// means no SQL was ever sent, so "nothing persisted" is partly true by construction. It is here to cover the
/// application-failure shape; the check-constraint test is what proves SQL Server rolls back inserts it had
/// already accepted.
/// </remarks>
internal sealed class ThrowOnVersionInsert : SaveChangesInterceptor
{
    public static readonly ThrowOnVersionInsert Instance = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var insertingVersion = eventData.Context?.ChangeTracker
            .Entries<RecipeVersion>()
            .Any(entry => entry.State == EntityState.Added) ?? false;

        return insertingVersion
            ? throw new InvalidOperationException("Injected failure while writing the recipe version.")
            : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
