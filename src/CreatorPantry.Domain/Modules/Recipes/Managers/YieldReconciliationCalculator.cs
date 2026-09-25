using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Reconciles a recipe's batch yield, serving count, and serving size — solving for a missing one, confirming
/// or flagging a stated one, and comparing against a pan/vessel capacity when given (ING-005, CALC-002/005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two known values, never zero or one.</strong> <c>batchYield = servingCount × servingSize</c> has
/// three variables; any two determine the third, but recipes.md's "do not invent a missing serving
/// definition" means this calculator will not guess at a value with fewer than two given, and will not
/// silently prefer one value over another when all three disagree.
/// </para>
/// <para>
/// <strong>Pan capacity is a comparison, never a source of geometry.</strong> A stated pan or vessel capacity
/// is divided against a known batch yield as a plain, exact ratio — nothing here assumes a fill percentage,
/// derives a capacity from a pan's dimensions, or treats the comparison as a verdict. A caller supplies the
/// capacity outright, or this calculator does not touch pan/vessel at all.
/// </para>
/// <para>
/// Pure and stateless, like <see cref="RecipeScalingCalculator"/> (7.6): no persistence, no unit conversion of
/// its own (a caller brings every value to one unit first — see
/// <see cref="CreatorPantry.Domain.Modules.Measurement.Managers.UnitConversionCalculator"/>, 7.7), and nothing
/// here mutates or saves a recipe.
/// </para>
/// </remarks>
public static class YieldReconciliationCalculator
{
    public static YieldReconciliationOutcome Reconcile(YieldReconciliationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (IsNonPositive(input.BatchYield) || IsNonPositive(input.ServingCount) || IsNonPositive(input.ServingSize) || IsNonPositive(input.PanVolume))
            return YieldReconciliationOutcome.Failure(YieldReconciliationError.NonPositiveInput);

        return CountKnown(input) switch
        {
            3 => ReconcileComplete(input),
            2 => ReconcilePartial(input),
            _ => YieldReconciliationOutcome.Success(BuildPreview(input, YieldReconciliationStatus.InsufficientInput, solvedField: null, formula: null)),
        };
    }

    private static bool IsNonPositive(decimal? value) => value is { } v && v <= 0m;

    private static int CountKnown(YieldReconciliationInput input) =>
        (input.BatchYield is not null ? 1 : 0) + (input.ServingCount is not null ? 1 : 0) + (input.ServingSize is not null ? 1 : 0);

    private static YieldReconciliationOutcome ReconcileComplete(YieldReconciliationInput input)
    {
        var computed = Quantity.FromDecimal(input.ServingCount!.Value).Multiply(Quantity.FromDecimal(input.ServingSize!.Value));
        var roundedComputed = computed.ToDecimal(input.DisplayPrecision);
        var roundedGiven = Quantity.FromDecimal(input.BatchYield!.Value).ToDecimal(input.DisplayPrecision);

        var reconciled = roundedComputed == roundedGiven;
        var status = reconciled ? YieldReconciliationStatus.Reconciled : YieldReconciliationStatus.Contradictory;
        var formula = reconciled
            ? "servingCount × servingSize = batchYield"
            : "servingCount × servingSize ≠ batchYield as given";

        return YieldReconciliationOutcome.Success(BuildPreview(input, status, solvedField: null, formula));
    }

    private static YieldReconciliationOutcome ReconcilePartial(YieldReconciliationInput input)
    {
        if (input.BatchYield is null)
        {
            var solved = Quantity.FromDecimal(input.ServingCount!.Value).Multiply(Quantity.FromDecimal(input.ServingSize!.Value));
            var resolved = input with { BatchYield = solved.ToDecimal(input.DisplayPrecision) };

            // The exact solved value is carried alongside the rounded echo, because the pan ratio is derived
            // from it: rounding first and dividing second is a rounding computed from a rounding, and the
            // ratio is published as exact. `3 × 0.3333` at precision 2 rounds to 1.00 and would report a pan
            // filled exactly to the brim when the true ratio is 9999/10000.
            return YieldReconciliationOutcome.Success(BuildPreview(
                resolved, YieldReconciliationStatus.Solved, YieldReconciliationField.BatchYield, "batchYield = servingCount × servingSize", solved));
        }

        if (input.ServingSize is null)
        {
            var solved = Divide(Quantity.FromDecimal(input.BatchYield.Value), Quantity.FromDecimal(input.ServingCount!.Value));
            var resolved = input with { ServingSize = solved.ToDecimal(input.DisplayPrecision) };

            return YieldReconciliationOutcome.Success(BuildPreview(
                resolved, YieldReconciliationStatus.Solved, YieldReconciliationField.ServingSize, "servingSize = batchYield ÷ servingCount"));
        }

        {
            var solved = Divide(Quantity.FromDecimal(input.BatchYield.Value), Quantity.FromDecimal(input.ServingSize.Value));
            var resolved = input with { ServingCount = solved.ToDecimal(input.DisplayPrecision) };

            return YieldReconciliationOutcome.Success(BuildPreview(
                resolved, YieldReconciliationStatus.Solved, YieldReconciliationField.ServingCount, "servingCount = batchYield ÷ servingSize"));
        }
    }

    /// <param name="exactBatchYield">
    /// The unrounded batch yield, when this call solved for one. The pan ratio is derived from it rather than
    /// from <see cref="YieldReconciliationInput.BatchYield"/>, which by then holds the rounded echo — CALC-002
    /// requires the exact value to be the one every result is computed from, never a previous rounding.
    /// <see langword="null"/> when the batch yield was given rather than solved, where the stated decimal is
    /// itself the exact value.
    /// </param>
    private static YieldReconciliationPreview BuildPreview(
        YieldReconciliationInput input,
        YieldReconciliationStatus status,
        YieldReconciliationField? solvedField,
        string? formula,
        Quantity? exactBatchYield = null)
    {
        var (panFillRatio, panComparable) = ComparePan(input, exactBatchYield);

        return new YieldReconciliationPreview
        {
            Status = status,
            BatchYield = input.BatchYield,
            ServingCount = input.ServingCount,
            ServingSize = input.ServingSize,
            SolvedField = solvedField,
            Formula = formula,
            PanVolume = input.PanVolume,
            PanFillRatio = panFillRatio,
            PanComparable = panComparable,
        };
    }

    private static (Quantity? Ratio, bool? Comparable) ComparePan(YieldReconciliationInput input, Quantity? exactBatchYield)
    {
        if (input.PanVolume is null)
            return (null, null);

        if (input.Dimension is not MeasurementDimension.Volume || input.BatchYield is null)
            return (null, false);

        var batchYield = exactBatchYield ?? Quantity.FromDecimal(input.BatchYield.Value);

        return (Divide(batchYield, Quantity.FromDecimal(input.PanVolume.Value)), true);
    }

    /// <summary>Division for two exact quantities — <see cref="Quantity"/> has no <c>Divide</c> of its own; composed from <see cref="Quantity.Multiply"/> via the reciprocal fraction.</summary>
    private static Quantity Divide(Quantity numerator, Quantity denominator) =>
        Quantity.FromFraction(numerator.Numerator * denominator.Denominator, numerator.Denominator * denominator.Numerator);
}
