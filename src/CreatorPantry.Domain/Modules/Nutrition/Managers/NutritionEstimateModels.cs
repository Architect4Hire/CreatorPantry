using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

// ---- What a line came to ----

/// <summary>Whether one recipe line counted towards the estimate, and if not, why not.</summary>
public enum NutritionLineOutcome
{
    /// <summary>Weighed, mapped to a confirmed reference, and counted.</summary>
    Included = 1,

    /// <summary>No platform ingredient, or no confirmed mapping for it.</summary>
    Unmapped = 2,

    /// <summary>The confirmed reference is under review, or the source no longer has it as confirmed.</summary>
    NeedsReview = 3,

    /// <summary>Mapped, but its quantity cannot be turned into grams.</summary>
    Unconvertible = 4,

    /// <summary>No quantity to count — "salt to taste". Not required, and not counted.</summary>
    NotQuantified = 5,

    /// <summary>Marked optional by the creator. Not required, and not counted.</summary>
    Optional = 6,
}

/// <summary>The detail behind a line's outcome.</summary>
public enum NutritionLineReason
{
    None = 0,
    NoIngredient = 1,
    NoMapping = 2,
    MappingNotConfirmed = 3,
    MappingUnderReview = 4,
    SourceVersionChanged = 5,
    ReferenceWithdrawn = 6,
    NoUnit = 7,
    UnitUnknown = 8,
    UnitNotMeasurable = 9,
    NoPortion = 10,
    PortionDoesNotMatch = 11,
}

/// <summary>
/// How a line's grams were known — and, as the weakest among the counted lines, the estimate's confidence.
/// Ordered from strongest to weakest.
/// </summary>
public enum NutritionQuantityBasis
{
    /// <summary>The line is in a mass unit: its grams are its quantity.</summary>
    Mass = 1,

    /// <summary>Converted through a household portion the source recorded.</summary>
    ReferencePortion = 2,

    /// <summary>Converted through a weight the creator gave.</summary>
    CreatorPortion = 3,
}

public enum NutritionEstimateStatus
{
    /// <summary>Every required line is counted.</summary>
    Complete = 1,

    /// <summary>At least one required line is not. The totals are a known-incomplete lower bound.</summary>
    Incomplete = 2,
}

/// <summary>Where the number of servings came from.</summary>
public enum NutritionServingsSource
{
    Requested = 1,
    ServingCount = 2,

    /// <summary>The batch divided by the serving size, both in the version's own yield unit.</summary>
    YieldOverServingSize = 3,

    /// <summary>The version says nothing a serving count can be had from; there are no per-serving figures.</summary>
    None = 4,
}

// ---- What the calculator is given ----

/// <param name="LineId">The line's id in the version's snapshot.</param>
public sealed record NutritionEstimateLineInput(
    Guid LineId,
    string DisplayText,
    decimal? Quantity,
    decimal? QuantityUpper,
    Guid? MeasurementUnitId,
    Guid? IngredientId,
    bool IsOptional);

/// <param name="BaseUnitFactor">How many of the dimension's base unit (g, ml, one) this unit is.</param>
public sealed record NutritionEstimateUnitInput(Guid Id, MeasurementDimension Dimension, decimal? BaseUnitFactor);

/// <summary>A workspace's mapping of one ingredient, as the calculator needs it.</summary>
public sealed record NutritionEstimateMappingInput(
    Guid MappingId,
    Guid IngredientId,
    NutritionMappingStatus Status,
    string? SourceCode,
    string? ExternalRecordId,
    string? SourceVersion,
    Guid? PortionMeasurementUnitId,
    decimal? PortionAmount,
    decimal? PortionGrams,
    NutritionPortionOrigin? PortionOrigin);

public sealed record NutritionEstimateNutrientInput(NutrientCode Code, decimal AmountPer100Grams, NutrientUnit Unit);

/// <summary>A reference food as the source reports it now.</summary>
public sealed record NutritionEstimateReferenceInput(
    string SourceCode,
    string ExternalRecordId,
    string SourceVersion,
    string Citation,
    string LicenseCode,
    string LicenseUrl,
    IReadOnlyList<NutritionEstimateNutrientInput> Nutrients);

/// <param name="Requested">A serving count the caller chose, which overrides the version's.</param>
public sealed record NutritionEstimateServingsInput(
    decimal? Requested,
    decimal? ServingCount,
    decimal? YieldQuantity,
    decimal? ServingSize);

/// <summary>Everything one estimate is computed from. Nothing else is read.</summary>
/// <param name="References">
/// The confirmed mappings' records as the source reports them now, keyed by source code and record id. A
/// confirmed mapping whose record is absent here is one the source withdrew.
/// </param>
public sealed record NutritionEstimateInput(
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    IReadOnlyList<NutritionEstimateLineInput> Lines,
    IReadOnlyDictionary<Guid, NutritionEstimateUnitInput> Units,
    IReadOnlyDictionary<Guid, NutritionEstimateMappingInput> MappingsByIngredient,
    IReadOnlyDictionary<(string SourceCode, string ExternalRecordId), NutritionEstimateReferenceInput> References,
    NutritionEstimateServingsInput Servings);

// ---- What the estimate says ----

/// <summary>
/// One value of the estimate: exact to six places, and rounded once for display. A range when any counted line
/// was a range; otherwise <see cref="Low"/> equals <see cref="High"/>.
/// </summary>
/// <param name="Trace">The value is not zero but rounds to zero for display. Say "trace", never "0".</param>
public sealed record NutritionAmountServiceModel(
    decimal Low,
    decimal High,
    decimal DisplayLow,
    decimal DisplayHigh,
    int DisplayPrecision,
    MidpointRounding Rounding,
    bool IsRange,
    bool Trace);

/// <param name="Complete">
/// Every counted line's reference reported this nutrient. When false, the value is a lower bound and the lines
/// that lacked it are named: a nutrient a source did not list is unknown, never zero.
/// </param>
/// <param name="PerServing">Null when the version gives no serving count to divide by.</param>
public sealed record NutritionNutrientEstimateServiceModel(
    NutrientCode Code,
    NutrientUnit Unit,
    NutritionAmountServiceModel WholeRecipe,
    NutritionAmountServiceModel? PerServing,
    bool Complete,
    IReadOnlyList<Guid> LinesMissingIt);

public sealed record NutritionEstimateSourceServiceModel(string SourceCode, string ExternalRecordId, string SourceVersion);

/// <param name="Required">Not optional and quantified: a line the estimate is not complete without.</param>
/// <param name="GramsLow">The line's weight, exact to six places, when it was counted.</param>
public sealed record NutritionLineEstimateServiceModel(
    Guid LineId,
    string DisplayText,
    NutritionLineOutcome Outcome,
    NutritionLineReason Reason,
    bool Required,
    decimal? GramsLow,
    decimal? GramsHigh,
    NutritionQuantityBasis? Basis,
    Guid? MappingId,
    NutritionEstimateSourceServiceModel? Source);

public sealed record NutritionCitationServiceModel(string SourceCode, string Citation, string LicenseCode, string LicenseUrl);

/// <summary>
/// A nutrition estimate for one recipe version (14.3): deterministic, labelled as an estimate, and explicit about
/// everything it did not count.
/// </summary>
/// <param name="Label">Always "Estimate". Nothing here is a measured or certified value.</param>
/// <param name="Publishable">
/// True only when the estimate is complete. A caller that publishes nutrition publishes nothing when this is
/// false, and of a publishable estimate only the nutrients that are themselves complete.
/// </param>
/// <param name="Partial">The totals leave out at least one required line, so they are a lower bound.</param>
/// <param name="Confidence">The weakest quantity basis among the counted lines; null when none was counted.</param>
public sealed record NutritionEstimateServiceModel(
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    string Label,
    string MethodVersion,
    string Method,
    NutritionEstimateStatus Status,
    bool Publishable,
    bool Partial,
    NutritionQuantityBasis? Confidence,
    decimal? Servings,
    NutritionServingsSource ServingsSource,
    IReadOnlyList<NutritionNutrientEstimateServiceModel> Nutrients,
    IReadOnlyList<NutritionLineEstimateServiceModel> Lines,
    IReadOnlyList<NutritionCitationServiceModel> Sources);
