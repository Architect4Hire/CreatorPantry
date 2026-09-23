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
using static CreatorPantry.Tests.Reference.ReferenceConstraintAssertions;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Exercises the EF configuration for <see cref="Ingredient"/>, <see cref="IngredientAlias"/>, and
/// <see cref="FoodCategory"/> against an in-memory SQLite database, the same pattern
/// <c>MeasurementUnitConstraintTests</c> uses. This verifies the natural keys, normalization rules, and
/// dimension rule the <c>AddIngredientReference</c> migration also creates, without applying it anywhere.
/// </summary>
public sealed class IngredientConstraintTests : IDisposable
{
    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    // --- Global reference data, not workspace-owned ------------------------------------------------------

    [Fact]
    public void Ingredient_entities_carry_no_workspace_id()
    {
        using var scope = _services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        foreach (var type in new[] { typeof(Ingredient), typeof(IngredientAlias), typeof(FoodCategory) })
        {
            Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(type));
            Assert.Null(model.FindEntityType(type)!.FindProperty(nameof(IWorkspaceOwned.WorkspaceId)));
        }
    }

    [Fact]
    public async Task Ingredients_are_readable_without_a_resolved_workspace()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Ingredients.Add(NewIngredient("All-Purpose Flour"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ingredients = await db.Ingredients.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("all purpose flour", Assert.Single(ingredients).NormalizedName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- Case and alias uniqueness ----------------------------------------------------------------------

    /// <summary>
    /// The point of a normalized natural key: casing variants are one ingredient, not several. Normalization
    /// happens before the database sees the value, so this holds whatever the column's collation is.
    /// </summary>
    [Theory]
    [InlineData("Flour", "flour")]
    [InlineData("Flour", "FLOUR")]
    [InlineData("Flour", "  flour  ")]
    [InlineData("All-Purpose Flour", "all purpose flour")]
    [InlineData("Jalapeño", "jalapeno")]
    [InlineData("Crème Fraîche", "creme fraiche")]
    [InlineData("Half-and-Half", "half and half")]
    public async Task Two_ingredients_cannot_share_a_normalized_name(string first, string second)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Ingredients.Add(NewIngredient(first));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Ingredients.Add(NewIngredient(second));

        await AssertRejectedByAsync(db, "Ingredients.NormalizedName");
    }

    [Fact]
    public async Task Distinct_ingredients_coexist()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Ingredients.AddRange(NewIngredient("All-Purpose Flour"), NewIngredient("Bread Flour"), NewIngredient("Rye Flour"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await db.Ingredients.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_alias_cannot_resolve_to_two_ingredients()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var greenOnion = NewIngredient("Green Onion");
        var leek = NewIngredient("Leek");
        db.Ingredients.AddRange(greenOnion, leek);
        db.IngredientAliases.Add(NewAlias(greenOnion.Id, "Scallion"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // "scallions." normalizes differently, but "SCALLION" does not — the key is already claimed.
        db.IngredientAliases.Add(NewAlias(leek.Id, "SCALLION"));

        await AssertRejectedByAsync(db, "IngredientAliases.NormalizedAlias");
    }

    [Fact]
    public async Task One_ingredient_can_have_many_aliases()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var cornstarch = NewIngredient("Cornstarch");
        db.Ingredients.Add(cornstarch);
        db.IngredientAliases.AddRange(
            NewAlias(cornstarch.Id, "corn starch"),
            NewAlias(cornstarch.Id, "cornflour"),
            NewAlias(cornstarch.Id, "corn flour"));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var aliases = await db.IngredientAliases
            .Where(alias => alias.IngredientId == cornstarch.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, aliases.Count);
    }

    [Fact]
    public async Task Deleting_an_ingredient_removes_only_its_own_aliases()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var cornstarch = NewIngredient("Cornstarch");
        var eggplant = NewIngredient("Eggplant");
        db.Ingredients.AddRange(cornstarch, eggplant);
        db.IngredientAliases.AddRange(NewAlias(cornstarch.Id, "corn starch"), NewAlias(eggplant.Id, "aubergine"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Ingredients.Remove(cornstarch);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var surviving = await db.IngredientAliases.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(eggplant.Id, Assert.Single(surviving).IngredientId);
    }

    // --- Category ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_categories_cannot_share_a_code()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.FoodCategories.Add(NewCategory("dairy"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.FoodCategories.Add(NewCategory("dairy"));

        await AssertRejectedByAsync(db, "FoodCategories.Code");
    }

    /// <summary>Retiring a category must never take its ingredients with it.</summary>
    [Fact]
    public async Task Deleting_a_category_leaves_its_ingredients_uncategorized()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var baking = NewCategory("baking");
        var flour = NewIngredient("All-Purpose Flour");
        flour.FoodCategoryId = baking.Id;
        db.FoodCategories.Add(baking);
        db.Ingredients.Add(flour);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.FoodCategories.Remove(baking);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var surviving = Assert.Single(await db.Ingredients.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Null(surviving.FoodCategoryId);
    }

    // --- The default count unit must be a Count unit -----------------------------------------------------

    [Fact]
    public async Task A_default_count_unit_may_reference_a_count_unit()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var clove = NewUnit("clove", MeasurementDimension.Count);
        var garlic = NewIngredient("Garlic");
        garlic.DefaultCountUnitId = clove.Id;
        garlic.DefaultCountUnitDimension = MeasurementDimension.Count;
        db.MeasurementUnits.Add(clove);
        db.Ingredients.Add(garlic);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var saved = await db.Ingredients.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(clove.Id, saved.DefaultCountUnitId);
    }

    /// <summary>
    /// The composite foreign key doing its job: the dimension column claims Count, so the lookup against
    /// <c>MeasurementUnits (Id, Dimension)</c> finds no row for a gram and the write is refused. This is the
    /// case a plain single-column foreign key would have accepted.
    /// </summary>
    [Theory]
    [InlineData(MeasurementDimension.Mass)]
    [InlineData(MeasurementDimension.Volume)]
    [InlineData(MeasurementDimension.Temperature)]
    public async Task A_default_count_unit_cannot_reference_a_unit_of_another_dimension(MeasurementDimension dimension)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var otherUnit = NewUnit($"u-{(int)dimension}", dimension);
        db.MeasurementUnits.Add(otherUnit);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var garlic = NewIngredient("Garlic");
        garlic.DefaultCountUnitId = otherUnit.Id;
        garlic.DefaultCountUnitDimension = MeasurementDimension.Count;
        db.Ingredients.Add(garlic);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>
    /// The other half of the rule: declaring the dimension as anything but Count is refused outright, so the
    /// composite key cannot be satisfied by pointing honestly at a mass unit.
    /// </summary>
    [Fact]
    public async Task A_default_count_unit_cannot_declare_a_non_count_dimension()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var gram = NewUnit("g", MeasurementDimension.Mass);
        db.MeasurementUnits.Add(gram);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var garlic = NewIngredient("Garlic");
        garlic.DefaultCountUnitId = gram.Id;
        garlic.DefaultCountUnitDimension = MeasurementDimension.Mass;
        db.Ingredients.Add(garlic);

        await AssertRejectedByAsync(db, "CK_Ingredients_DefaultCountUnit_Dimension");
    }

    [Fact]
    public async Task A_default_count_unit_cannot_be_half_set()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var garlic = NewIngredient("Garlic");
        garlic.DefaultCountUnitDimension = MeasurementDimension.Count;

        db.Ingredients.Add(garlic);

        await AssertRejectedByAsync(db, "CK_Ingredients_DefaultCountUnit_Dimension");
    }

    /// <summary>Most ingredients have no default count noun at all, which must stay the easy case.</summary>
    [Fact]
    public async Task An_ingredient_needs_no_default_count_unit()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Ingredients.Add(NewIngredient("All-Purpose Flour"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var saved = await db.Ingredients.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(saved.DefaultCountUnitId);
        Assert.Null(saved.DefaultCountUnitDimension);
    }

    /// <summary>
    /// The database refuses to delete a unit an ingredient still points at (<c>ON DELETE RESTRICT</c>), so a
    /// unit in use cannot vanish out from under a suggestion. Retiring one means clearing
    /// <see cref="MeasurementUnit.IsActive"/>, not deleting the row.
    /// </summary>
    [Fact]
    public async Task A_referenced_count_unit_cannot_be_deleted()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var clove = await SeedGarlicWithCloveAsync(db);

        // Clear first: the guarantee under test is the database's, and a tracked dependent would let EF resolve
        // the reference client-side before the database ever sees the delete (see the test below).
        db.ChangeTracker.Clear();
        db.MeasurementUnits.Remove(await db.MeasurementUnits.SingleAsync(unit => unit.Id == clove, TestContext.Current.CancellationToken));

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>
    /// Records a sharp edge rather than asserting a guarantee. With the dependent ingredient tracked in the
    /// same context, EF severs the reference client-side — it nulls both foreign-key columns and the delete
    /// then succeeds, so the <c>RESTRICT</c> above never fires. The protection is therefore the database's
    /// alone: application code that deletes a measurement unit while ingredients are loaded silently clears
    /// their default-count suggestion instead of failing. Anything that deletes reference data should go
    /// through a path that does not have dependents tracked.
    /// </summary>
    [Fact]
    public async Task Deleting_a_referenced_unit_with_the_ingredient_tracked_clears_the_reference_instead()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var clove = await SeedGarlicWithCloveAsync(db);

        db.MeasurementUnits.Remove(await db.MeasurementUnits.SingleAsync(unit => unit.Id == clove, TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var garlic = Assert.Single(await db.Ingredients.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Null(garlic.DefaultCountUnitId);
        Assert.Null(garlic.DefaultCountUnitDimension);
        Assert.Empty(await db.MeasurementUnits.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Seeds garlic with "clove" as its default count unit, returning the unit's id.</summary>
    private static async Task<Guid> SeedGarlicWithCloveAsync(CreatorPantryDbContext db)
    {
        var clove = NewUnit("clove", MeasurementDimension.Count);
        var garlic = NewIngredient("Garlic");
        garlic.DefaultCountUnitId = clove.Id;
        garlic.DefaultCountUnitDimension = MeasurementDimension.Count;
        db.MeasurementUnits.Add(clove);
        db.Ingredients.Add(garlic);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return clove.Id;
    }

    // --- Normalization and search text ------------------------------------------------------------------

    [Theory]
    [InlineData("Flour", "flour")]
    [InlineData("  All-Purpose Flour  ", "all purpose flour")]
    [InlineData("All-Purpose Flour (Unbleached)", "all purpose flour unbleached")]
    [InlineData("Jalapeño", "jalapeno")]
    [InlineData("Crème Fraîche", "creme fraiche")]
    [InlineData("Salt & Pepper", "salt pepper")]
    [InlineData("Half-and-Half", "half and half")]
    [InlineData("2% Milk", "2 milk")]
    [InlineData("---", "")]
    public void Normalizing_a_name_folds_case_diacritics_and_punctuation(string name, string expected) =>
        Assert.Equal(expected, IngredientPolicy.NormalizeName(name));

    /// <summary>
    /// Plurals are alias rows, not a stemming rule. Normalization must leave them distinct so the alias table
    /// stays the inspectable record of which forms mean what.
    /// </summary>
    [Fact]
    public void Normalizing_a_name_does_not_singularize() =>
        Assert.NotEqual(IngredientPolicy.NormalizeName("egg"), IngredientPolicy.NormalizeName("eggs"));

    /// <summary>Phrases, not a bag of words: reordering would stop "cream cheese" matching.</summary>
    [Fact]
    public void Search_text_preserves_whole_phrases()
    {
        var searchText = IngredientPolicy.BuildSearchText("cream cheese", ["soft cheese"]);

        Assert.Equal($"cream cheese{IngredientPolicy.SearchTextSeparator}soft cheese", searchText);
    }

    [Fact]
    public void Search_text_drops_duplicate_and_empty_phrases()
    {
        var searchText = IngredientPolicy.BuildSearchText("cornstarch", ["cornstarch", "", "  ", "corn flour"]);

        Assert.Equal($"cornstarch{IngredientPolicy.SearchTextSeparator}corn flour", searchText);
    }

    /// <summary>
    /// The property that makes drift detection possible: the same aliases in any order give the same string,
    /// so a rebuild from a database query does not depend on the order the rows came back in.
    /// </summary>
    [Fact]
    public void Search_text_does_not_depend_on_alias_order() =>
        Assert.Equal(
            IngredientPolicy.BuildSearchText("cornstarch", ["corn starch", "corn flour", "cornflour"]),
            IngredientPolicy.BuildSearchText("cornstarch", ["cornflour", "corn starch", "corn flour"]));

    /// <summary>Truncating mid-phrase could match something the ingredient is not, so it stops at a boundary.</summary>
    [Fact]
    public void Search_text_truncates_on_a_phrase_boundary()
    {
        var aliases = Enumerable.Range(0, 200).Select(index => $"alias number {index}").ToArray();

        var searchText = IngredientPolicy.BuildSearchText("cornstarch", aliases);

        Assert.True(searchText.Length <= IngredientPolicy.SearchTextMaxLength);
        Assert.DoesNotContain($"{IngredientPolicy.SearchTextSeparator}alias number", searchText.Split(IngredientPolicy.SearchTextSeparator)[^1]);
        Assert.All(searchText.Split(IngredientPolicy.SearchTextSeparator), phrase => Assert.Contains(phrase, aliases.Append("cornstarch")));
    }

    /// <summary>
    /// <see cref="Ingredient.SearchText"/> is derived, so it can drift. This is the assertion a rebuild rule
    /// has to keep true.
    /// </summary>
    [Fact]
    public async Task Stored_search_text_equals_a_fresh_rebuild()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var cornstarch = NewIngredient("Cornstarch", "corn starch", "corn flour");
        db.Ingredients.Add(cornstarch);
        db.IngredientAliases.AddRange(NewAlias(cornstarch.Id, "corn starch"), NewAlias(cornstarch.Id, "corn flour"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var saved = await db.Ingredients.SingleAsync(TestContext.Current.CancellationToken);
        var aliases = await db.IngredientAliases
            .Where(alias => alias.IngredientId == saved.Id)
            .Select(alias => alias.NormalizedAlias)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IngredientPolicy.BuildSearchText(saved.NormalizedName, aliases), saved.SearchText);
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private static Ingredient NewIngredient(string canonicalName, params string[] aliases)
    {
        var normalizedName = IngredientPolicy.NormalizeName(canonicalName);

        return new Ingredient
        {
            Id = Guid.NewGuid(),
            CanonicalName = canonicalName,
            NormalizedName = normalizedName,
            SearchText = IngredientPolicy.BuildSearchText(normalizedName, aliases.Select(IngredientPolicy.NormalizeName)),
            IsActive = true,
        };
    }

    private static IngredientAlias NewAlias(Guid ingredientId, string alias) => new()
    {
        Id = Guid.NewGuid(),
        IngredientId = ingredientId,
        Alias = alias,
        NormalizedAlias = IngredientPolicy.NormalizeName(alias),
    };

    private static FoodCategory NewCategory(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = code,
        IsActive = true,
    };

    private static MeasurementUnit NewUnit(string code, MeasurementDimension dimension) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = code,
        PluralName = $"{code}s",
        Abbreviation = code,
        Dimension = dimension,
        System = MeasurementSystem.Neutral,
        BaseUnitFactor = MeasurementPolicy.RequiresBaseUnitFactor(dimension) ? 1m : null,
        DisplayPrecision = 0,
        IsActive = true,
    };
}
