using CreatorPantry.Domain.Managers.Quantities;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// Converts an explicit recipe temperature between Celsius and Fahrenheit (CALC-003).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The formula, and nothing before it.</strong> This takes a <c>decimal</c> value a caller already
/// knows is a temperature — <see cref="CreatorPantry.Domain.Modules.Recipes.Data.Entities.RecipeInstructionStep.TemperatureValue"/>
/// or its like — never a step's free-text <c>Text</c> or <c>Note</c>. recipes.md forbids inferring a number
/// from vague heat language ("medium-high"), and there is no code path here that could: a step with no
/// structured value has nothing to hand this calculator, and it is never asked to guess one.
/// </para>
/// <para>
/// <strong>Affine, unlike <see cref="UnitConversionCalculator"/>.</strong> <c>°F = °C × 9/5 + 32</c> is not a
/// factor ratio — it does not pass through zero the way a mass or volume conversion does — so it is its own
/// operation rather than a third branch of that calculator's same-dimension arithmetic
/// (<see cref="CreatorPantry.Domain.Managers.Reference.MeasurementDimension.Temperature"/> units carry no
/// <c>BaseUnitFactor</c> for exactly this reason).
/// </para>
/// <para>
/// Exact throughout — 9/5 and 5/9 are held as fractions, not <see cref="decimal"/> literals — with a
/// <see cref="decimal"/> derived for display only once, at the very end. Pure and stateless, like
/// <see cref="CreatorPantry.Domain.Modules.Recipes.Managers.RecipeScalingCalculator"/> (7.6): no persistence,
/// no clock, no AI.
/// </para>
/// </remarks>
public static class TemperatureConversionCalculator
{
    private static readonly Quantity NineFifths = Quantity.FromFraction(9, 5);
    private static readonly Quantity FiveNinths = Quantity.FromFraction(5, 9);
    private static readonly Quantity ThirtyTwo = Quantity.FromInt(32);

    /// <param name="value">The temperature, in <paramref name="fromScale"/>.</param>
    /// <param name="precision">The step's own display precision. Not chosen here — see <see cref="TemperatureConversionResult.Precision"/>.</param>
    /// <param name="ovenModeContext">Passed through untouched to <see cref="TemperatureConversionResult.OvenModeContext"/>.</param>
    /// <param name="safetyNote">Passed through untouched to <see cref="TemperatureConversionResult.SafetyNote"/>.</param>
    public static TemperatureConversionResult Convert(
        decimal value,
        TemperatureScale fromScale,
        TemperatureScale toScale,
        int precision,
        string? ovenModeContext = null,
        string? safetyNote = null)
    {
        var source = Quantity.FromDecimal(value);

        var (converted, formula) = fromScale == toScale
            ? (source, "identity — source and target scale are the same")
            : fromScale == TemperatureScale.Celsius
                ? (source.Multiply(NineFifths).Add(ThirtyTwo), "°F = °C × 9/5 + 32")
                : (source.Subtract(ThirtyTwo).Multiply(FiveNinths), "°C = (°F − 32) × 5/9");

        return new TemperatureConversionResult
        {
            SourceValue = source,
            SourceScale = fromScale,
            ConvertedValue = converted,
            ConvertedDisplayValue = converted.ToDecimal(precision),
            ConvertedScale = toScale,
            Precision = precision,
            Rounding = MidpointRounding.ToEven,
            Formula = formula,
            OvenModeContext = ovenModeContext,
            SafetyNote = safetyNote,
        };
    }
}
