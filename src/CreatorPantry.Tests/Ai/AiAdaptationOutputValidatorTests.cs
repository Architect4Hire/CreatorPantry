using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-005's own two checks over <see cref="AiOutputValidator"/>'s shared five stages: deterministic yield
/// math, and an honest limitation in place of a fabricated success. No database, no provider — a raw payload
/// in, a verdict out.
/// </summary>
public sealed class AiAdaptationOutputValidatorTests
{
    private const string SchemaVersion = "fixture.adaptation.v1";
    private static readonly Guid Line1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Line2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ---- the base validator's stages still run ---------------------------------------------------------

    [Fact]
    public void Malformed_json_is_rejected_before_either_new_check_runs()
    {
        var result = AiAdaptationOutputValidator.Validate(
            "not json", SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.MalformedJson, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_change_outside_the_whole_recipe_scope_is_rejected_by_the_shared_stage()
    {
        // AiChangeTargetKind.RecipeConcept is not applicable to any operation scope.
        var payload = Document(changes: """
            {"changeKind":"Set","targetKind":"RecipeConcept","fieldName":"title","afterValue":"x"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.OutsideScope, result.Failure!.ReasonCode);
    }

    // ---- deterministic yield math -----------------------------------------------------------------------

    [Fact]
    public void A_yield_answer_copying_the_deterministic_figure_is_accepted()
    {
        var preview = Scale(2m, Line(Line1, 100m));
        var afterValue = preview.Lines.Single().ScaledQuantity!.Value
            .ToDecimal(AiAdaptationOutputValidator.QuantityComparisonScale);

        var payload = Document(changes: $$"""
            {"changeKind":"Set","targetKind":"Ingredient","targetId":"{{Line1}}","fieldName":"quantity","afterValue":"{{afterValue}}"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Yield, preview);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    [Fact]
    public void A_yield_answer_inventing_its_own_number_is_rejected_and_not_correctable()
    {
        var preview = Scale(2m, Line(Line1, 100m));

        // The correct doubled figure is 200; this invents a different one.
        var payload = Document(changes: $$"""
            {"changeKind":"Set","targetKind":"Ingredient","targetId":"{{Line1}}","fieldName":"quantity","afterValue":"199"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Yield, preview);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.YieldMathNotDeterministic, result.Failure!.ReasonCode);
        Assert.False(result.Failure.IsCorrectableByReprompt);
    }

    /// <summary>A line with nothing to scale has no deterministic figure at all, so any proposed number is invented.</summary>
    [Fact]
    public void A_quantity_proposed_for_a_line_with_no_deterministic_figure_is_rejected()
    {
        var preview = Scale(2m, Line(Line1, quantity: null));

        var payload = Document(changes: $$"""
            {"changeKind":"Set","targetKind":"Ingredient","targetId":"{{Line1}}","fieldName":"quantity","afterValue":"12"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Yield, preview);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.YieldMathNotDeterministic, result.Failure!.ReasonCode);
    }

    /// <summary>A fixed line's deterministic figure is its unchanged original — proposing a scaled number for it is still invented.</summary>
    [Fact]
    public void A_fixed_line_rescaled_by_the_model_is_rejected()
    {
        var preview = Scale(2m, Line(Line1, 100m, IngredientScaling.Fixed));

        var payload = Document(changes: $$"""
            {"changeKind":"Set","targetKind":"Ingredient","targetId":"{{Line1}}","fieldName":"quantity","afterValue":"200"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Yield, preview);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.YieldMathNotDeterministic, result.Failure!.ReasonCode);
    }

    /// <summary>A change to a line the deterministic preview does not name is not this stage's concern — AiDiffCalculator catches it.</summary>
    [Fact]
    public void A_quantity_change_on_an_unknown_line_is_not_rejected_by_this_stage()
    {
        var preview = Scale(2m, Line(Line1, 100m));

        var payload = Document(changes: $$"""
            {"changeKind":"Set","targetKind":"Ingredient","targetId":"{{Line2}}","fieldName":"quantity","afterValue":"anything"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Yield, preview);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    /// <summary>Not a yield goal: no deterministic figures exist to check against, so nothing here is enforced.</summary>
    [Fact]
    public void A_non_yield_goal_never_runs_the_determinism_check()
    {
        var payload = Document(changes: $$"""
            {"changeKind":"Set","targetKind":"Ingredient","targetId":"{{Line1}}","fieldName":"quantity","afterValue":"anything"}
            """);

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    // ---- an honest limitation in place of a fabricated success ------------------------------------------

    [Fact]
    public void Empty_changes_with_a_whole_answer_limitation_is_accepted()
    {
        var payload = Document(warnings: """{"kind":"Limitation","message":"Cannot be made nut-free without losing the intended texture."}""");

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    [Fact]
    public void Empty_changes_with_no_warning_at_all_is_rejected()
    {
        var payload = Document();

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.AdaptationAnswerUnexplained, result.Failure!.ReasonCode);
        Assert.True(result.Failure.IsCorrectableByReprompt);
    }

    /// <summary>A caution is not a limitation. The two mean different things and one cannot stand in for the other.</summary>
    [Fact]
    public void Empty_changes_with_only_a_caution_is_rejected()
    {
        var payload = Document(warnings: """{"kind":"CulinaryCaution","message":"Worth a second look."}""");

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.AdaptationAnswerUnexplained, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// A warning cannot address a change index when there are no changes to index — the shared stage rejects
    /// that before this one's own check ever runs, which is what stops a limitation "explaining" an empty
    /// answer by pointing at a change that does not exist.
    /// </summary>
    [Fact]
    public void A_limitation_pointing_at_a_change_index_in_an_empty_answer_is_rejected_by_the_shared_stage()
    {
        var payload = Document(warnings: """{"kind":"Limitation","message":"x","changeIndex":0}""");

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.WarningIndexInvalid, result.Failure!.ReasonCode);
    }

    [Fact]
    public void Non_empty_changes_need_no_limitation()
    {
        var payload = Document(changes: """{"changeKind":"Set","targetKind":"Recipe","fieldName":"title","afterValue":"New title"}""");

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    [Fact]
    public void More_than_the_warning_cap_is_rejected()
    {
        var warnings = string.Join(
            ",", Enumerable.Range(0, AiPolicy.MaxAdaptationWarnings + 1)
                .Select(_ => """{"kind":"Assumption","message":"x"}"""));

        var payload = $$"""{"schemaVersion":"{{SchemaVersion}}","changes":[],"warnings":[{{warnings}}]}""";

        var result = AiAdaptationOutputValidator.Validate(
            payload, SchemaVersion, AiOperationScope.WholeRecipe, AiAdaptationGoal.Dietary, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.ValueTooLong, result.Failure!.ReasonCode);
        Assert.True(result.Failure.IsCorrectableByReprompt);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string Document(string? changes = null, string? warnings = null) => $$"""
        {"schemaVersion":"{{SchemaVersion}}","changes":[{{changes}}],"warnings":[{{warnings}}]}
        """;

    private static RecipeIngredientScalingInput Line(
        Guid id,
        decimal? quantity,
        IngredientScaling scalingBehavior = IngredientScaling.Proportional) =>
        new()
        {
            Id = id,
            DisplayText = "a line",
            Quantity = quantity,
            QuantityUpper = null,
            ScalingBehavior = scalingBehavior,
            MeasurementUnitDimension = null,
            DisplayPrecision = RecipeScalingPolicy.DefaultDisplayPrecision,
        };

    private static RecipeScalingPreview Scale(decimal multiplier, params RecipeIngredientScalingInput[] lines)
    {
        var outcome = RecipeScalingCalculator.Scale(
            RecipeScalingRequest.ForMultiplier(Quantity.FromDecimal(multiplier)), null, lines);

        Assert.True(outcome.Succeeded);
        return outcome.Preview!;
    }
}
