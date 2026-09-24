using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CreatorPantry.Tests;

/// <summary>
/// The two adjustments the real model needs before SQLite will carry it. Both are test-harness workarounds,
/// deliberately kept in the harness: the production mappings stay native.
/// </summary>
/// <remarks>
/// <para>
/// <strong>1. <see cref="Recipe.RowVersion"/> needs a value.</strong> <c>IsRowVersion()</c> maps to SQL
/// Server's native <c>rowversion</c>: the server generates and bumps it, and EF never sends the column. SQLite
/// has no equivalent, so the column is created <c>NOT NULL</c>, left out of the insert, and every write fails.
/// The default expression here fills it on insert. The production mapping stays native because a
/// server-generated token cannot be forgotten by a writer or lost to a read-modify-write race, and weakening it
/// to something application-managed so that an in-memory test database is happy would trade a real guarantee
/// for a convenient one. The consequence to know: under SQLite the value does not change on update, so these
/// tests prove the schema, never the concurrency behaviour. That belongs to tests against a real database.
/// </para>
/// <para>
/// <strong>2. <c>DateTimeOffset</c> cannot be ordered by.</strong> SQLite stores it as text and EF Core refuses
/// to translate <c>ORDER BY</c> over it outright — "SQLite does not support expressions of type
/// 'DateTimeOffset' in ORDER BY clauses". The recipe library's default ordering is by <c>UpdatedAt</c>, so
/// without this every search through a SQLite-backed host throws before it reaches a row. Converting to UTC
/// ticks gives SQLite an integer it can both compare and order, and integers order identically to the instants
/// they encode — which is the property the keyset paging depends on.
/// </para>
/// <para>
/// <strong>What the conversion costs.</strong> The stored value is an instant, so the original UTC offset is
/// not recoverable: a value written as <c>+02:00</c> reads back as the same instant at <c>+00:00</c>. Harmless
/// here, and worth being explicit about — every timestamp this system writes comes from
/// <c>IClock.UtcNow</c> and is already at offset zero, so the round trip is lossless for real data. A test that
/// deliberately wrote a non-UTC offset and then asserted on that offset would be the one case this breaks, and
/// there is none. On SQL Server nothing changes: <c>datetimeoffset</c> stores and orders the offset natively.
/// </para>
/// </remarks>
internal sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    private static readonly ValueConverter<DateTimeOffset, long> ToUtcTicks =
        new(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> ToNullableUtcTicks =
        new(value => value == null ? null : value.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero));

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.Entity<Recipe>()
            .Property(recipe => recipe.RowVersion)
            .HasDefaultValueSql("randomblob(8)");

        // Applied by walking the model rather than by naming columns, so an entity added later is covered
        // without anyone remembering to come back here.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(ToUtcTicks);
            }
            else if (property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(ToNullableUtcTicks);
            }
        }
    }
}
