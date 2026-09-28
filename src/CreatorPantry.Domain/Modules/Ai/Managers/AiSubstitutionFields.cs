namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The field names AIREC-004's substitutions are stored under, as <c>AiStructuredChange.FieldName</c>.
/// </summary>
/// <remarks>
/// A substitution is not stored as a substitution. <see cref="IngredientSubstitutionAiTaskHandler"/> flattens
/// each one into an <see cref="AiChangeKind.Add"/> row carrying the alternative's name plus an
/// <see cref="AiChangeKind.Set"/> row per other field, the same way <see cref="AiConceptFields"/> describes
/// for concepts — so these strings are the only thing tying a written field to a read one, and anything
/// reading a substitution back has to agree on them.
/// </remarks>
public static class AiSubstitutionFields
{
    public const string FunctionalRole = "functionalRole";

    public const string QuantityGuidance = "quantityGuidance";

    public const string TechniqueImpact = "techniqueImpact";

    public const string FlavorImpact = "flavorImpact";

    public const string TextureImpact = "textureImpact";

    /// <summary>
    /// The dietary consequences, joined for storage as <c>"conflicts with vegan; may conflict with kosher"</c>.
    /// </summary>
    /// <inheritdoc cref="AllergenEffects" path="/remarks"/>
    public const string DietaryEffects = "dietaryEffects";

    /// <summary>
    /// The allergen consequences, joined for storage as
    /// <c>"introduces tree nuts; may introduce soy"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flattened rather than stored structurally, because <c>AiStructuredChange</c> carries one
    /// <c>AfterValue</c> and nothing in this module stores a child payload. The direction survives the
    /// flattening, which is the part that matters: every phrase this can produce names an effect that runs one
    /// way, because <see cref="AiAllergenEffectKind"/> and <see cref="AiDietaryEffectKind"/> have no member
    /// that runs the other.
    /// </para>
    /// <para>
    /// Written as words rather than enum names. <c>"MayIntroduce soy"</c> is a machine token on the one field
    /// where being understood matters most, and every reader of it would otherwise have to translate it
    /// separately — which is how one of them comes to show a creator the raw token.
    /// </para>
    /// </remarks>
    public const string AllergenEffects = "allergenEffects";

    public const string Confidence = "confidence";

    public const string EvidenceBasis = "evidenceBasis";

    public const string EvidenceNote = "evidenceNote";

    public const string TestRecommendation = "testRecommendation";
}
