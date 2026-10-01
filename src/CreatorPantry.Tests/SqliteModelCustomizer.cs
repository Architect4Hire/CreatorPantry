using System.Globalization;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CreatorPantry.Tests;

/// <summary>
/// The three adjustments the real model needs before SQLite will carry it. All are test-harness workarounds,
/// deliberately kept in the harness: the production mappings stay native.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every SQLite-backed harness installs this, with no exceptions left.</strong> Adjustment 3 below is
/// not optional the way the first two were: a <c>vector</c> column fails model <em>validation</em> under
/// SQLite, so a harness without this line cannot build the context at all and every test in it fails on
/// something it never touched. Eight harnesses were in that position when the brand-source chunk arrived —
/// the auth, idempotency, outbox and reference ones, which had never needed a row version or an ordered
/// timestamp. Write
/// <c>.UseSqlite(connection).ReplaceService&lt;IModelCustomizer, SqliteModelCustomizer&gt;()</c> in any new one.
/// </para>
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
/// <para>
/// <strong>3. <c>SqlVector&lt;float&gt;</c> has no SQLite mapping at all.</strong> A brand-source chunk's
/// embedding is a native SQL Server <c>vector(1536)</c>, a type the SQLite provider cannot map in either
/// direction — so without this pass the model fails to <em>build</em>, and every test in every SQLite fixture
/// dies at construction with an error about one property on one entity nobody here was touching. Writing it as
/// a comma-separated list of round-trippable floats gives SQLite something to store and read back exactly, and
/// the column type is cleared along with it so the harness does not create a column whose declared type SQLite
/// would give numeric affinity.
/// </para>
/// <para>
/// <strong>What that conversion costs, which is the whole point of knowing it is here.</strong> Text in a
/// SQLite column is not a vector: there is no <c>VECTOR_DISTANCE</c>, so nothing about similarity, ranking or
/// nearest-neighbour behaviour can be asserted through this harness. What these tests can prove is the shape —
/// that the property is required, that the slice and set columns constrain each other, that the query filter
/// and the cascade are what the configuration says. Everything about the vector itself belongs to tests
/// against a real SQL Server, where the column is the engine's own type.
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

    private static readonly ValueConverter<SqlVector<float>, string> ToFloatList =
        new(vector => WriteFloats(vector), text => ReadFloats(text));

    /// <summary>
    /// Compares and snapshots by value. The struct's own equality is over a <see cref="ReadOnlyMemory{T}"/>
    /// field, so two separately allocated arrays holding identical floats would not compare equal and a loaded
    /// chunk would look modified the moment it was read.
    /// </summary>
    private static readonly ValueComparer<SqlVector<float>> ByValue = new(
        (left, right) => SameFloats(left, right),
        vector => WriteFloats(vector).GetHashCode(StringComparison.Ordinal),
        vector => new SqlVector<float>(vector.Memory.ToArray()));

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        // Applied by walking the model rather than by naming columns, so an entity added later is covered
        // without anyone remembering to come back here.
        //
        // The row-version pass used to name Recipe.RowVersion directly, and AiOperation arrived with a token of
        // its own — under SQLite every insert failed on a NOT NULL column EF never sends, which reads as
        // "the operation insert was rejected" a long way from its cause. Discovering them from the model is
        // what stops the next concurrency token repeating that.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ValueGenerated == ValueGenerated.OnAddOrUpdate && property.IsConcurrencyToken)
            {
                property.SetDefaultValueSql("randomblob(8)");
            }
            else if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(ToUtcTicks);
            }
            else if (property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(ToNullableUtcTicks);
            }
            else if (property.ClrType == typeof(SqlVector<float>))
            {
                property.SetValueConverter(ToFloatList);
                property.SetValueComparer(ByValue);

                // The model declares vector(1536). SQLite would accept the name and apply numeric affinity to
                // the text written above it; clearing it leaves the provider to choose TEXT.
                property.SetColumnType(null);
            }
        }
    }

    private static bool SameFloats(SqlVector<float> left, SqlVector<float> right) =>
        left.Memory.Span.SequenceEqual(right.Memory.Span);

    private static string WriteFloats(SqlVector<float> vector) =>
        string.Join(',', vector.Memory.ToArray().Select(value => value.ToString("R", CultureInfo.InvariantCulture)));

    private static SqlVector<float> ReadFloats(string text) =>
        new(text.Split(',').Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray());
}
