using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CreatorPantry.Tests;

/// <summary>
/// Gives <see cref="Recipe.RowVersion"/> a value under SQLite, which these tests need and SQLite cannot
/// supply on its own.
/// </summary>
/// <remarks>
/// <para>
/// <c>IsRowVersion()</c> maps to SQL Server's native <c>rowversion</c>: the server generates and bumps it,
/// and EF never sends the column. SQLite has no equivalent, so the column is created <c>NOT NULL</c>, left
/// out of the insert, and every write fails. The default expression here fills it on insert.
/// </para>
/// <para>
/// Applied by every SQLite-backed test host that carries the real model. It is a test-harness workaround,
/// deliberately kept in the harness. The production mapping stays
/// native because a server-generated token cannot be forgotten by a writer or lost to a read-modify-write
/// race, and weakening it to something application-managed so that an in-memory test database is happy
/// would trade a real guarantee for a convenient one. The consequence to know: under SQLite the value does
/// not change on update, so these tests prove the schema, never the concurrency behaviour. That belongs to
/// the update seam, tested against a real database.
/// </para>
/// </remarks>
internal sealed class SqliteRowVersionModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.Entity<Recipe>()
            .Property(recipe => recipe.RowVersion)
            .HasDefaultValueSql("randomblob(8)");
    }
}
