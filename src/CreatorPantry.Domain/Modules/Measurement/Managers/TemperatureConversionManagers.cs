using CreatorPantry.Domain.Managers.Quantities;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// The two scales <see cref="TemperatureConversionCalculator"/> knows. Not the generic
/// <see cref="CreatorPantry.Domain.Managers.Reference.MeasurementDimension.Temperature"/> unit catalogue: an
/// affine formula needs to know which of exactly two scales it is bridging, not a <c>BaseUnitFactor</c> that
/// Temperature units never carry.
/// </summary>
public enum TemperatureScale
{
    Celsius = 0,
    Fahrenheit = 1,
}

/// <summary>
/// One temperature conversion. Everything the caller supplied beyond the number and the two scales —
/// <see cref="Precision"/>, <see cref="OvenModeContext"/>, <see cref="SafetyNote"/> — is echoed back exactly as
/// given, proving the conversion touched only the number.
/// </summary>
public sealed record TemperatureConversionResult
{
    /// <summary>The value as given, exact — never itself rounded.</summary>
    public required Quantity SourceValue { get; init; }

    public required TemperatureScale SourceScale { get; init; }

    /// <summary>The result, exact. Equal to <see cref="SourceValue"/> when the source and target scale are the same.</summary>
    public required Quantity ConvertedValue { get; init; }

    /// <summary><see cref="ConvertedValue"/> rounded exactly once, at this one boundary, to <see cref="Precision"/> using <see cref="Rounding"/>.</summary>
    public required decimal ConvertedDisplayValue { get; init; }

    public required TemperatureScale ConvertedScale { get; init; }

    /// <summary>The caller's own display precision for this step's temperature — passed through, never chosen here.</summary>
    public required int Precision { get; init; }

    /// <summary>The rounding mode applied, returned explicitly rather than left for a caller to assume (CALC-002 — documented rounding).</summary>
    public required MidpointRounding Rounding { get; init; }

    /// <summary>The formula actually applied, for display.</summary>
    public required string Formula { get; init; }

    /// <summary>
    /// Convection/fan/conventional context or similar, exactly as the caller passed it in. This calculator has
    /// no such concept of its own — a null in is a null out, a value in is the same value out.
    /// </summary>
    public required string? OvenModeContext { get; init; }

    /// <summary>
    /// The step's own safety-adjacent note ("until the centre reads 74 °C"), exactly as the caller passed it
    /// in. Never read, parsed, or used to adjust the conversion (recipes.md — no doneness/safety inference).
    /// </summary>
    public required string? SafetyNote { get; init; }
}
