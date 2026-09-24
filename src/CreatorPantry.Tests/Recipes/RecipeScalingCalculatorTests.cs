using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CsCheck;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Property tests and a checked-in fixture set for <see cref="RecipeScalingCalculator"/>: qualitative,
/// discrete/package, range, and extreme-factor lines, plus the request-level validation that stands between a
/// creator's input and a computed preview.
/// </summary>
public sealed class RecipeScalingCalculatorTests
{
    private static readonly Gen<Quantity> SmallPositiveFactor =
        Gen.Int[1, 2000].Select(i => Quantity.FromFraction(i, 100));

    private static readonly Gen<Quantity> NonPositiveQuantity =
        Gen.Int[-1000, 0].Select(i => Quantity.FromFraction(i, 100));

    /// <summary>Two-decimal-place values, so a factor-of-one round trip matches at the default display precision exactly.</summary>
    private static readonly Gen<decimal> CentsScaleQuantity =
        Gen.Int[1, 100_000].Select(i => i / 100m);

    private static RecipeIngredientScalingInput Line(
        decimal? quantity,
        decimal? quantityUpper = null,
        IngredientScaling scalingBehavior = IngredientScaling.Proportional,
        MeasurementDimension? dimension = null,
        int? displayPrecision = RecipeScalingPolicy.DefaultDisplayPrecision,
        string displayText = "line") =>
        new()
        {
            Id = Guid.NewGuid(),
            DisplayText = displayText,
            Quantity = quantity,
            QuantityUpper = quantityUpper,
            ScalingBehavior = scalingBehavior,
            MeasurementUnitDimension = dimension,
            DisplayPrecision = displayPrecision,
        };

    private static RecipeScalingPreview ScaleOrThrow(
        RecipeScalingRequest request, Quantity? recipeYieldQuantity, params RecipeIngredientScalingInput[] lines)
    {
        var outcome = RecipeScalingCalculator.Scale(request, recipeYieldQuantity, lines);

        Assert.True(outcome.Succeeded);
        return outcome.Preview!;
    }

    // ---- Identity and non-interference ----

    [Fact]
    public void Scaling_by_one_is_identity_for_a_proportional_line()
    {
        CentsScaleQuantity.Sample(quantity =>
        {
            var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(1)), null, Line(quantity));
            var line = preview.Lines.Single();

            Assert.Equal(Quantity.FromDecimal(quantity), line.ScaledQuantity);
            Assert.Equal(quantity, line.ScaledDisplayQuantity);
            Assert.Empty(line.Warnings);
        });
    }

    [Fact]
    public void A_fixed_line_never_changes_under_any_factor()
    {
        Gen.Select(SmallPositiveFactor, CentsScaleQuantity).Sample((factor, quantity) =>
        {
            var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(factor), null, Line(quantity, scalingBehavior: IngredientScaling.Fixed));
            var line = preview.Lines.Single();

            Assert.False(line.WasScaled);
            Assert.Equal(quantity, line.ScaledDisplayQuantity);
            Assert.Equal(RecipeScalingWarningCode.FixedQuantityNotScaled, Assert.Single(line.Warnings));
        });
    }

    [Fact]
    public void A_review_required_line_never_changes_under_any_factor()
    {
        Gen.Select(SmallPositiveFactor, CentsScaleQuantity).Sample((factor, quantity) =>
        {
            var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(factor), null, Line(quantity, scalingBehavior: IngredientScaling.ReviewRequired));
            var line = preview.Lines.Single();

            Assert.False(line.WasScaled);
            Assert.Equal(quantity, line.ScaledDisplayQuantity);
        });
    }

    // ---- Range ----

    [Fact]
    public void A_proportional_range_scales_both_bounds_exactly_and_keeps_lower_below_upper()
    {
        Gen.Select(SmallPositiveFactor, CentsScaleQuantity, Gen.Int[1, 10_000].Select(i => i / 100m))
            .Sample((factor, lower, gap) =>
            {
                var upper = lower + gap;
                var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(factor), null, Line(lower, upper));
                var line = preview.Lines.Single();

                Assert.True(line.WasScaled);
                Assert.Equal(Quantity.FromDecimal(lower).Multiply(factor), line.ScaledQuantity);
                Assert.Equal(Quantity.FromDecimal(upper).Multiply(factor), line.ScaledQuantityUpper);
                Assert.True(line.ScaledQuantity < line.ScaledQuantityUpper);
            });
    }

    [Fact]
    public void A_review_required_range_is_flagged_and_left_unchanged()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(2)), null,
            Line(2m, 3m, scalingBehavior: IngredientScaling.ReviewRequired));
        var line = preview.Lines.Single();

        Assert.False(line.WasScaled);
        Assert.Equal(2m, line.ScaledDisplayQuantity);
        Assert.Equal(3m, line.ScaledDisplayQuantityUpper);
        Assert.Equal(RecipeScalingWarningCode.ReviewRequiredRange, Assert.Single(line.Warnings));
    }

    // ---- Qualitative, discrete, and package fixtures ----

    [Fact]
    public void A_qualitative_line_with_no_quantity_is_flagged_and_left_unchanged()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(3)), null,
            Line(null, scalingBehavior: IngredientScaling.ReviewRequired, displayText: "salt, to taste"));
        var line = preview.Lines.Single();

        Assert.False(line.WasScaled);
        Assert.Null(line.ScaledQuantity);
        Assert.Equal(RecipeScalingWarningCode.ReviewRequiredQualitative, Assert.Single(line.Warnings));
    }

    [Fact]
    public void A_discrete_count_line_is_flagged_and_left_unchanged()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromFraction(1, 2)), null,
            Line(2m, scalingBehavior: IngredientScaling.ReviewRequired, dimension: MeasurementDimension.Count, displayText: "2 eggs"));
        var line = preview.Lines.Single();

        Assert.False(line.WasScaled);
        Assert.Equal(2m, line.ScaledDisplayQuantity);
        Assert.Equal(RecipeScalingWarningCode.ReviewRequiredDiscreteCount, Assert.Single(line.Warnings));
    }

    [Fact]
    public void A_package_style_line_is_flagged_as_discrete_count_because_it_is_authored_as_one()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(2)), null,
            Line(1m, scalingBehavior: IngredientScaling.ReviewRequired, dimension: MeasurementDimension.Count,
                displayText: "1 (14.5 oz) can diced tomatoes"));
        var line = preview.Lines.Single();

        Assert.False(line.WasScaled);
        Assert.Equal(1m, line.ScaledDisplayQuantity);
        Assert.Equal(RecipeScalingWarningCode.ReviewRequiredDiscreteCount, Assert.Single(line.Warnings));
    }

    [Fact]
    public void A_review_required_line_with_a_numeric_non_count_quantity_falls_back_to_other()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(2)), null,
            Line(9m, scalingBehavior: IngredientScaling.ReviewRequired, dimension: MeasurementDimension.Volume,
                displayText: "9-inch pan's worth of butter, for greasing"));
        var line = preview.Lines.Single();

        Assert.Equal(RecipeScalingWarningCode.ReviewRequiredOther, Assert.Single(line.Warnings));
    }

    // ---- Defensive: a line with nothing to scale ----

    [Fact]
    public void A_proportional_line_with_no_quantity_is_flagged_rather_than_treated_as_zero()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(2)), null, Line(null));
        var line = preview.Lines.Single();

        Assert.False(line.WasScaled);
        Assert.Null(line.ScaledQuantity);
        Assert.Equal(RecipeScalingWarningCode.NoQuantityToScale, Assert.Single(line.Warnings));
    }

    // ---- Rounding ----

    [Fact]
    public void A_nonzero_result_that_rounds_to_zero_is_flagged_but_still_computed_exactly()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromFraction(1, 50)), null, Line(0.01m));
        var line = preview.Lines.Single();

        Assert.True(line.WasScaled);
        Assert.False(line.ScaledQuantity!.Value.IsZero);
        Assert.Equal(0m, line.ScaledDisplayQuantity);
        Assert.Equal(RecipeScalingWarningCode.ScaledToNegligibleDisplay, Assert.Single(line.Warnings));
    }

    // ---- Extreme factors ----

    [Fact]
    public void A_factor_at_or_above_the_high_extreme_threshold_flags_the_preview()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(20)), null, Line(1m));

        Assert.Contains(RecipeScalingWarningCode.ExtremeScaleFactor, preview.RecipeWarnings);
    }

    [Fact]
    public void A_factor_at_or_below_the_low_extreme_threshold_flags_the_preview()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromFraction(1, 20)), null, Line(1m));

        Assert.Contains(RecipeScalingWarningCode.ExtremeScaleFactor, preview.RecipeWarnings);
    }

    [Fact]
    public void An_ordinary_doubling_or_halving_does_not_flag_the_preview()
    {
        var doubled = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(2)), null, Line(1m));
        var halved = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromFraction(1, 2)), null, Line(1m));

        Assert.DoesNotContain(RecipeScalingWarningCode.ExtremeScaleFactor, doubled.RecipeWarnings);
        Assert.DoesNotContain(RecipeScalingWarningCode.ExtremeScaleFactor, halved.RecipeWarnings);
    }

    // ---- Recipe yield ----

    [Fact]
    public void Scaling_without_a_structured_recipe_yield_still_scales_lines_but_flags_the_yield()
    {
        var preview = ScaleOrThrow(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(2)), null, Line(1m));

        Assert.Null(preview.ScaledYieldQuantity);
        Assert.Contains(RecipeScalingWarningCode.RecipeYieldNotStructured, preview.RecipeWarnings);
        Assert.True(preview.Lines.Single().WasScaled);
    }

    [Fact]
    public void A_target_yield_request_resolves_a_factor_that_reproduces_the_target_exactly()
    {
        Gen.Select(Gen.Int[1, 1000], Gen.Int[1, 1000]).Sample((yieldValue, targetValue) =>
        {
            var yield = Quantity.FromInt(yieldValue);
            var target = Quantity.FromInt(targetValue);

            var preview = ScaleOrThrow(RecipeScalingRequest.ForTargetYield(target), yield, Line(1m));

            Assert.Equal(target, preview.Factor.Multiply(yield));
            Assert.Equal(target, preview.ScaledYieldQuantity);
        });
    }

    [Fact]
    public void A_target_yield_request_without_a_structured_recipe_yield_fails()
    {
        var outcome = RecipeScalingCalculator.Scale(RecipeScalingRequest.ForTargetYield(Quantity.FromInt(6)), null, [Line(1m)]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecipeScalingRequestError.RecipeYieldNotStructured, outcome.Error);
    }

    [Fact]
    public void A_target_yield_request_against_a_zero_recipe_yield_fails()
    {
        var outcome = RecipeScalingCalculator.Scale(RecipeScalingRequest.ForTargetYield(Quantity.FromInt(6)), Quantity.Zero, [Line(1m)]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecipeScalingRequestError.RecipeYieldNotStructured, outcome.Error);
    }

    // ---- Request validation ----

    [Fact]
    public void A_nonpositive_multiplier_is_always_rejected()
    {
        NonPositiveQuantity.Sample(multiplier =>
        {
            var outcome = RecipeScalingCalculator.Scale(RecipeScalingRequest.ForMultiplier(multiplier), null, [Line(1m)]);

            Assert.False(outcome.Succeeded);
            Assert.Equal(RecipeScalingRequestError.NonPositiveMultiplier, outcome.Error);
            Assert.Null(outcome.Preview);
        });
    }

    [Fact]
    public void A_nonpositive_target_yield_is_always_rejected()
    {
        NonPositiveQuantity.Sample(target =>
        {
            var outcome = RecipeScalingCalculator.Scale(RecipeScalingRequest.ForTargetYield(target), Quantity.FromInt(4), [Line(1m)]);

            Assert.False(outcome.Succeeded);
            Assert.Equal(RecipeScalingRequestError.NonPositiveTargetYield, outcome.Error);
            Assert.Null(outcome.Preview);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_or_negative_multiplier_is_rejected(int multiplier)
    {
        var outcome = RecipeScalingCalculator.Scale(RecipeScalingRequest.ForMultiplier(Quantity.FromInt(multiplier)), null, [Line(1m)]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecipeScalingRequestError.NonPositiveMultiplier, outcome.Error);
    }
}
