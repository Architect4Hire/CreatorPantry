namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// The kinds of external evidence a reference fact can rest on (14.1). Each is approved — or not — on its own
/// account: a source vetted for one says nothing about another.
/// </summary>
/// <remarks>
/// Not persisted. Numbered from 1 so that an unset value is never mistaken for nutrition.
/// </remarks>
public enum ReferenceEvidenceKind
{
    Nutrition = 1,

    /// <summary>A mass for a stated volume of one food — what crossing mass and volume needs (recipes.md).</summary>
    Density = 2,

    Allergen = 3,

    Dietary = 4,

    FoodSafety = 5,
}

/// <summary>Whether evidence of one kind can be had on this host (14.5). The cross-module face of the gateway's coverage.</summary>
public enum ReferenceEvidenceAvailability
{
    /// <summary>An approved source covers the kind, and this host can reach it.</summary>
    Available = 1,

    /// <summary>An approved source covers the kind, but this host has no provider for it.</summary>
    NotConfigured = 2,

    /// <summary>Nothing is approved for the kind. Its evidence is unknown on every host.</summary>
    NoApprovedSource = 3,
}

/// <param name="SourceCode">The approved source's code, when there is one.</param>
public sealed record ReferenceEvidenceCoverageServiceModel(
    ReferenceEvidenceKind Kind, ReferenceEvidenceAvailability Availability, string? SourceCode);

/// <summary>
/// The nutrients CreatorPantry reads from a reference source, in its own terms rather than a provider's numbering.
/// </summary>
/// <remarks>
/// <para>
/// A closed set on purpose: the label nutrients an estimate is built from. A nutrient a source reports that is
/// not here is dropped at the boundary, not carried as an unnamed number nothing downstream knows how to read.
/// </para>
/// <para>
/// Each has one unit (<see cref="EvidencePolicy.UnitOf"/>). A source that reports one in a different unit has
/// that value dropped rather than converted: a unit disagreement is a sign of reading the wrong row, and no
/// arithmetic makes that right.
/// </para>
/// </remarks>
public enum NutrientCode
{
    /// <summary>Energy, in kilocalories.</summary>
    Energy = 1,
    Protein = 2,
    TotalFat = 3,
    SaturatedFat = 4,
    TransFat = 5,
    Cholesterol = 6,

    /// <summary>Carbohydrate by difference.</summary>
    Carbohydrate = 7,
    DietaryFiber = 8,
    TotalSugars = 9,
    AddedSugars = 10,
    Sodium = 11,
    Potassium = 12,
    Calcium = 13,
    Iron = 14,
    VitaminD = 15,
}

public enum NutrientUnit
{
    Kilocalorie = 1,
    Gram = 2,
    Milligram = 3,
    Microgram = 4,
}
