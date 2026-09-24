using System.Globalization;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// Computes a deterministic unit conversion: same-dimension arithmetic via
/// <see cref="CreatorPantry.Domain.Modules.Measurement.Data.Entities.MeasurementUnit.BaseUnitFactor"/> ratios, or
/// ingredient-specific Mass/Volume bridging via a supplied density fact (ING-004, CALC-003/004/006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two operations, never blurred.</strong> <see cref="MeasurementPolicy.MayConvert"/> already decides
/// whether two same-dimension units may bridge by arithmetic alone — this calculator calls it rather than
/// re-deciding. Crossing Mass and Volume is a different operation with a different input (a density fact,
/// never inferred), and crossing into or out of Temperature is a third operation this calculator refuses
/// outright — that is 7.8's affine formula, not a factor ratio.
/// </para>
/// <para>
/// <strong>Exact, then rounded once, at the edge.</strong> Every quantity — the value being converted, both
/// units' <see cref="CreatorPantry.Domain.Modules.Measurement.Data.Entities.MeasurementUnit.BaseUnitFactor"/>,
/// and a supplied density's own mass and volume — is read as an exact <see cref="Quantity"/> and combined
/// exactly. A <c>decimal</c> is derived from the result for display only, once, at the very end. Nothing here
/// ever recomputes from a previously rounded value.
/// </para>
/// <para>
/// Pure and stateless, like <see cref="CreatorPantry.Domain.Modules.Recipes.Managers.RecipeScalingCalculator"/>
/// (7.6): no persistence, no clock, no workspace, and no dependency on the Ingredients module — a caller
/// resolves both units and, when bridging
/// Mass and Volume, resolves the one <c>Approved</c> <c>IngredientDensityReference</c> to use and maps it to
/// <see cref="IngredientDensityConversionInput"/>.
/// </para>
/// </remarks>
public static class UnitConversionCalculator
{
    /// <summary>Converts <paramref name="quantity"/>, expressed in <paramref name="fromUnit"/>, into <paramref name="toUnit"/>.</summary>
    /// <param name="density">
    /// The density fact to bridge Mass and Volume with. Required, and used, only when the two units' dimensions
    /// are one Mass and one Volume; ignored otherwise. Its own <see cref="IngredientDensityConversionInput.MassUnit"/>
    /// and <see cref="IngredientDensityConversionInput.VolumeUnit"/> need not match <paramref name="fromUnit"/>
    /// or <paramref name="toUnit"/> — each side converts through its own <c>BaseUnitFactor</c> first.
    /// </param>
    public static UnitConversionOutcome Convert(
        Quantity quantity,
        MeasurementUnitServiceModel fromUnit,
        MeasurementUnitServiceModel toUnit,
        IngredientDensityConversionInput? density = null)
    {
        ArgumentNullException.ThrowIfNull(fromUnit);
        ArgumentNullException.ThrowIfNull(toUnit);

        if (fromUnit.Dimension is MeasurementDimension.Temperature || toUnit.Dimension is MeasurementDimension.Temperature)
            return UnitConversionOutcome.Failure(UnitConversionError.TemperatureNotSupported);

        if (fromUnit.Dimension == toUnit.Dimension)
        {
            return MeasurementPolicy.MayConvert(fromUnit.Dimension, fromUnit.Code, toUnit.Dimension, toUnit.Code)
                ? UnitConversionOutcome.Success(ConvertSameDimension(quantity, fromUnit, toUnit))
                : UnitConversionOutcome.Failure(UnitConversionError.IncompatibleUnits);
        }

        if (!IsMassVolumePair(fromUnit.Dimension, toUnit.Dimension))
            return UnitConversionOutcome.Failure(UnitConversionError.IncompatibleUnits);

        return density is null
            ? UnitConversionOutcome.Failure(UnitConversionError.MissingDensity)
            : UnitConversionOutcome.Success(ConvertByDensity(quantity, fromUnit, toUnit, density));
    }

    private static bool IsMassVolumePair(MeasurementDimension from, MeasurementDimension to) =>
        (from is MeasurementDimension.Mass && to is MeasurementDimension.Volume) ||
        (from is MeasurementDimension.Volume && to is MeasurementDimension.Mass);

    private static UnitConversionResult ConvertSameDimension(
        Quantity quantity, MeasurementUnitServiceModel fromUnit, MeasurementUnitServiceModel toUnit)
    {
        if (fromUnit.Dimension is MeasurementDimension.Qualitative)
        {
            // MayConvert only reaches here when the codes are identical — nothing numeric to combine.
            return BuildResult(
                UnitConversionMethod.SameDimensionFactor,
                quantity,
                toUnit,
                source: null,
                formula: $"identity — {fromUnit.Code} carries no numeric relationship to convert");
        }

        var factor = Divide(Quantity.FromDecimal(fromUnit.BaseUnitFactor!.Value), Quantity.FromDecimal(toUnit.BaseUnitFactor!.Value));

        return BuildResult(
            UnitConversionMethod.SameDimensionFactor,
            quantity.Multiply(factor),
            toUnit,
            source: null,
            formula: $"× ({DecimalString(fromUnit.BaseUnitFactor.Value)} {fromUnit.Code}-per-base ÷ {DecimalString(toUnit.BaseUnitFactor.Value)} {toUnit.Code}-per-base)");
    }

    private static UnitConversionResult ConvertByDensity(
        Quantity quantity, MeasurementUnitServiceModel fromUnit, MeasurementUnitServiceModel toUnit, IngredientDensityConversionInput density)
    {
        var gramsInReference = Quantity.FromDecimal(density.MassQuantity).Multiply(Quantity.FromDecimal(density.MassUnit.BaseUnitFactor!.Value));
        var millilitresInReference = Quantity.FromDecimal(density.VolumeQuantity).Multiply(Quantity.FromDecimal(density.VolumeUnit.BaseUnitFactor!.Value));
        var gramsPerMillilitre = Divide(gramsInReference, millilitresInReference);

        var fromFactor = Quantity.FromDecimal(fromUnit.BaseUnitFactor!.Value);
        var toFactor = Quantity.FromDecimal(toUnit.BaseUnitFactor!.Value);

        var converted = fromUnit.Dimension is MeasurementDimension.Mass
            ? Divide(Divide(quantity.Multiply(fromFactor), gramsPerMillilitre), toFactor) // grams -> ml -> toUnit
            : Divide(quantity.Multiply(fromFactor).Multiply(gramsPerMillilitre), toFactor); // ml -> grams -> toUnit

        return BuildResult(
            UnitConversionMethod.IngredientDensity,
            converted,
            toUnit,
            source: density.SourceCitation,
            formula: $"× density {DecimalString(density.MassQuantity)} {density.MassUnit.Code} per {DecimalString(density.VolumeQuantity)} {density.VolumeUnit.Code}");
    }

    private static UnitConversionResult BuildResult(
        UnitConversionMethod method, Quantity converted, MeasurementUnitServiceModel toUnit, string? source, string formula) =>
        new()
        {
            Method = method,
            ConvertedQuantity = converted,
            ConvertedDisplayQuantity = converted.ToDecimal(toUnit.DisplayPrecision),
            Precision = toUnit.DisplayPrecision,
            Rounding = MidpointRounding.ToEven,
            Formula = formula,
            Source = source,
        };

    /// <summary>Division for two exact quantities — <see cref="Quantity"/> has no <c>Divide</c> of its own; this composes it from <see cref="Quantity.Multiply"/> via the reciprocal fraction.</summary>
    private static Quantity Divide(Quantity numerator, Quantity denominator) =>
        Quantity.FromFraction(numerator.Numerator * denominator.Denominator, numerator.Denominator * denominator.Numerator);

    private static string DecimalString(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
