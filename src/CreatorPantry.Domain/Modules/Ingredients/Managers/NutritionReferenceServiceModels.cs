namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// How a read of nutrition reference evidence ended, for a caller outside this module (14.2). Coarser than the
/// gateway's own outcomes on purpose: another module can act on "found", "there is no such approved record"
/// and "ask again later", and on nothing finer.
/// </summary>
public enum NutritionReferenceStatus
{
    Found = 1,

    /// <summary>The ingredient is not an active catalogue entry, or the source has no such record.</summary>
    NotFound = 2,

    /// <summary>The record exists but is not from an approved data set, or nothing is approved for nutrition.</summary>
    NoApprovedSource = 3,

    /// <summary>This host has no evidence provider.</summary>
    NotConfigured = 4,

    /// <summary>Throttled, refused, unreachable or unreadable. Nothing can be concluded; ask again later.</summary>
    Unavailable = 5,
}

/// <summary>One approved food a search for a platform ingredient found.</summary>
public sealed record NutritionReferenceCandidateServiceModel(
    string SourceCode,
    string ExternalRecordId,
    string Description,
    string SourceVersion);

/// <param name="IngredientName">The platform ingredient's canonical name — what was searched for.</param>
/// <param name="Candidates">In the source's own ranking, best first. Empty unless the status is Found, and may be empty then.</param>
public sealed record NutritionReferenceSearchServiceModel(
    NutritionReferenceStatus Status,
    string? IngredientName,
    IReadOnlyList<NutritionReferenceCandidateServiceModel> Candidates);

/// <summary>One household portion of a reference food, as the source recorded it.</summary>
/// <param name="VolumeUnitCode">The <c>MeasurementUnit.Code</c> of its volume, or null when it is not a measured volume.</param>
public sealed record NutritionReferencePortionServiceModel(
    decimal Amount,
    decimal GramWeight,
    string? VolumeUnitCode,
    string? Condition,
    string SourceLabel);

/// <summary>One nutrient of a reference food, per 100 g of its edible portion, as the source reported it.</summary>
/// <param name="Derivation">How the source says it was obtained ("Analytical", "Assumed zero"), when it said.</param>
public sealed record NutritionReferenceNutrientServiceModel(
    NutrientCode Code,
    decimal AmountPer100Grams,
    NutrientUnit Unit,
    string SourceLabel,
    string? Derivation);

/// <summary>One reference food, with the provenance a mapping records. Everything but the status is null unless Found.</summary>
/// <param name="ReferenceSourceId">The cited <c>ReferenceSource</c> row.</param>
/// <param name="Nutrients">What the source reported. A nutrient not listed is unknown, never zero.</param>
/// <param name="Citation">The attribution an estimate built on this record must carry.</param>
/// <param name="LicenseCode">The SPDX licence the source's data is used under.</param>
public sealed record NutritionReferenceServiceModel(
    NutritionReferenceStatus Status,
    Guid? ReferenceSourceId = null,
    string? SourceCode = null,
    string? ExternalRecordId = null,
    string? Description = null,
    string? SourceVersion = null,
    IReadOnlyList<NutritionReferencePortionServiceModel>? Portions = null,
    IReadOnlyList<NutritionReferenceNutrientServiceModel>? Nutrients = null,
    string? Citation = null,
    string? LicenseCode = null,
    string? LicenseUrl = null);
