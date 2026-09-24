using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CsCheck;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Complete, partial, contradictory, and unsupported-input tests for <see cref="YieldReconciliationCalculator"/>.
/// </summary>
public sealed class YieldReconciliationCalculatorTests
{
    private static readonly Gen<decimal> PositiveCentsValue = Gen.Int[1, 100_000].Select(i => i / 100m);

    private static YieldReconciliationInput Input(
        decimal? batchYield = null,
        decimal? servingCount = null,
        decimal? servingSize = null,
        decimal? panVolume = null,
        MeasurementDimension dimension = MeasurementDimension.Volume,
        int displayPrecision = 2) =>
        new(dimension, displayPrecision, batchYield, servingCount, servingSize, panVolume);

    private static YieldReconciliationPreview ReconcileOrThrow(YieldReconciliationInput input)
    {
        var outcome = YieldReconciliationCalculator.Reconcile(input);

        Assert.True(outcome.Succeeded);
        return outcome.Preview!;
    }

    // ---- Complete: reconciled ----

    [Fact]
    public void Three_consistent_values_are_reconciled_without_changing_anything()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 12m, servingCount: 24m, servingSize: 0.5m));

        Assert.Equal(YieldReconciliationStatus.Reconciled, preview.Status);
        Assert.Equal(12m, preview.BatchYield);
        Assert.Equal(24m, preview.ServingCount);
        Assert.Equal(0.5m, preview.ServingSize);
        Assert.Null(preview.SolvedField);
    }

    [Fact]
    public void Three_values_that_agree_only_after_rounding_are_reconciled()
    {
        // 3 servings × (1/3) each = 1 exactly in theory; entered as 0.33 it rounds to the stated 1 at precision 0.
        var preview = ReconcileOrThrow(Input(batchYield: 1m, servingCount: 3m, servingSize: 0.33m, displayPrecision: 0));

        Assert.Equal(YieldReconciliationStatus.Reconciled, preview.Status);
    }

    // ---- Complete: contradictory ----

    [Fact]
    public void Three_values_that_disagree_beyond_rounding_are_contradictory_and_none_is_overridden()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 10m, servingCount: 24m, servingSize: 0.5m));

        Assert.Equal(YieldReconciliationStatus.Contradictory, preview.Status);
        Assert.Equal(10m, preview.BatchYield);
        Assert.Equal(24m, preview.ServingCount);
        Assert.Equal(0.5m, preview.ServingSize);
        Assert.Null(preview.SolvedField);
    }

    // ---- Partial: solved ----

    [Fact]
    public void Batch_yield_is_solved_from_serving_count_and_size()
    {
        var preview = ReconcileOrThrow(Input(servingCount: 24m, servingSize: 0.5m));

        Assert.Equal(YieldReconciliationStatus.Solved, preview.Status);
        Assert.Equal(YieldReconciliationField.BatchYield, preview.SolvedField);
        Assert.Equal(12m, preview.BatchYield);
    }

    [Fact]
    public void Serving_size_is_solved_from_batch_yield_and_serving_count()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 12m, servingCount: 24m));

        Assert.Equal(YieldReconciliationStatus.Solved, preview.Status);
        Assert.Equal(YieldReconciliationField.ServingSize, preview.SolvedField);
        Assert.Equal(0.5m, preview.ServingSize);
    }

    [Fact]
    public void Serving_count_is_solved_from_batch_yield_and_serving_size()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 12m, servingSize: 0.5m));

        Assert.Equal(YieldReconciliationStatus.Solved, preview.Status);
        Assert.Equal(YieldReconciliationField.ServingCount, preview.SolvedField);
        Assert.Equal(24m, preview.ServingCount);
    }

    [Fact]
    public void Solving_and_then_checking_reproduces_a_consistent_triple()
    {
        Gen.Select(PositiveCentsValue, PositiveCentsValue).Sample((servingCount, servingSize) =>
        {
            var solved = ReconcileOrThrow(Input(servingCount: servingCount, servingSize: servingSize, displayPrecision: 6));
            var checkedAgain = ReconcileOrThrow(Input(
                batchYield: solved.BatchYield, servingCount: servingCount, servingSize: servingSize, displayPrecision: 6));

            Assert.Equal(YieldReconciliationStatus.Reconciled, checkedAgain.Status);
        });
    }

    // ---- Unsupported input ----

    [Fact]
    public void No_values_at_all_is_insufficient_input()
    {
        var preview = ReconcileOrThrow(Input());

        Assert.Equal(YieldReconciliationStatus.InsufficientInput, preview.Status);
        Assert.Null(preview.Formula);
        Assert.Null(preview.SolvedField);
    }

    [Fact]
    public void Exactly_one_value_is_insufficient_input()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 12m));

        Assert.Equal(YieldReconciliationStatus.InsufficientInput, preview.Status);
        Assert.Equal(12m, preview.BatchYield);
        Assert.Null(preview.Formula);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_value_is_rejected_outright(int nonPositive)
    {
        var outcome = YieldReconciliationCalculator.Reconcile(Input(batchYield: nonPositive));

        Assert.False(outcome.Succeeded);
        Assert.Equal(YieldReconciliationError.NonPositiveInput, outcome.Error);
        Assert.Null(outcome.Preview);
    }

    // ---- Pan/vessel comparison ----

    [Fact]
    public void A_known_batch_yield_and_pan_volume_produce_an_exact_ratio()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 1200m, servingCount: 12m, servingSize: 100m, panVolume: 1000m));

        Assert.Equal(true, preview.PanComparable);
        Assert.Equal(Quantity.FromFraction(6, 5), preview.PanFillRatio);
    }

    [Fact]
    public void No_pan_volume_given_leaves_the_comparison_not_applicable()
    {
        var preview = ReconcileOrThrow(Input(batchYield: 12m, servingCount: 24m, servingSize: 0.5m));

        Assert.Null(preview.PanComparable);
        Assert.Null(preview.PanFillRatio);
    }

    [Fact]
    public void A_pan_volume_against_a_non_volume_dimension_is_not_comparable()
    {
        var preview = ReconcileOrThrow(Input(
            batchYield: 24m, servingCount: 24m, servingSize: 1m, panVolume: 1000m, dimension: MeasurementDimension.Count));

        Assert.Equal(false, preview.PanComparable);
        Assert.Null(preview.PanFillRatio);
    }

    [Fact]
    public void A_pan_volume_with_no_known_batch_yield_is_not_comparable()
    {
        var preview = ReconcileOrThrow(Input(panVolume: 1000m));

        Assert.Equal(YieldReconciliationStatus.InsufficientInput, preview.Status);
        Assert.Equal(false, preview.PanComparable);
        Assert.Null(preview.PanFillRatio);
    }

    [Fact]
    public void A_pan_volume_compares_against_a_freshly_solved_batch_yield()
    {
        var preview = ReconcileOrThrow(Input(servingCount: 12m, servingSize: 100m, panVolume: 1000m));

        Assert.Equal(YieldReconciliationStatus.Solved, preview.Status);
        Assert.Equal(true, preview.PanComparable);
        Assert.Equal(Quantity.FromFraction(6, 5), preview.PanFillRatio);
    }
}
