using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Computes a deterministic recipe scaling preview by multiplier or target yield (ING-003, CALC-001/002/005/006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Arithmetic only, never a decision.</strong> Whether a line scales at all is read from
/// <see cref="RecipeIngredientScalingInput.ScalingBehavior"/> — recorded when the line was authored, not
/// inferred here. This calculator's own judgment is limited to the two things that are genuinely properties of
/// the arithmetic: whether a rounded result reads as zero when it is not, and whether the factor itself is far
/// enough from 1 that linear scaling assumptions (leavening, pan size, cook time) are worth a second look.
/// </para>
/// <para>
/// <strong>Exact, then rounded once, at the edge.</strong> Every quantity is read as a <see cref="Quantity"/>
/// (an exact fraction — recipes.md forbids binary floating point here) and multiplied exactly; a decimal is
/// derived from that exact value for display only, and only in this one place, so rounding never compounds
/// across repeated scaling.
/// </para>
/// <para>
/// Pure and stateless, like <see cref="IngredientLineTokenizer"/>: no persistence, no clock, no workspace, and
/// nothing here mutates or saves a recipe. A caller resolves units and lines into
/// <see cref="RecipeIngredientScalingInput"/> and decides what to do with the resulting
/// <see cref="RecipeScalingPreview"/>.
/// </para>
/// </remarks>
public static class RecipeScalingCalculator
{
    /// <summary>
    /// Scales every line by the request's factor, or by the factor derived from its target yield.
    /// </summary>
    /// <param name="request">A multiplier or a target yield — never both (see <see cref="RecipeScalingRequest"/>).</param>
    /// <param name="recipeYieldQuantity">
    /// The recipe's own <c>YieldQuantity</c>, read as a <see cref="Quantity"/>, or <see langword="null"/> when
    /// the recipe's yield is text-only. Required to resolve a target-yield request; optional for a direct
    /// multiplier, where it is only used to show a scaled yield in the preview.
    /// </param>
    /// <param name="lines">The recipe's ingredient lines, in any order; the preview preserves it.</param>
    public static RecipeScalingOutcome Scale(
        RecipeScalingRequest request,
        Quantity? recipeYieldQuantity,
        IReadOnlyList<RecipeIngredientScalingInput> lines)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lines);

        var (factor, requestError) = ResolveFactor(request, recipeYieldQuantity);

        if (requestError is { } error)
            return RecipeScalingOutcome.Failure(error);

        var appliedFactor = factor!.Value;
        var recipeWarnings = new List<RecipeScalingWarningCode>();

        if (appliedFactor >= RecipeScalingPolicy.ExtremeFactorHigh || appliedFactor <= RecipeScalingPolicy.ExtremeFactorLow)
            recipeWarnings.Add(RecipeScalingWarningCode.ExtremeScaleFactor);

        Quantity? scaledYield = null;
        decimal? scaledYieldDisplay = null;

        if (recipeYieldQuantity is { } yield)
        {
            scaledYield = yield.Multiply(appliedFactor);
            scaledYieldDisplay = scaledYield.Value.ToDecimal(RecipeScalingPolicy.DefaultDisplayPrecision);
        }
        else
        {
            recipeWarnings.Add(RecipeScalingWarningCode.RecipeYieldNotStructured);
        }

        var preview = new RecipeScalingPreview
        {
            Factor = appliedFactor,
            RequestedTargetYieldQuantity = request.TargetYieldQuantity,
            ScaledYieldQuantity = scaledYield,
            ScaledYieldDisplayQuantity = scaledYieldDisplay,
            Lines = lines.Select(line => ScaleLine(line, appliedFactor)).ToList(),
            RecipeWarnings = recipeWarnings,
        };

        return RecipeScalingOutcome.Success(preview);
    }

    private static (Quantity? Factor, RecipeScalingRequestError? Error) ResolveFactor(
        RecipeScalingRequest request, Quantity? recipeYieldQuantity)
    {
        if (request.Multiplier is { } multiplier)
        {
            if (multiplier.IsZero || multiplier.IsNegative)
                return (null, RecipeScalingRequestError.NonPositiveMultiplier);

            return WithinRange(multiplier) ? (multiplier, null) : (null, RecipeScalingRequestError.FactorOutOfRange);
        }

        var target = request.TargetYieldQuantity!.Value;

        if (target.IsZero || target.IsNegative)
            return (null, RecipeScalingRequestError.NonPositiveTargetYield);

        if (recipeYieldQuantity is not { } yield || yield.IsZero || yield.IsNegative)
            return (null, RecipeScalingRequestError.RecipeYieldNotStructured);

        // target / yield, computed as a fraction rather than through a Quantity.Divide this type does not have.
        var derivedFactor = Quantity.FromFraction(target.Numerator * yield.Denominator, target.Denominator * yield.Numerator);

        // Checked on the derived factor too, and not only on a submitted multiplier: a target of 1,000,000
        // against a recipe yield of 0.000000000001 is two individually legal numbers whose ratio is not.
        return WithinRange(derivedFactor) ? (derivedFactor, null) : (null, RecipeScalingRequestError.FactorOutOfRange);
    }

    /// <summary>
    /// Whether a factor is one every downstream <c>ToDecimal</c> can represent. Beyond this the scaled value
    /// overflows <c>decimal</c> and the calculator would throw rather than answer.
    /// </summary>
    private static bool WithinRange(Quantity factor) =>
        factor.CompareTo(RecipeScalingPolicy.MaxFactor) <= 0
        && factor.CompareTo(Quantity.FromFraction(RecipeScalingPolicy.MaxFactor.Denominator, RecipeScalingPolicy.MaxFactor.Numerator)) >= 0;

    private static RecipeIngredientScalingPreviewLine ScaleLine(RecipeIngredientScalingInput line, Quantity factor) =>
        line.ScalingBehavior switch
        {
            IngredientScaling.Fixed => NotScaled(line, RecipeScalingWarningCode.FixedQuantityNotScaled),
            IngredientScaling.ReviewRequired => NotScaled(line, ReviewRequiredWarning(line)),
            _ => ScaleProportional(line, factor),
        };

    private static RecipeScalingWarningCode ReviewRequiredWarning(RecipeIngredientScalingInput line)
    {
        if (line.Quantity is null)
            return RecipeScalingWarningCode.ReviewRequiredQualitative;

        if (line.QuantityUpper is not null)
            return RecipeScalingWarningCode.ReviewRequiredRange;

        return line.MeasurementUnitDimension == MeasurementDimension.Count
            ? RecipeScalingWarningCode.ReviewRequiredDiscreteCount
            : RecipeScalingWarningCode.ReviewRequiredOther;
    }

    private static RecipeIngredientScalingPreviewLine ScaleProportional(RecipeIngredientScalingInput line, Quantity factor)
    {
        if (line.Quantity is not { } quantity)
            return NotScaled(line, RecipeScalingWarningCode.NoQuantityToScale);

        Quantity scaledLower;
        Quantity? scaledUpper;

        if (line.QuantityUpper is { } upper)
        {
            var scaledRange = QuantityRange.Create(Quantity.FromDecimal(quantity), Quantity.FromDecimal(upper)).Multiply(factor);
            scaledLower = scaledRange.Lower;
            scaledUpper = scaledRange.Upper;
        }
        else
        {
            scaledLower = Quantity.FromDecimal(quantity).Multiply(factor);
            scaledUpper = null;
        }

        var precision = line.DisplayPrecision ?? RecipeScalingPolicy.DefaultDisplayPrecision;
        var displayLower = scaledLower.ToDecimal(precision);
        var displayUpper = scaledUpper?.ToDecimal(precision);

        var warnings = new List<RecipeScalingWarningCode>();

        if (IsNegligibleDisplay(scaledLower, displayLower) || (scaledUpper is { } upperValue && IsNegligibleDisplay(upperValue, displayUpper)))
            warnings.Add(RecipeScalingWarningCode.ScaledToNegligibleDisplay);

        return new RecipeIngredientScalingPreviewLine
        {
            Id = line.Id,
            DisplayText = line.DisplayText,
            WasScaled = true,
            ScaledQuantity = scaledLower,
            ScaledQuantityUpper = scaledUpper,
            ScaledDisplayQuantity = displayLower,
            ScaledDisplayQuantityUpper = displayUpper,
            Warnings = warnings,
        };
    }

    private static bool IsNegligibleDisplay(Quantity exact, decimal? display) => display == 0m && !exact.IsZero;

    private static RecipeIngredientScalingPreviewLine NotScaled(RecipeIngredientScalingInput line, RecipeScalingWarningCode warning) =>
        new()
        {
            Id = line.Id,
            DisplayText = line.DisplayText,
            WasScaled = false,
            ScaledQuantity = line.Quantity is { } quantity ? Quantity.FromDecimal(quantity) : null,
            ScaledQuantityUpper = line.QuantityUpper is { } upper ? Quantity.FromDecimal(upper) : null,
            ScaledDisplayQuantity = line.Quantity,
            ScaledDisplayQuantityUpper = line.QuantityUpper,
            Warnings = [warning],
        };
}
