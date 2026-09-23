using System.Reflection;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The invariants the recipe aggregate's EF configuration puts in the database rather than in whichever
/// layer happens to write next. Each one is exercised by trying to break it.
/// </summary>
public sealed class RecipeAggregateConstraintTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_step_temperature_must_be_measured_in_a_temperature_unit()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        // "Bake at 180 g." The composite foreign key would accept a gram — it is a real unit — so the check
        // constraint is what refuses it.
        var step = recipe.InstructionGroups.Single().Steps.Single();
        step.TemperatureUnitId = RecipeAggregateFixture.GramId;
        step.TemperatureUnitDimension = MeasurementDimension.Mass;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_ingredient_quantity_may_not_be_measured_in_degrees()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        var ingredient = recipe.IngredientGroups.Single().Ingredients.Single();
        ingredient.MeasurementUnitId = RecipeAggregateFixture.CelsiusId;
        ingredient.MeasurementUnitDimension = MeasurementDimension.Temperature;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_quantity_range_must_run_upward_from_a_low_end()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        var ingredient = recipe.IngredientGroups.Single().Ingredients.Single();
        ingredient.Quantity = null;
        ingredient.QuantityUpper = 3m;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_matched_line_must_name_the_ingredient_it_matched()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        // Matched with nothing to point at: the two columns would then disagree, and an interface reading
        // them has to guess which one to believe.
        var ingredient = recipe.IngredientGroups.Single().Ingredients.Single();
        ingredient.IngredientId = null;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_recipe_has_at_most_one_hero_image()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        db.RecipeAssetLinks.Add(new RecipeAssetLink
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            SortOrder = 1,
            MediaAssetId = Guid.NewGuid(),
            Role = RecipeAssetRole.Hero,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_recipe_may_have_many_gallery_images()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        // The unique index is filtered to the hero role, so it must not constrain anything else.
        db.RecipeAssetLinks.AddRange(
            NewGalleryLink(recipe.Id, sortOrder: 1),
            NewGalleryLink(recipe.Id, sortOrder: 2));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await db.RecipeAssetLinks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_lines_in_one_group_cannot_share_a_position()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);
        var group = recipe.IngredientGroups.Single();

        db.RecipeIngredients.Add(NewLine(recipe.Id, group.Id, sortOrder: 0));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Positions_restart_in_each_group()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        var second = new RecipeIngredientGroup
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            Title = "For the glaze",
            SortOrder = 1,
        };

        db.RecipeIngredientGroups.Add(second);
        // Position 0 again, which is correct: ordering is unique within a group, not within a recipe.
        db.RecipeIngredients.Add(NewLine(recipe.Id, second.Id, sortOrder: 0));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.RecipeIngredients.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_yield_unit_needs_a_yield_to_measure()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        recipe.YieldUnitId = RecipeAggregateFixture.GramId;
        recipe.YieldUnitDimension = MeasurementDimension.Mass;
        recipe.YieldQuantity = null;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_yield_may_be_a_bare_number_with_no_unit()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        // "Makes 12", which is how creators routinely write it. The rule runs one way only.
        recipe.YieldText = "makes 12 muffins";
        recipe.YieldQuantity = 12m;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12m, (await db.Recipes.SingleAsync(TestContext.Current.CancellationToken)).YieldQuantity);
    }

    [Fact]
    public async Task Times_cannot_run_backwards()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        recipe.PrepTimeMinutes = -10;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_total_time_need_not_be_the_sum_of_its_parts()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);

        // Prep overlaps cooking and resting is unattended, so a creator's stated total is a fact in its own
        // right. Nothing may recompute it from the parts.
        recipe.PrepTimeMinutes = 20;
        recipe.CookTimeMinutes = 25;
        recipe.RestTimeMinutes = 60;
        recipe.TotalTimeMinutes = 75;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(75, (await db.Recipes.SingleAsync(TestContext.Current.CancellationToken)).TotalTimeMinutes);
    }

    [Fact]
    public async Task An_unmatched_line_keeps_the_creators_text()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedAsync(db);
        var group = recipe.IngredientGroups.Single();

        // Nothing structured at all: no quantity, no unit, no match. recipes.md makes this a complete and
        // correct ingredient line, and the configuration must not require any of the additive columns.
        db.RecipeIngredients.Add(new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeIngredientGroupId = group.Id,
            SortOrder = 1,
            DisplayText = "flaky sea salt, to taste",
            MatchStatus = IngredientMatchStatus.NoMatch,
            ScalingBehavior = IngredientScaling.ReviewRequired,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var line = await db.RecipeIngredients
            .SingleAsync(ingredient => ingredient.SortOrder == 1, TestContext.Current.CancellationToken);

        Assert.Equal("flaky sea salt, to taste", line.DisplayText);
        Assert.Null(line.IngredientId);
        Assert.Equal(IngredientScaling.ReviewRequired, line.ScalingBehavior);
    }

    [Fact]
    public async Task A_referenced_ingredient_cannot_be_deleted_out_from_under_a_recipe()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        await SeedAsync(db);

        // The catalogue retires rows with IsActive precisely so this never has to happen; the FK is what
        // makes the alternative impossible rather than merely discouraged.
        var flour = await db.Ingredients.SingleAsync(
            ingredient => ingredient.Id == RecipeAggregateFixture.FlourId, TestContext.Current.CancellationToken);
        db.Ingredients.Remove(flour);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(typeof(RecipeStatus))]
    [InlineData(typeof(IngredientScaling))]
    [InlineData(typeof(IngredientMatchStatus))]
    [InlineData(typeof(RecipeAssetRole))]
    [InlineData(typeof(RecipeVersionSource))]
    [InlineData(typeof(RecipeVersionReadiness))]
    // Not this module's, but its numeric values are written into every stored snapshot document as
    // YieldUnitDimension, MeasurementUnitDimension and TemperatureUnitDimension — so renumbering it would
    // change what every historical archive means, which is this rule's whole concern.
    [InlineData(typeof(MeasurementDimension))]
    public void Recipe_enums_are_ordered_not_flags(Type enumType)
    {
        // Their numeric values are persisted and are named literally by check constraints and by the filtered
        // hero index, so renumbering one silently re-points existing rows.
        Assert.Null(enumType.GetCustomAttribute<FlagsAttribute>());

        var values = Enum.GetValues(enumType).Cast<int>().ToList();
        Assert.Equal(values.OrderBy(value => value), values);
        Assert.Equal(values.Distinct(), values);
    }

    private static async Task<Recipe> SeedAsync(Domain.Managers.Persistence.CreatorPantryDbContext db)
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe;
    }

    private static RecipeAssetLink NewGalleryLink(Guid recipeId, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        SortOrder = sortOrder,
        MediaAssetId = Guid.NewGuid(),
        Role = RecipeAssetRole.Gallery,
    };

    private static RecipeIngredient NewLine(Guid recipeId, Guid groupId, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        RecipeIngredientGroupId = groupId,
        SortOrder = sortOrder,
        DisplayText = "1 teaspoon fine sea salt",
    };
}
