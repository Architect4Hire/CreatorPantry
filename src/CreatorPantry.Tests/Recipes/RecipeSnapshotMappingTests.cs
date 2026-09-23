using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The snapshot must reconstruct a recipe exactly, survive a round trip through storage, and refuse to be
/// read under a schema it was not written for. Without all three it is an archive that quietly lies.
/// </summary>
public sealed class RecipeSnapshotMappingTests
{
    [Fact]
    public void A_captured_recipe_restores_to_the_same_content()
    {
        var original = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        original.Headnote = "The one my grandmother made.";
        original.YieldText = "makes 12 muffins";
        original.YieldQuantity = 12m;

        var restored = RecipeSnapshotMapper.Restore(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(original)), original.Id);

        AssertSameContent(original, restored);
    }

    [Fact]
    public void A_snapshot_survives_serialization()
    {
        var original = RecipeAggregateFixture.NewRecipe("Olive oil cake");

        var json = RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(original)));
        var restored = RecipeSnapshotMapper.Restore(RecipeSnapshotSerializer.Deserialize(json), original.Id);

        AssertSameContent(original, restored);
    }

    [Fact]
    public void Capturing_the_same_recipe_twice_produces_the_same_document()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");

        // Diffing depends on this. If capture were sensitive to the order EF happened to materialise a
        // collection in, an unchanged recipe would produce a non-empty diff and every version would look
        // like an edit.
        Assert.Equal(
            RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe))),
            RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe))));
    }

    [Fact]
    public void Stable_ids_survive_the_round_trip()
    {
        var original = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var originalLineId = original.IngredientGroups.Single().Ingredients.Single().Id;
        var originalStepId = original.InstructionGroups.Single().Steps.Single().Id;

        var restored = RecipeSnapshotMapper.Restore(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(original)), original.Id);

        // What lets a diff match a moved line to itself rather than reporting a delete and an insert.
        Assert.Equal(originalLineId, restored.IngredientGroups.Single().Ingredients.Single().Id);
        Assert.Equal(originalStepId, restored.InstructionGroups.Single().Steps.Single().Id);
    }

    [Fact]
    public void A_restore_carries_no_ownership_and_no_authorship()
    {
        var original = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        original.WorkspaceId = Guid.NewGuid();
        original.CreatedByMembershipId = Guid.NewGuid();

        var restored = RecipeSnapshotMapper.Restore(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(original)), original.Id);

        // A snapshot is content. If it carried a workspace or an author, restoring one would be a way to
        // move a recipe between workspaces or to rewrite who wrote it.
        Assert.Equal(Guid.Empty, restored.WorkspaceId);
        Assert.Equal(Guid.Empty, restored.CreatedByMembershipId);
        Assert.All(restored.IngredientGroups, group => Assert.Equal(Guid.Empty, group.WorkspaceId));
    }

    [Fact]
    public void A_document_from_a_newer_schema_is_refused_rather_than_guessed_at()
    {
        var document = RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(RecipeAggregateFixture.NewRecipe("Olive oil cake"))) with
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion + 1,
        };

        var failure = Assert.Throws<NotSupportedException>(() => RecipeSnapshotMapper.Restore(document, Guid.NewGuid()));

        Assert.Contains("schema version", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_document_with_no_schema_version_is_refused()
    {
        // The gap this closes: SchemaVersion used to default to CurrentSchemaVersion, so a truncated row, a
        // hand-edited one, or JSON from another system deserialized to "version 1" and sailed through the
        // check that exists to catch exactly that.
        var document = RecipeSnapshotSerializer.Deserialize("""{"recipe":{"title":"x"}}""");

        Assert.Equal(0, document.SchemaVersion);
        Assert.Throws<NotSupportedException>(() => RecipeSnapshotMapper.Restore(document, Guid.NewGuid()));
    }

    [Fact]
    public void Every_schema_version_this_build_claims_to_read_is_readable()
    {
        // The guard against a future schema bump silently orphaning every snapshot already in the database.
        // When CurrentSchemaVersion becomes 2, this fails until a v1 upgrade path exists — which is the
        // point, because the alternative is a creator's history going dark on a routine release.
        for (var version = RecipeSnapshotDocument.MinimumReadableSchemaVersion;
             version <= RecipeSnapshotDocument.CurrentSchemaVersion;
             version++)
        {
            var document = RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(RecipeAggregateFixture.NewRecipe("Cake"))) with
            {
                SchemaVersion = version,
            };

            var restored = RecipeSnapshotMapper.Restore(document, Guid.NewGuid());
            Assert.Equal("Cake", restored.Title);
        }
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    public void Unreadable_stored_text_fails_as_one_documented_type(string stored)
    {
        // A caller translating storage failures into ProblemDetails with a stable error code should not have
        // to know System.Text.Json's exception hierarchy to catch the case that actually happens.
        Assert.Throws<InvalidOperationException>(() => RecipeSnapshotSerializer.Deserialize(stored));
    }

    [Fact]
    public void A_document_whose_collections_are_null_restores_as_empty_rather_than_crashing()
    {
        var document = RecipeSnapshotSerializer.Deserialize(
            $$"""{"schemaVersion":{{RecipeSnapshotDocument.CurrentSchemaVersion}},"recipe":{"title":"x"},"ingredientGroups":null,"instructionGroups":null,"equipment":null,"assetLinks":null}""");

        // JSON null overrides the `= []` initializer, so this used to be a NullReferenceException out of the
        // middle of Restore — an unhandled 500 on a corrupt archive row.
        var restored = RecipeSnapshotMapper.Restore(document, Guid.NewGuid());

        Assert.Empty(restored.IngredientGroups);
        Assert.Empty(restored.InstructionGroups);
        Assert.Empty(restored.Equipment);
        Assert.Empty(restored.AssetLinks);
    }

    [Fact]
    public void Creator_text_survives_exactly_whatever_it_contains()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Crème brûlée 🍮");
        recipe.Headnote = "מתכון — with <script> & \"quotes\" and a combining é";
        recipe.IngredientGroups.Single().Ingredients.Single().DisplayText = "½ cup sucre · 250 g crème";

        var restored = RecipeSnapshotMapper.Restore(
            RecipeSnapshotSerializer.Deserialize(RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe)))),
            recipe.Id);

        // The encoder's escaping is part of the storage contract: swapping in a relaxed one for readability
        // would change stored bytes, and any loss here is loss of the creator's own words.
        Assert.Equal(recipe.Title, restored.Title);
        Assert.Equal(recipe.Headnote, restored.Headnote);
        Assert.Equal(
            recipe.IngredientGroups.Single().Ingredients.Single().DisplayText,
            restored.IngredientGroups.Single().Ingredients.Single().DisplayText);
    }

    [Fact]
    public void Enum_values_are_stored_as_numbers_so_a_rename_cannot_change_the_past()
    {
        var json = RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(RecipeAggregateFixture.NewRecipe("Cake"))));

        // A C# member renamed next year must not change what a document written today means.
        Assert.DoesNotContain("\"Hero\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"role\":{(int)RecipeAssetRole.Hero}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cleared_field_is_stored_rather_than_omitted()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Cake");
        recipe.Headnote = null;

        var json = RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe)));

        // "The creator cleared this" and "this field did not exist yet" have to stay distinguishable; that
        // distinction is the reason this is a document and not a shadow table.
        Assert.Contains("\"headnote\":null", json, StringComparison.Ordinal);
    }

    private static void AssertSameContent(Recipe original, Recipe restored)
    {
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Headnote, restored.Headnote);
        Assert.Equal(original.YieldText, restored.YieldText);
        Assert.Equal(original.YieldQuantity, restored.YieldQuantity);
        Assert.Equal(original.Status, restored.Status);

        var originalLine = original.IngredientGroups.Single().Ingredients.Single();
        var restoredLine = restored.IngredientGroups.Single().Ingredients.Single();
        Assert.Equal(originalLine.DisplayText, restoredLine.DisplayText);
        Assert.Equal(originalLine.Quantity, restoredLine.Quantity);
        Assert.Equal(originalLine.MeasurementUnitId, restoredLine.MeasurementUnitId);
        Assert.Equal(originalLine.MatchStatus, restoredLine.MatchStatus);
        Assert.Equal(originalLine.ScalingBehavior, restoredLine.ScalingBehavior);

        var originalStep = original.InstructionGroups.Single().Steps.Single();
        var restoredStep = restored.InstructionGroups.Single().Steps.Single();
        Assert.Equal(originalStep.Text, restoredStep.Text);
        Assert.Equal(originalStep.TemperatureValue, restoredStep.TemperatureValue);
        Assert.Equal(originalStep.TemperatureUnitId, restoredStep.TemperatureUnitId);

        Assert.Equal(original.Equipment.Single().DisplayText, restored.Equipment.Single().DisplayText);
        Assert.Equal(original.AssetLinks.Single().MediaAssetId, restored.AssetLinks.Single().MediaAssetId);
        Assert.Equal(original.AssetLinks.Single().Role, restored.AssetLinks.Single().Role);
    }
}
