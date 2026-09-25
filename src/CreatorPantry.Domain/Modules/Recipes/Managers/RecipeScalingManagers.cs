using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Constants <see cref="RecipeScalingCalculator"/> uses, named so a threshold is never a bare literal.</summary>
public static class RecipeScalingPolicy
{
    /// <summary>
    /// Fractional digits kept for a line's rounded preview display when the line carries no unit, or the unit's
    /// own <c>DisplayPrecision</c> was not supplied to the calculator.
    /// </summary>
    public const int DefaultDisplayPrecision = 2;

    /// <summary>
    /// A resolved factor at or above this is flagged <see cref="RecipeScalingWarningCode.ExtremeScaleFactor"/>.
    /// Doubling or halving a recipe is the ordinary case the linear model handles well; an order-of-magnitude
    /// change is where leavening, pan size, and cook time stop behaving linearly, so it is surfaced rather than
    /// computed silently (recipes.md).
    /// </summary>
    public static readonly Quantity ExtremeFactorHigh = Quantity.FromInt(20);

    /// <summary>The low-side mirror of <see cref="ExtremeFactorHigh"/> — a factor at or below 1/20.</summary>
    public static readonly Quantity ExtremeFactorLow = Quantity.FromFraction(1, 20);

    /// <summary>
    /// The largest factor this calculator will resolve, and the reciprocal of the smallest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hard refusal, not a warning — <see cref="RecipeScalingWarningCode.ExtremeScaleFactor"/> is the
    /// warning, and it deliberately does not gate anything. This exists because
    /// <see cref="Quantity.ToDecimal(int, MidpointRounding)"/> throws <see cref="OverflowException"/> when a
    /// result will not fit a <c>decimal</c>, and an escaping exception is a 500 rather than an answer.
    /// </para>
    /// <para>
    /// A million is far beyond any recipe and still leaves seven orders of magnitude of headroom: ingredient
    /// quantities are <c>decimal(28,12)</c>, so at most about 1e16, and 1e16 × 1e6 is 1e22 against decimal's
    /// 7.9e28 ceiling. It also catches the indirect route, where a target yield against a near-zero recipe
    /// yield derives an astronomical factor from two individually reasonable numbers.
    /// </para>
    /// </remarks>
    public static readonly Quantity MaxFactor = Quantity.FromInt(1_000_000);
}

/// <summary>
/// Why a line's quantity did or did not move, or why its rounded display deserves a second look. This is the
/// warning taxonomy CALC-005/CALC-006 ask a scaling preview to show rather than leaving a client to guess.
/// </summary>
public enum RecipeScalingWarningCode
{
    /// <summary>
    /// <see cref="IngredientScaling.Fixed"/> — the quantity is kept exactly as written. Published so a client
    /// can say why, instead of a line silently not changing (recipes.md's "not silently").
    /// </summary>
    FixedQuantityNotScaled = 0,

    /// <summary>
    /// <see cref="IngredientScaling.ReviewRequired"/> with no quantity at all — "salt, to taste". Nothing here
    /// is multiplied because there is nothing numeric to multiply.
    /// </summary>
    ReviewRequiredQualitative = 1,

    /// <summary>
    /// <see cref="IngredientScaling.ReviewRequired"/> on a <see cref="MeasurementDimension.Count"/> quantity —
    /// "2 eggs", or a package line such as "1 (14.5 oz) can", which is authored as a count rather than as its
    /// own concept.
    /// </summary>
    ReviewRequiredDiscreteCount = 2,

    /// <summary><see cref="IngredientScaling.ReviewRequired"/> on a line that carries a range.</summary>
    ReviewRequiredRange = 3,

    /// <summary>
    /// <see cref="IngredientScaling.ReviewRequired"/> for a reason this schema does not capture — vessel size,
    /// leavening, fermentation timing, a spice, or a thickener (recipes.md). The creator's own choice to flag
    /// the line is trusted rather than re-derived here.
    /// </summary>
    ReviewRequiredOther = 4,

    /// <summary>
    /// A line is not marked <see cref="IngredientScaling.ReviewRequired"/> but has no quantity to multiply. A
    /// data inconsistency, not a normal case — the line is left unchanged and flagged rather than treated as
    /// zero.
    /// </summary>
    NoQuantityToScale = 5,

    /// <summary>
    /// The exact scaled quantity is not zero, but rounds to zero at the line's display precision. The exact
    /// value is still returned in full; only the rounded preview would misleadingly read as nothing.
    /// </summary>
    ScaledToNegligibleDisplay = 6,

    /// <summary>Recipe-level: <c>Recipe.YieldQuantity</c> is absent, so a scaled yield cannot be shown as a number.</summary>
    RecipeYieldNotStructured = 7,

    /// <summary>
    /// Recipe-level: the resolved factor is at or beyond <see cref="RecipeScalingPolicy.ExtremeFactorHigh"/> or
    /// <see cref="RecipeScalingPolicy.ExtremeFactorLow"/>. The calculation still runs in full; this only asks
    /// for a second look.
    /// </summary>
    ExtremeScaleFactor = 8,
}

/// <summary>Why a scaling request could not be resolved to a factor at all.</summary>
public enum RecipeScalingRequestError
{
    /// <summary>The submitted multiplier is zero or negative.</summary>
    NonPositiveMultiplier = 0,

    /// <summary>The submitted target yield is zero or negative.</summary>
    NonPositiveTargetYield = 1,

    /// <summary>
    /// A target-yield request needs <c>Recipe.YieldQuantity</c> to derive a multiplier from, and it is absent,
    /// zero, or negative — the recipe's yield is text-only ("serves a crowd") or was never given a number.
    /// </summary>
    RecipeYieldNotStructured = 2,

    /// <summary>
    /// The resolved factor is beyond <see cref="RecipeScalingPolicy.MaxFactor"/>, or below its reciprocal.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="RecipeScalingWarningCode.ExtremeScaleFactor"/>, which is a caution about a
    /// calculation that still runs. This is a refusal: past this point a scaled quantity no longer fits a
    /// <c>decimal</c>, and the honest answer is that the request cannot be computed rather than an exception
    /// escaping as a 500. Reachable both directly and through a target yield against a near-zero recipe
    /// yield.
    /// </remarks>
    FactorOutOfRange = 3,
}

/// <summary>
/// One ingredient line as <see cref="RecipeScalingCalculator"/> needs it — not the entity or the ServiceModel,
/// because neither carries everything scaling needs in one place: the ServiceModel omits unit dimension and
/// precision (they are facts about the unit, resolved by the caller), and the entity has no display precision
/// at all. Building this is the caller's job (Business, once this is wired to a request) so the calculator
/// itself stays free of any dependency on the Measurement module.
/// </summary>
public sealed record RecipeIngredientScalingInput
{
    public required Guid Id { get; init; }

    /// <summary>The creator's own text for the line, carried through unchanged for the preview.</summary>
    public required string DisplayText { get; init; }

    /// <summary>The low end or single value, canonical and unrounded. <see langword="null"/> means no number at all.</summary>
    public required decimal? Quantity { get; init; }

    /// <summary>The high end of a range. Always <see langword="null"/> when <see cref="Quantity"/> is.</summary>
    public required decimal? QuantityUpper { get; init; }

    public required IngredientScaling ScalingBehavior { get; init; }

    /// <summary>The line's unit's dimension, when it has a unit. Refines a <see cref="RecipeScalingWarningCode"/> choice.</summary>
    public required MeasurementDimension? MeasurementUnitDimension { get; init; }

    /// <summary>
    /// The line's unit's display precision, when it has a unit. Falls back to
    /// <see cref="RecipeScalingPolicy.DefaultDisplayPrecision"/> when absent.
    /// </summary>
    public required int? DisplayPrecision { get; init; }
}

/// <summary>
/// A scaling request: a direct multiplier, or a target yield to derive one from. Exactly one is ever set —
/// enforced by construction, not by convention — so <see cref="RecipeScalingCalculator"/> never has to guess
/// which the caller meant.
/// </summary>
public sealed record RecipeScalingRequest
{
    private RecipeScalingRequest(Quantity? multiplier, Quantity? targetYieldQuantity) =>
        (Multiplier, TargetYieldQuantity) = (multiplier, targetYieldQuantity);

    /// <summary>The factor to scale by, when the request named one directly.</summary>
    public Quantity? Multiplier { get; }

    /// <summary>The yield to scale toward, when the request named a target instead of a factor.</summary>
    public Quantity? TargetYieldQuantity { get; }

    public static RecipeScalingRequest ForMultiplier(Quantity multiplier) => new(multiplier, null);

    public static RecipeScalingRequest ForTargetYield(Quantity targetYieldQuantity) => new(null, targetYieldQuantity);
}

/// <summary>One ingredient line's scaling preview.</summary>
public sealed record RecipeIngredientScalingPreviewLine
{
    public required Guid Id { get; init; }

    public required string DisplayText { get; init; }

    /// <summary><see langword="false"/> for <see cref="IngredientScaling.Fixed"/> and <see cref="IngredientScaling.ReviewRequired"/> lines.</summary>
    public required bool WasScaled { get; init; }

    /// <summary>
    /// The exact result — the original quantity when <see cref="WasScaled"/> is <see langword="false"/>, or the
    /// scaled quantity otherwise. <see langword="null"/> only when the line had no quantity to begin with.
    /// </summary>
    public required Quantity? ScaledQuantity { get; init; }

    /// <summary>The exact upper bound, mirroring <see cref="ScaledQuantity"/>, when the line carries a range.</summary>
    public required Quantity? ScaledQuantityUpper { get; init; }

    /// <summary>
    /// <see cref="ScaledQuantity"/> rounded for display. Always derived from the exact value directly, never
    /// from a previous rounding, so rounding never compounds across repeated scaling (recipes.md, ING-006).
    /// </summary>
    public required decimal? ScaledDisplayQuantity { get; init; }

    public required decimal? ScaledDisplayQuantityUpper { get; init; }

    public required IReadOnlyList<RecipeScalingWarningCode> Warnings { get; init; }
}

/// <summary>
/// A complete scaling proposal. Nothing here is persisted or mutates a recipe (recipes.md) — it is a preview
/// for a creator to accept or discard.
/// </summary>
public sealed record RecipeScalingPreview
{
    /// <summary>The multiplier actually applied — either the request's own, or the one derived from a target yield.</summary>
    public required Quantity Factor { get; init; }

    /// <summary>Echoes the request's target yield, when the request was made that way.</summary>
    public required Quantity? RequestedTargetYieldQuantity { get; init; }

    /// <summary>The recipe's yield scaled by <see cref="Factor"/>, or <see langword="null"/> when the recipe has no structured yield.</summary>
    public required Quantity? ScaledYieldQuantity { get; init; }

    public required decimal? ScaledYieldDisplayQuantity { get; init; }

    public required IReadOnlyList<RecipeIngredientScalingPreviewLine> Lines { get; init; }

    /// <summary>Warnings about the request as a whole, rather than about any one line.</summary>
    public required IReadOnlyList<RecipeScalingWarningCode> RecipeWarnings { get; init; }
}

/// <summary>
/// The outcome of a scaling calculation: a <see cref="RecipeScalingPreview"/>, or a
/// <see cref="RecipeScalingRequestError"/> when the request could not be resolved to a factor at all. Not
/// <c>OperationResult&lt;T&gt;</c> — that type is documented as the outcome of a facade operation, and
/// <see cref="RecipeScalingCalculator"/> is a pure domain service with no facade around it yet.
/// </summary>
public sealed class RecipeScalingOutcome
{
    private RecipeScalingOutcome(RecipeScalingPreview? preview, RecipeScalingRequestError? error) =>
        (Preview, Error) = (preview, error);

    public RecipeScalingPreview? Preview { get; }

    public RecipeScalingRequestError? Error { get; }

    public bool Succeeded => Error is null;

    public static RecipeScalingOutcome Success(RecipeScalingPreview preview) => new(preview, null);

    public static RecipeScalingOutcome Failure(RecipeScalingRequestError error) => new(null, error);
}
