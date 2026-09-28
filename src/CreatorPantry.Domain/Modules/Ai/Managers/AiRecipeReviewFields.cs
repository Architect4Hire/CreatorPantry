namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The field names AIREC-006's findings are stored under, as <c>AiStructuredChange.FieldName</c>.
/// </summary>
/// <remarks>
/// A finding is not stored as a finding. <see cref="RecipeReviewAiTaskHandler"/> flattens each one into an
/// <see cref="AiChangeKind.Add"/> row carrying its summary plus an <see cref="AiChangeKind.Set"/> row per other
/// field, the same way <see cref="AiSubstitutionFields"/> describes for AIREC-004 — so these strings are the
/// only thing tying a written field to a read one.
/// </remarks>
public static class AiRecipeReviewFields
{
    public const string Category = "category";

    public const string Severity = "severity";

    /// <summary>Which field kind the finding's <see cref="AiRecipeReviewFieldRef"/> named.</summary>
    public const string FieldKind = "fieldKind";

    /// <summary>
    /// The ingredient line, step, or equipment item id the finding named, when it named one.
    /// </summary>
    public const string FieldEntityId = "fieldEntityId";

    public const string Allergen = "allergen";

    public const string AllergenEffect = "allergenEffect";

    public const string Diet = "diet";

    public const string DietaryEffect = "dietaryEffect";

    public const string EvidenceBasis = "evidenceBasis";

    public const string EvidenceNote = "evidenceNote";

    public const string Confidence = "confidence";

    public const string RequiresReferenceCheck = "requiresReferenceCheck";

    /// <summary>
    /// The unknown factors, joined for storage as <c>"whether the cure time is adequate; the exact salt percentage"</c>.
    /// </summary>
    public const string UnknownFactors = "unknownFactors";
}
