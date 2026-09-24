using System.Reflection;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Makes "add a field to the snapshot and forget to compare it" a failing test rather than something a
/// reviewer has to notice.
/// </summary>
/// <remarks>
/// <para>
/// The same asymmetry <c>RecipeSnapshotCompletenessTests</c> relies on, one layer further along. A field
/// that is archived but never diffed produces a comparison that says a version is unchanged when it is not
/// — which is worse than not archiving it at all, because the creator is told something false rather than
/// shown nothing.
/// </para>
/// <para>
/// Two tests, because either alone can be satisfied without the field actually being compared. The first is
/// reflection over the snapshot records: every archived property must have a
/// <see cref="RecipeComparisonField"/>. The second is behavioural: every
/// <see cref="RecipeComparisonField"/> must be emitted by <see cref="RecipeComparer"/> when the
/// corresponding value actually changes. Adding a property fails the first until a member exists, and then
/// fails the second until the comparer emits it.
/// </para>
/// </remarks>
public sealed class RecipeComparisonCompletenessTests
{
    /// <summary>
    /// Snapshot properties that deliberately have no comparison field, and why.
    /// </summary>
    /// <remarks>
    /// The deliberate seam. Leaving a property out of the comparison is a real decision; forgetting one is
    /// invisible. Putting a name here makes the decision a visible line in a diff.
    /// </remarks>
    private static readonly Dictionary<string, string> NotCompared = new()
    {
        ["Id"] = "identifies an item rather than describing it; it is what the comparison matches on",
        ["SortOrder"] = "position, reported as RecipeItemChange.Moved and the ranks beside it — never as a field change",
        ["SchemaVersion"] = "the shape the archive was written in; two readable versions may legitimately differ",
        ["Recipe"] = "the header, whose own properties are compared individually",
        ["IngredientGroups"] = "a child collection, compared as items",
        ["InstructionGroups"] = "a child collection, compared as items",
        ["Ingredients"] = "a child collection, compared as items",
        ["Steps"] = "a child collection, compared as items",
        ["Equipment"] = "a child collection, compared as items",
        ["AssetLinks"] = "a child collection, compared as items",
        ["Tags"] = "a child collection, compared as items",
    };

    /// <summary>
    /// The prefix each record's comparison fields carry, so one flat enum can name properties that several
    /// records spell identically — <c>Title</c>, <c>Note</c> and <c>IsOptional</c> all appear more than once.
    /// </summary>
    private static readonly (Type Record, string Prefix)[] RecordPrefixes =
    [
        (typeof(RecipeSnapshotDocument), string.Empty),
        (typeof(RecipeSnapshotHeader), string.Empty),
        (typeof(RecipeSnapshotIngredientGroup), "IngredientGroup"),
        (typeof(RecipeSnapshotIngredient), "Ingredient"),
        (typeof(RecipeSnapshotInstructionGroup), "InstructionGroup"),
        (typeof(RecipeSnapshotInstructionStep), "Step"),
        (typeof(RecipeSnapshotEquipment), "Equipment"),
        (typeof(RecipeSnapshotAssetLink), "Asset"),
        (typeof(RecipeSnapshotTag), "Tag"),
    ];

    /// <summary>
    /// The handful of properties whose field is not simply prefix plus property name, because the
    /// convention would have produced <c>IngredientIngredientNameText</c> and its like.
    /// </summary>
    /// <remarks>
    /// Also the lever for keeping a published name stable through an internal rename. <c>RecipeComparisonField</c>
    /// members are returned on the compare route, so the naming convention this test enforces means renaming a
    /// snapshot property would otherwise demand renaming a wire value — a breaking change produced by a
    /// refactor. Adding the renamed property here, pointing at the member it already had, moves the C# name
    /// without moving the contract.
    /// </remarks>
    private static readonly Dictionary<string, RecipeComparisonField> NamedDifferently = new()
    {
        ["RecipeSnapshotIngredient.IngredientNameText"] = RecipeComparisonField.IngredientNameText,
        ["RecipeSnapshotIngredient.IngredientId"] = RecipeComparisonField.IngredientReferenceId,
        ["RecipeSnapshotEquipment.EquipmentTypeId"] = RecipeComparisonField.EquipmentTypeId,
    };

    [Fact]
    public void Every_archived_property_has_a_comparison_field()
    {
        var fields = Enum.GetValues<RecipeComparisonField>().ToDictionary(field => field.ToString());
        var missing = new List<string>();

        foreach (var (record, prefix) in RecordPrefixes)
        {
            foreach (var property in record.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (NotCompared.ContainsKey(property.Name))
                {
                    continue;
                }

                var key = $"{record.Name}.{property.Name}";

                if (NamedDifferently.ContainsKey(key) || fields.ContainsKey(prefix + property.Name))
                {
                    continue;
                }

                missing.Add($"{key} has no RecipeComparisonField (expected '{prefix + property.Name}')");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_comparison_field_is_emitted_when_its_value_changes()
    {
        // One recipe mutated in place, not two built separately: the children keep their identifiers, so
        // every field arrives as an edit to a retained item rather than as an addition, which is the path
        // this is meant to cover.
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        var before = RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe));

        ChangeEveryValue(recipe);

        var comparison = RecipeComparer.Compare(
            before,
            RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe)));

        var emitted = comparison.Sections
            .SelectMany(section => section.FieldChanges.Concat(section.ItemChanges.SelectMany(item => item.FieldChanges)))
            .Select(change => change.Field)
            .ToHashSet();

        // A field the comparer never emits is a field a creator will never be told about.
        Assert.Empty(Enum.GetValues<RecipeComparisonField>().Except(emitted));
    }

    /// <summary>
    /// Replaces every archived value on a fully populated recipe with a different one, in place.
    /// </summary>
    /// <remarks>
    /// Every value here differs from what <see cref="RecipeAggregateFixture.FullyPopulatedRecipe"/> set. A
    /// value that happened to match would quietly drop its field from the comparison and weaken the
    /// assertion above without failing it — which is why the fixture sets everything to a distinct
    /// non-default in the first place.
    /// </remarks>
    private static void ChangeEveryValue(Recipe recipe)
    {
        recipe.Title = "A different title";
        recipe.Description = "A different description.";
        recipe.Headnote = "A different headnote.";
        recipe.Notes = "Different notes.";
        recipe.StorageNotes = "Different storage notes.";
        recipe.AttributionText = "Different attribution.";
        recipe.SourceUrl = "https://example.com/a-different-source";
        recipe.CuisineId = Guid.NewGuid();
        recipe.CourseId = Guid.NewGuid();
        recipe.PrimaryTechniqueId = Guid.NewGuid();
        recipe.PrepTimeMinutes = 21;
        recipe.CookTimeMinutes = 26;
        recipe.RestTimeMinutes = 61;
        recipe.TotalTimeMinutes = 76;
        recipe.YieldText = "makes 13 muffins";
        recipe.YieldQuantity = 13m;
        recipe.YieldUnitId = Guid.NewGuid();
        recipe.YieldUnitDimension = MeasurementDimension.Mass;
        recipe.Status = RecipeStatus.Archived;

        var ingredientGroup = recipe.IngredientGroups.Single();
        ingredientGroup.Title = "For the batter";

        var line = ingredientGroup.Ingredients.Single();
        line.DisplayText = "3 cups (360 g) bread flour";
        line.IngredientNameText = "bread flour";
        line.Quantity = 360m;
        line.QuantityUpper = 380m;
        line.MeasurementUnitId = Guid.NewGuid();
        line.MeasurementUnitDimension = MeasurementDimension.Volume;
        line.IngredientId = Guid.NewGuid();
        line.MatchStatus = IngredientMatchStatus.Ambiguous;
        line.PreparationNote = "scooped and levelled";
        line.IsOptional = false;
        line.ScalingBehavior = IngredientScaling.ReviewRequired;

        var instructionGroup = recipe.InstructionGroups.Single();
        instructionGroup.Title = "Assemble";

        var step = instructionGroup.Steps.Single();
        step.Text = "Bake until a skewer comes out clean.";
        step.TechniqueId = Guid.NewGuid();
        step.DurationMinutes = 35;
        step.TemperatureValue = 175m;
        step.TemperatureUnitId = Guid.NewGuid();
        step.TemperatureUnitDimension = MeasurementDimension.Mass;
        step.Note = "A different note.";

        var equipment = recipe.Equipment.Single();
        equipment.DisplayText = "8-inch square tin";
        equipment.EquipmentTypeId = Guid.NewGuid();
        equipment.IsOptional = false;
        equipment.Note = "A different equipment note.";

        var asset = recipe.AssetLinks.Single();
        asset.MediaAssetId = Guid.NewGuid();
        asset.Role = RecipeAssetRole.Process;
        asset.Caption = "A different caption.";

        // Swapped rather than edited: a tag carries nothing but its id, so the only change it can express is
        // arriving or leaving, and that is what exercises TagWorkspaceTagId.
        recipe.Tags.Clear();
        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = Guid.NewGuid() });
    }
}
