using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>What <see cref="YieldReconciliationCalculator"/> concluded about a recipe's stated numbers.</summary>
public enum YieldReconciliationStatus
{
    /// <summary>All three of batch yield, serving count, and serving size were given, and agree at the stated precision.</summary>
    Reconciled = 0,

    /// <summary>Exactly two of the three were given; the third was computed exactly.</summary>
    Solved = 1,

    /// <summary>
    /// All three were given, but <c>servingCount × servingSize</c> does not agree with the stated batch yield
    /// even after rounding. Nothing is overridden — none of the three is assumed to be the wrong one.
    /// </summary>
    Contradictory = 2,

    /// <summary>
    /// Fewer than two of batch yield, serving count, and serving size were given. Nothing can be computed —
    /// recipes.md forbids inventing a missing serving definition.
    /// </summary>
    InsufficientInput = 3,
}

/// <summary>Which of the three reconciled values <see cref="YieldReconciliationStatus.Solved"/> computed.</summary>
public enum YieldReconciliationField
{
    BatchYield = 0,
    ServingCount = 1,
    ServingSize = 2,
}

/// <summary>Why a reconciliation request was refused outright, before any case analysis.</summary>
public enum YieldReconciliationError
{
    /// <summary>One of the supplied values was zero or negative — no recipe yields, serves, or holds a non-positive amount.</summary>
    NonPositiveInput = 0,
}

/// <summary>
/// A recipe's stated yield numbers, as far as the creator has entered them. Every value is independently
/// optional — that is the point: which subset is present is what decides whether anything can be reconciled.
/// </summary>
/// <param name="Dimension">
/// The dimension <see cref="BatchYield"/>, <see cref="ServingSize"/>, and <see cref="PanVolume"/> are all
/// expressed in (the caller is responsible for having already brought them to one unit — this calculator does
/// no unit conversion of its own; see
/// <see cref="CreatorPantry.Domain.Modules.Measurement.Managers.UnitConversionCalculator"/>, 7.7, for that).
/// </param>
/// <param name="DisplayPrecision">
/// The rounding precision that decides whether a stated batch yield and a computed one count as agreeing
/// (CALC-002 — a documented tolerance, not a silent one).
/// </param>
/// <param name="BatchYield">The recipe's total yield, in <paramref name="Dimension"/>.</param>
/// <param name="ServingCount">The number of servings. Dimensionless — a serving is not itself a measurement unit.</param>
/// <param name="ServingSize">The amount per serving, in <paramref name="Dimension"/>.</param>
/// <param name="PanVolume">
/// A stated pan or vessel capacity, in <paramref name="Dimension"/>. Only ever compared directly against a
/// known batch yield as a plain ratio — never used to derive a yield, a fill assumption, or a pan geometry
/// this calculator was not given outright.
/// </param>
public sealed record YieldReconciliationInput(
    MeasurementDimension Dimension,
    int DisplayPrecision,
    decimal? BatchYield,
    decimal? ServingCount,
    decimal? ServingSize,
    decimal? PanVolume);

/// <summary>
/// A yield reconciliation preview. Nothing here is persisted or mutates a recipe (recipes.md) — every field is
/// a proposal for a creator to confirm.
/// </summary>
public sealed record YieldReconciliationPreview
{
    public required YieldReconciliationStatus Status { get; init; }

    /// <summary>The given value, or the solved one when <see cref="SolvedField"/> is <see cref="YieldReconciliationField.BatchYield"/>.</summary>
    public required decimal? BatchYield { get; init; }

    /// <summary>The given value, or the solved one when <see cref="SolvedField"/> is <see cref="YieldReconciliationField.ServingCount"/>.</summary>
    public required decimal? ServingCount { get; init; }

    /// <summary>The given value, or the solved one when <see cref="SolvedField"/> is <see cref="YieldReconciliationField.ServingSize"/>.</summary>
    public required decimal? ServingSize { get; init; }

    /// <summary>Which value <see cref="YieldReconciliationStatus.Solved"/> computed; <see langword="null"/> for every other status.</summary>
    public required YieldReconciliationField? SolvedField { get; init; }

    /// <summary>A short, deterministic description of the arithmetic performed; <see langword="null"/> for <see cref="YieldReconciliationStatus.InsufficientInput"/>.</summary>
    public required string? Formula { get; init; }

    /// <summary>The given pan/vessel capacity, echoed; <see langword="null"/> when none was given.</summary>
    public required decimal? PanVolume { get; init; }

    /// <summary>
    /// The known batch yield divided by <see cref="PanVolume"/>, exact — purely descriptive, never a
    /// fits/does-not-fit verdict. Set only when <see cref="PanComparable"/> is <see langword="true"/>.
    /// </summary>
    public required Quantity? PanFillRatio { get; init; }

    /// <summary>
    /// <see langword="null"/> when no <see cref="PanVolume"/> was given at all (nothing to compare);
    /// <see langword="false"/> when one was given but could not be compared — the dimension is not Volume, or
    /// no batch yield is known; <see langword="true"/> when <see cref="PanFillRatio"/> was computed.
    /// </summary>
    public required bool? PanComparable { get; init; }
}

/// <summary>
/// The outcome of a reconciliation: a <see cref="YieldReconciliationPreview"/>, or a
/// <see cref="YieldReconciliationError"/> when the request itself was invalid. Not
/// <c>OperationResult&lt;T&gt;</c> — see <c>RecipeScalingOutcome</c> (7.6) for why.
/// </summary>
public sealed class YieldReconciliationOutcome
{
    private YieldReconciliationOutcome(YieldReconciliationPreview? preview, YieldReconciliationError? error) =>
        (Preview, Error) = (preview, error);

    public YieldReconciliationPreview? Preview { get; }

    public YieldReconciliationError? Error { get; }

    public bool Succeeded => Error is null;

    public static YieldReconciliationOutcome Success(YieldReconciliationPreview preview) => new(preview, null);

    public static YieldReconciliationOutcome Failure(YieldReconciliationError error) => new(null, error);
}
