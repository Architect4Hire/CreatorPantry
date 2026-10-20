namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>Where one approved trait came from, as an analysis must cite it.</summary>
/// <param name="IsActive">False for a retired source: still citable, but no longer one to rest a reassuring claim on.</param>
/// <param name="LicenseCode">The SPDX licence the source's data is used under, when recorded (14.1).</param>
public sealed record TraitSourceServiceModel(
    Guid ReferenceSourceId,
    string Code,
    string Name,
    ReferenceSourceKind Kind,
    string Citation,
    bool IsActive = true,
    string? LicenseCode = null,
    string? LicenseUrl = null);

/// <summary>One approved allergen trait in effect on the date asked about.</summary>
/// <param name="EvidenceNote">What the source actually said. Required by the schema for the states a reader could mistake.</param>
public sealed record ApprovedAllergenTraitServiceModel(
    Guid IngredientId,
    Guid AllergenId,
    AllergenPresence Presence,
    string EvidenceNote,
    DateOnly EffectiveFrom,
    TraitSourceServiceModel Source);

/// <summary>One approved dietary trait in effect on the date asked about.</summary>
public sealed record ApprovedDietaryTraitServiceModel(
    Guid IngredientId,
    Guid DietaryProfileId,
    DietaryCompatibility Compatibility,
    string EvidenceNote,
    DateOnly EffectiveFrom,
    TraitSourceServiceModel Source);

/// <summary>
/// The approved trait evidence about some ingredients for some allergens and dietary profiles (14.4). Records,
/// never a verdict: an ingredient, allergen or profile with nothing here is unknown.
/// </summary>
/// <remarks>
/// For each ingredient, subject and source, only that source's latest trait in effect on the date is returned —
/// a source revising itself supersedes its own earlier word, never another source's.
/// </remarks>
public sealed record IngredientTraitEvidenceServiceModel(
    IReadOnlyList<ApprovedAllergenTraitServiceModel> Allergens,
    IReadOnlyList<ApprovedDietaryTraitServiceModel> Dietary);
