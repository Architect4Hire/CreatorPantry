using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Exercises the EF configuration for <see cref="MeasurementUnit"/> and <see cref="UnitAlias"/> against an
/// in-memory SQLite database, the same pattern <c>WorkspaceConstraintTests</c> uses. This verifies the
/// uniqueness and dimension rules the <c>AddMeasurementUnits</c> migration also creates, without applying
/// that migration anywhere.
/// </summary>
public sealed class MeasurementUnitConstraintTests : IDisposable
{
    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    // --- Global reference data, not workspace-owned ------------------------------------------------------

    [Fact]
    public void Measurement_entities_carry_no_workspace_id()
    {
        using var scope = _services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        foreach (var type in new[] { typeof(MeasurementUnit), typeof(UnitAlias) })
        {
            Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(type));
            Assert.Null(model.FindEntityType(type)!.FindProperty(nameof(IWorkspaceOwned.WorkspaceId)));
        }
    }

    /// <summary>
    /// The point of the data-zone split: a workspace-owned set throws before a workspace is resolved, while
    /// shared reference data is readable with no workspace at all. Nothing here resolves one.
    /// </summary>
    [Fact]
    public async Task Units_are_readable_without_a_resolved_workspace_where_workspace_owned_data_is_not()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.MeasurementUnits.Add(Gram());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var units = await db.MeasurementUnits.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("g", Assert.Single(units).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- Uniqueness -------------------------------------------------------------------------------------

    /// <remarks>
    /// Exact duplicates only. Whether <c>G</c> collides with <c>g</c> depends on collation — SQL Server's
    /// default is case-insensitive, SQLite's index is not — so codes are held lowercase by
    /// <see cref="CodeFormat.CodePattern"/> rather than by asserting provider-specific behavior here.
    /// </remarks>
    [Fact]
    public async Task Two_units_cannot_share_a_code()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MeasurementUnits.Add(Gram());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.MeasurementUnits.Add(NewUnit("g", "gramme", MeasurementDimension.Mass, MeasurementSystem.Metric, factor: 1m));

        await AssertRejectedByAsync(db, "MeasurementUnits.Code");
    }

    [Fact]
    public async Task One_alias_cannot_resolve_to_two_units()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var gram = Gram();
        var ounce = NewUnit("oz", "ounce", MeasurementDimension.Mass, MeasurementSystem.UsCustomary, factor: 28.349523125m);
        db.MeasurementUnits.AddRange(gram, ounce);
        db.UnitAliases.Add(NewAlias(gram.Id, "grams"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // "Grams." normalizes to the same lookup key already claimed by the gram — ambiguity the database refuses.
        db.UnitAliases.Add(NewAlias(ounce.Id, "Grams."));

        await AssertRejectedByAsync(db, "UnitAliases.NormalizedAlias");
    }

    [Fact]
    public async Task One_unit_can_have_many_aliases()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var teaspoon = NewUnit("tsp", "teaspoon", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, factor: 4.92892159375m);
        db.MeasurementUnits.Add(teaspoon);
        db.UnitAliases.AddRange(NewAlias(teaspoon.Id, "tsp."), NewAlias(teaspoon.Id, "teaspoons"), NewAlias(teaspoon.Id, "teaspoonful"));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var aliases = await db.UnitAliases
            .Where(alias => alias.MeasurementUnitId == teaspoon.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, aliases.Count);
    }

    [Fact]
    public async Task Deleting_a_unit_removes_only_its_own_aliases()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var gram = Gram();
        var each = NewUnit("each", "item", MeasurementDimension.Count, MeasurementSystem.Neutral, factor: 1m);
        db.MeasurementUnits.AddRange(gram, each);
        db.UnitAliases.AddRange(NewAlias(gram.Id, "grams"), NewAlias(each.Id, "items"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.MeasurementUnits.Remove(gram);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var surviving = await db.UnitAliases.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(each.Id, Assert.Single(surviving).MeasurementUnitId);
    }

    // --- Dimension rules --------------------------------------------------------------------------------

    /// <summary>Mass, volume, and count convert by a multiplier, so one is mandatory.</summary>
    [Theory]
    [InlineData(MeasurementDimension.Mass)]
    [InlineData(MeasurementDimension.Volume)]
    [InlineData(MeasurementDimension.Count)]
    public async Task A_convertible_dimension_requires_a_base_unit_factor(MeasurementDimension dimension)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MeasurementUnits.Add(NewUnit($"u-{(int)dimension}", "unit", dimension, MeasurementSystem.Metric, factor: null));

        await AssertRejectedByAsync(db, "CK_MeasurementUnits_Factor_Dimension");
    }

    /// <summary>
    /// The rule that keeps a factor from meaning something it cannot: temperature is affine rather than
    /// multiplicative, and a qualitative unit has no numeric relationship to scale at all.
    /// </summary>
    [Theory]
    [InlineData(MeasurementDimension.Temperature)]
    [InlineData(MeasurementDimension.Qualitative)]
    public async Task A_non_convertible_dimension_cannot_carry_a_base_unit_factor(MeasurementDimension dimension)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MeasurementUnits.Add(NewUnit($"u-{(int)dimension}", "unit", dimension, MeasurementSystem.Neutral, factor: 1m));

        await AssertRejectedByAsync(db, "CK_MeasurementUnits_Factor_Dimension");
    }

    [Theory]
    [InlineData(MeasurementDimension.Temperature, MeasurementSystem.Metric, "c", "degree Celsius")]
    [InlineData(MeasurementDimension.Qualitative, MeasurementSystem.Neutral, "pinch", "pinch")]
    public async Task A_non_convertible_dimension_is_accepted_without_a_factor(
        MeasurementDimension dimension, MeasurementSystem system, string code, string name)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MeasurementUnits.Add(NewUnit(code, name, dimension, system, factor: null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var saved = await db.MeasurementUnits.SingleAsync(unit => unit.Code == code, TestContext.Current.CancellationToken);
        Assert.Null(saved.BaseUnitFactor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_base_unit_factor_must_be_positive(decimal factor)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MeasurementUnits.Add(NewUnit("bad", "bad unit", MeasurementDimension.Mass, MeasurementSystem.Metric, factor));

        await AssertRejectedByAsync(db, "CK_MeasurementUnits_Factor_Positive");
    }

    /// <summary>A factor's scale must survive the round trip, or every scaled recipe inherits the rounding error.</summary>
    [Fact]
    public async Task A_base_unit_factor_keeps_its_declared_scale()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        const decimal usTeaspoonInMillilitres = 4.92892159375m;

        db.MeasurementUnits.Add(NewUnit("tsp", "teaspoon", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, usTeaspoonInMillilitres));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var saved = await db.MeasurementUnits.SingleAsync(unit => unit.Code == "tsp", TestContext.Current.CancellationToken);
        Assert.Equal(usTeaspoonInMillilitres, saved.BaseUnitFactor);
    }

    [Theory]
    [InlineData(QuantityFormat.MinDisplayPrecision - 1)]
    [InlineData(QuantityFormat.MaxDisplayPrecision + 1)]
    public async Task Display_precision_outside_the_allowed_range_is_rejected(int displayPrecision)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var unit = Gram();
        unit.DisplayPrecision = displayPrecision;

        db.MeasurementUnits.Add(unit);

        await AssertRejectedByAsync(db, "CK_MeasurementUnits_DisplayPrecision");
    }

    // --- Policy -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MeasurementDimension.Mass, "g")]
    [InlineData(MeasurementDimension.Volume, "ml")]
    [InlineData(MeasurementDimension.Count, "each")]
    [InlineData(MeasurementDimension.Temperature, null)]
    [InlineData(MeasurementDimension.Qualitative, null)]
    public void A_base_unit_exists_exactly_where_a_factor_is_required(MeasurementDimension dimension, string? expectedBaseCode)
    {
        Assert.Equal(expectedBaseCode, MeasurementPolicy.BaseUnitCode(dimension));
        Assert.Equal(expectedBaseCode is not null, MeasurementPolicy.RequiresBaseUnitFactor(dimension));
    }

    /// <summary>Every dimension is classified; a new one cannot be added without deciding this.</summary>
    [Fact]
    public void Every_dimension_is_classified()
    {
        foreach (var dimension in Enum.GetValues<MeasurementDimension>())
        {
            Assert.Equal(MeasurementPolicy.BaseUnitCode(dimension) is not null, MeasurementPolicy.RequiresBaseUnitFactor(dimension));
        }
    }

    [Theory]
    [InlineData("tsp.", "tsp")]
    [InlineData("TSP", "tsp")]
    [InlineData("  Teaspoons  ", "teaspoons")]
    [InlineData("fl. oz.", "floz")]
    public void Normalizing_an_alias_removes_case_and_punctuation(string alias, string expected) =>
        Assert.Equal(expected, MeasurementPolicy.NormalizeAlias(alias));

    // --- Helpers ----------------------------------------------------------------------------------------

    /// <summary>
    /// Pins each rejection to the rule that caused it. This matters most for the factor and precision checks:
    /// SQLite has no decimal type, so naming the constraint is what proves they evaluate numerically rather
    /// than passing vacuously over text.
    /// </summary>
    private static Task AssertRejectedByAsync(DbContext db, string rule) =>
        ReferenceConstraintAssertions.AssertRejectedByAsync(db, rule);

    private static MeasurementUnit Gram() =>
        NewUnit("g", "gram", MeasurementDimension.Mass, MeasurementSystem.Metric, factor: 1m);

    private static MeasurementUnit NewUnit(
        string code, string name, MeasurementDimension dimension, MeasurementSystem system, decimal? factor) => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            DisplayName = name,
            PluralName = $"{name}s",
            Abbreviation = code,
            Dimension = dimension,
            System = system,
            BaseUnitFactor = factor,
            DisplayPrecision = 2,
            IsActive = true,
        };

    private static UnitAlias NewAlias(Guid unitId, string alias) => new()
    {
        Id = Guid.NewGuid(),
        MeasurementUnitId = unitId,
        Alias = alias,
        NormalizedAlias = MeasurementPolicy.NormalizeAlias(alias),
    };
}
