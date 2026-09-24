using CreatorPantry.Domain.Managers.Quantities;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>Which arithmetic <see cref="UnitConversionCalculator"/> actually performed.</summary>
public enum UnitConversionMethod
{
    /// <summary>Both units share a dimension; the ratio of their own <c>BaseUnitFactor</c>s bridges them.</summary>
    SameDimensionFactor = 0,

    /// <summary>The units are Mass and Volume; an ingredient-specific density fact bridges them (ING-004).</summary>
    IngredientDensity = 1,
}

/// <summary>Why a conversion could not be computed at all.</summary>
public enum UnitConversionError
{
    /// <summary>
    /// The units cannot be bridged by arithmetic: different dimensions with no density supplied and not a
    /// Mass/Volume pair, different <see cref="CreatorPantry.Domain.Managers.Reference.MeasurementDimension.Count"/> nouns
    /// ("bunch" is not "clove"), or different <see cref="CreatorPantry.Domain.Managers.Reference.MeasurementDimension.Qualitative"/> phrases.
    /// </summary>
    IncompatibleUnits = 0,

    /// <summary>The units are a Mass/Volume pair, but no <see cref="IngredientDensityConversionInput"/> was supplied.</summary>
    MissingDensity = 1,

    /// <summary>
    /// Either unit is <see cref="CreatorPantry.Domain.Managers.Reference.MeasurementDimension.Temperature"/>. Temperature is
    /// affine, not multiplicative, and is its own operation (7.8) — refused here rather than miscomputed.
    /// </summary>
    TemperatureNotSupported = 2,
}

/// <summary>
/// An ingredient's cited mass-for-volume equivalence, already resolved to the units it needs, for
/// <see cref="UnitConversionCalculator"/> to bridge Mass and Volume with. Deliberately not
/// <c>IngredientDensityReference</c> itself: the calculator has no dependency on the Ingredients module, and
/// needs only the canonical figures and their units, plus enough provenance to cite. Resolving an
/// <c>Approved</c> density row for a specific ingredient and condition, and mapping it to this shape, is the
/// caller's job.
/// </summary>
/// <param name="MassQuantity">The reference measurement's mass, in <paramref name="MassUnit"/>.</param>
/// <param name="MassUnit">Must have <see cref="MeasurementUnitServiceModel.Dimension"/> of Mass.</param>
/// <param name="VolumeQuantity">The same measurement's volume, in <paramref name="VolumeUnit"/>.</param>
/// <param name="VolumeUnit">Must have <see cref="MeasurementUnitServiceModel.Dimension"/> of Volume.</param>
/// <param name="SourceCitation">Human-readable attribution — recipes.md requires a usable fact to say where it came from.</param>
public sealed record IngredientDensityConversionInput(
    decimal MassQuantity,
    MeasurementUnitServiceModel MassUnit,
    decimal VolumeQuantity,
    MeasurementUnitServiceModel VolumeUnit,
    string SourceCitation);

/// <summary>A completed conversion: the exact result, its rounded display form, and how it was computed.</summary>
public sealed record UnitConversionResult
{
    public required UnitConversionMethod Method { get; init; }

    /// <summary>The result as an exact fraction — computed from canonical inputs throughout, never from a rounded display value.</summary>
    public required Quantity ConvertedQuantity { get; init; }

    /// <summary>
    /// <see cref="ConvertedQuantity"/> rounded exactly once, at this one boundary, to <see cref="Precision"/>
    /// using <see cref="Rounding"/>.
    /// </summary>
    public required decimal ConvertedDisplayQuantity { get; init; }

    /// <summary>The target unit's own display precision — the number of digits the caller asked to see.</summary>
    public required int Precision { get; init; }

    /// <summary>The rounding mode applied, returned explicitly rather than left for a caller to assume (CALC-002 — documented rounding).</summary>
    public required MidpointRounding Rounding { get; init; }

    /// <summary>A short, deterministic description of the operation actually performed.</summary>
    public required string Formula { get; init; }

    /// <summary>The density fact's citation, when <see cref="Method"/> is <see cref="UnitConversionMethod.IngredientDensity"/>; otherwise <see langword="null"/>.</summary>
    public required string? Source { get; init; }
}

/// <summary>
/// The outcome of a conversion: a <see cref="UnitConversionResult"/>, or a <see cref="UnitConversionError"/>
/// when the units cannot be bridged at all. Not <c>OperationResult&lt;T&gt;</c> — that type is the outcome of a
/// facade operation, and <see cref="UnitConversionCalculator"/> is a pure domain service with no facade around
/// it yet (mirrors <c>RecipeScalingOutcome</c>, 7.6).
/// </summary>
public sealed class UnitConversionOutcome
{
    private UnitConversionOutcome(UnitConversionResult? result, UnitConversionError? error) =>
        (Result, Error) = (result, error);

    public UnitConversionResult? Result { get; }

    public UnitConversionError? Error { get; }

    public bool Succeeded => Error is null;

    public static UnitConversionOutcome Success(UnitConversionResult result) => new(result, null);

    public static UnitConversionOutcome Failure(UnitConversionError error) => new(null, error);
}
