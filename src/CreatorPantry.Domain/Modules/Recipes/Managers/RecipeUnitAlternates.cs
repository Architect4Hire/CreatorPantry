using System.Globalization;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The alternate-unit text an export appends to an ingredient line, shared by every export format so they
/// cannot disagree about a conversion. All arithmetic is <see cref="UnitConversionCalculator"/>'s.
/// </summary>
/// <remarks>
/// <see cref="Result.Text"/> is <c>null</c> both when nothing needs converting (as written, no quantity, a
/// count or qualitative unit, already in the requested system) and when a conversion was refused; only the
/// latter sets <see cref="Result.Refusal"/>, so a caller can warn without guessing.
/// </remarks>
internal static class RecipeUnitAlternates
{
    internal readonly record struct Result(string? Text, string? Refusal);

    public static Result For(
        RecipeSnapshotIngredient line,
        RecipeUnitPresentation presentation,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> unitsById,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> targetUnits)
    {
        if (presentation == RecipeUnitPresentation.AsWritten
            || line.Quantity is not { } quantity
            || line.MeasurementUnitId is not { } unitId
            || line.MeasurementUnitDimension is not (MeasurementDimension.Mass or MeasurementDimension.Volume))
        {
            return default;
        }

        var system = presentation == RecipeUnitPresentation.Metric
            ? MeasurementSystem.Metric
            : MeasurementSystem.UsCustomary;

        if (!unitsById.TryGetValue(unitId, out var from)) return new(null, "its unit is unknown");
        if (from.System == system) return default;

        if (!targetUnits.TryGetValue(from.Dimension, out var to) || to.System != system)
        {
            return new(null, "no target unit was supplied");
        }

        var lower = UnitConversionCalculator.Convert(Quantity.FromDecimal(quantity), from, to);
        var upper = line.QuantityUpper is { } u
            ? UnitConversionCalculator.Convert(Quantity.FromDecimal(u), from, to)
            : null;
        if (!lower.Succeeded || upper is { Succeeded: false })
        {
            return new(null, "the units cannot be converted without more data");
        }

        if (system == MeasurementSystem.UsCustomary)
        {
            return new(
                QuantityDisplayCalculator.Format(
                    lower.Result!.ConvertedQuantity,
                    upper?.Result!.ConvertedQuantity,
                    to,
                    to.DisplayPrecision,
                    useAbbreviation: true).Text,
                null);
        }

        var text = Decimal(lower.Result!.ConvertedDisplayQuantity);
        if (upper is not null) text += "–" + Decimal(upper.Result!.ConvertedDisplayQuantity);
        return new($"{text} {to.Abbreviation}", null);
    }

    private static string Decimal(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}
