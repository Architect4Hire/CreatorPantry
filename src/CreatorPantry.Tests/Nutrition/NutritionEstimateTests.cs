using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// An estimate read end to end (14.3): a recipe version, this workspace's confirmed mappings from SQL Server, the
/// evidence source's current nutrients and the unit catalogue — and what happens when any of them disagrees.
/// </summary>
public sealed class NutritionEstimateTests(SqlServerNutritionFixture fixture) : IClassFixture<SqlServerNutritionFixture>
{
    private static readonly Guid WorkspaceA = SqlServerNutritionFixture.WorkspaceA;
    private static readonly Guid WorkspaceB = SqlServerNutritionFixture.WorkspaceB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_mapping_with_the_sources_nutrients_is_counted()
    {
        var (recipe, version, ingredient, recordId) = await RecipeWithOneConfirmedLineAsync("counted");

        var estimate = Value(await Estimates(WorkspaceA).EstimateAsync(recipe, version, null, Ct));

        Assert.True(estimate.Publishable);
        var line = Assert.Single(estimate.Lines);
        Assert.Equal(NutritionLineOutcome.Included, line.Outcome);
        Assert.Equal(new NutritionEstimateSourceServiceModel("usda-fdc", recordId, "sr-legacy/2019-04-01"), line.Source);
        Assert.Equal(1820m, Assert.Single(estimate.Nutrients, nutrient => nutrient.Code == NutrientCode.Energy).WholeRecipe.Low);
        Assert.Equal(EvidenceSourceCatalog.UsdaFoodDataCentral.Citation, Assert.Single(estimate.Sources).Citation);
        Assert.NotEqual(Guid.Empty, ingredient);
    }

    [Fact]
    public async Task Another_workspaces_mapping_never_counts()
    {
        var (recipe, version, _, _) = await RecipeWithOneConfirmedLineAsync("isolation");

        // The same version's lines, read in a workspace that has decided nothing about the ingredient.
        var estimate = Value(await Estimates(WorkspaceB).EstimateAsync(recipe, version, null, Ct));

        var line = Assert.Single(estimate.Lines);
        Assert.Equal((NutritionLineOutcome.Unmapped, NutritionLineReason.NoMapping), (line.Outcome, line.Reason));
        Assert.False(estimate.Publishable);
        Assert.Empty(estimate.Nutrients);
    }

    [Fact]
    public async Task A_newer_source_version_than_was_confirmed_is_a_line_for_review_and_the_mapping_is_not_touched()
    {
        var (recipe, version, ingredient, recordId) = await RecipeWithOneConfirmedLineAsync("stale");
        fixture.Evidence.Has(recordId, "Food", "sr-legacy/2026-04-01", [], [new(NutrientCode.Energy, 400m, NutrientUnit.Kilocalorie, "Energy", null)]);

        var estimate = Value(await Estimates(WorkspaceA).EstimateAsync(recipe, version, null, Ct));

        var line = Assert.Single(estimate.Lines);
        Assert.Equal((NutritionLineOutcome.NeedsReview, NutritionLineReason.SourceVersionChanged), (line.Outcome, line.Reason));
        Assert.False(estimate.Publishable);

        // An estimate reads; refreshing the mapping is a decision of its own.
        await using var scope = fixture.ScopeFor(WorkspaceA);
        var mapping = scope.ServiceProvider.GetRequiredService<INutritionMappingFacade>();
        Assert.Equal(NutritionMappingStatus.Confirmed, Value(await mapping.GetAsync(ingredient, Ct)).Status);
    }

    [Fact]
    public async Task A_record_the_source_withdrew_is_a_line_for_review()
    {
        var (recipe, version, _, recordId) = await RecipeWithOneConfirmedLineAsync("withdrawn");
        fixture.Evidence.Answers(recordId, ReferenceEvidenceOutcome.NotFound);

        var line = Assert.Single(Value(await Estimates(WorkspaceA).EstimateAsync(recipe, version, null, Ct)).Lines);

        Assert.Equal((NutritionLineOutcome.NeedsReview, NutritionLineReason.ReferenceWithdrawn), (line.Outcome, line.Reason));
    }

    [Fact]
    public async Task A_source_that_cannot_be_asked_fails_the_estimate_rather_than_shrink_it()
    {
        var (recipe, version, _, _) = await RecipeWithOneConfirmedLineAsync("outage");

        fixture.Evidence.Down = true;
        try
        {
            var result = await Estimates(WorkspaceA).EstimateAsync(recipe, version, null, Ct);
            Assert.Equal(NutritionErrorCodes.SourceUnavailable, result.Error?.Code);
        }
        finally
        {
            fixture.Evidence.Down = false;
        }
    }

    [Fact]
    public async Task A_version_that_is_not_there_is_not_found()
    {
        var result = await Estimates(WorkspaceA).EstimateAsync(Guid.NewGuid(), Guid.NewGuid(), null, Ct);

        Assert.Equal(NutritionErrorCodes.EstimateRecipeNotFound, result.Error?.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10000.0001")]
    [InlineData("1.00001")]
    public async Task A_serving_count_that_cannot_be_used_is_refused(string servings)
    {
        var result = await Estimates(WorkspaceA).EstimateAsync(Guid.NewGuid(), Guid.NewGuid(), decimal.Parse(servings, System.Globalization.CultureInfo.InvariantCulture), Ct);

        Assert.Equal(NutritionErrorCodes.EstimateInvalid, result.Error?.Code);
    }

    [Fact]
    public async Task A_viewer_may_read_an_estimate()
    {
        var (recipe, version, _, _) = await RecipeWithOneConfirmedLineAsync("viewer");

        Assert.True((await Estimates(WorkspaceA, WorkspaceRole.Viewer).EstimateAsync(recipe, version, null, Ct)).Succeeded);
    }

    // ---- Helpers ----

    /// <summary>A recipe version of one line — 500 g of a new ingredient — whose mapping workspace A has confirmed.</summary>
    private async Task<(Guid Recipe, Guid Version, Guid Ingredient, string RecordId)> RecipeWithOneConfirmedLineAsync(string stem)
    {
        var name = $"{stem} {Guid.NewGuid():N}";
        var ingredient = await fixture.NewIngredientAsync(name);
        var recordId = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        fixture.Evidence.Has(recordId, "Food", "sr-legacy/2019-04-01", [], [new(NutrientCode.Energy, 364m, NutrientUnit.Kilocalorie, "Energy", "Calculated")]);
        fixture.Evidence.Finds(name, recordId);

        await using (var scope = fixture.ScopeFor(WorkspaceA))
        {
            var mappings = scope.ServiceProvider.GetRequiredService<INutritionMappingFacade>();
            var candidate = Value(await mappings.ProposeAsync(ingredient, Ct));
            Value(await mappings.ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct));
        }

        var (recipe, version) = (Guid.NewGuid(), Guid.NewGuid());
        fixture.Versions.Add(recipe, version, new NutritionRecipeVersion(
            1,
            [new NutritionEstimateLineInput(Guid.NewGuid(), $"500 g {name}", 500m, null, SqlServerNutritionFixture.GramId, ingredient, false)],
            ServingCount: 4m,
            YieldQuantity: null,
            ServingSize: null));

        return (recipe, version, ingredient, recordId);
    }

    private INutritionEstimateFacade Estimates(Guid workspaceId, WorkspaceRole role = WorkspaceRole.Editor) =>
        fixture.ScopeFor(workspaceId, role).ServiceProvider.GetRequiredService<INutritionEstimateFacade>();

    private static T Value<T>(OperationResult<T> result)
    {
        Assert.True(result.Succeeded, $"{result.Error?.Code}: {result.Error?.Message}");
        return result.Value!;
    }
}

/// <summary>A recipe version's snapshot, as an estimate reads it.</summary>
public sealed class NutritionRecipeVersionReaderTests
{
    [Fact]
    public void Lines_are_read_group_by_group_in_the_creators_order_with_the_versions_yield()
    {
        var (first, second, third) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (unit, ingredient) = (Guid.NewGuid(), Guid.NewGuid());

        var snapshot = new RecipeSnapshotServiceModel(7, new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = "Flatbread", ServingCount = 4m, YieldQuantity = 600m, ServingSize = 150m },
            IngredientGroups =
            [
                new RecipeSnapshotIngredientGroup
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 2,
                    Ingredients = [new RecipeSnapshotIngredient { Id = third, SortOrder = 0, DisplayText = "third" }],
                },
                new RecipeSnapshotIngredientGroup
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 1,
                    Ingredients =
                    [
                        new RecipeSnapshotIngredient { Id = second, SortOrder = 5, DisplayText = "second", IsOptional = true },
                        new RecipeSnapshotIngredient
                        {
                            Id = first,
                            SortOrder = 1,
                            DisplayText = "250–300 g flour",
                            Quantity = 250m,
                            QuantityUpper = 300m,
                            MeasurementUnitId = unit,
                            MeasurementUnitDimension = MeasurementDimension.Mass,
                            IngredientId = ingredient,
                        },
                    ],
                },
            ],
        });

        var version = NutritionRecipeVersionReader.FromSnapshot(snapshot);

        Assert.Equal(7, version.VersionNumber);
        Assert.Equal([first, second, third], version.Lines.Select(line => line.LineId));
        Assert.Equal(new NutritionEstimateLineInput(first, "250–300 g flour", 250m, 300m, unit, ingredient, false), version.Lines[0]);
        Assert.True(version.Lines[1].IsOptional);
        Assert.Equal((4m, 600m, 150m), (version.ServingCount!.Value, version.YieldQuantity!.Value, version.ServingSize!.Value));
    }
}
