namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>
/// What kind of food-domain caution a template's output is subject to. One member per distinct caution in
/// <c>.claude/rules/ai.md</c>, so a later enforcement rule has exactly one class to hang off.
/// </summary>
/// <remarks>
/// Declared here and validated at load; nothing acts on it yet. The structured-output validator and the
/// individual capabilities are what turn a class into a requirement — this phase only guarantees that every
/// template has stated which one applies, deliberately rather than by omission.
/// </remarks>
public enum PromptSafetyClass
{
    /// <summary>
    /// Not declared. Never valid on a loaded template: it is the zero value so that a manifest which omits
    /// <c>safetyClass</c> fails the load instead of defaulting to the least restrictive class.
    /// </summary>
    Unspecified = 0,

    /// <summary>No food-domain claim is possible from this output — editorial scheduling, SEO titles.</summary>
    None = 1,

    /// <summary>Taste, texture, and technique. Culinary plausibility only; never a safety claim.</summary>
    CulinaryAdvice = 2,

    /// <summary>
    /// Touches dietary tags or allergens. Descriptive metadata only — absence of data is never absence of an
    /// allergen, and no output may read as a guarantee.
    /// </summary>
    DietaryOrAllergen = 3,

    /// <summary>Produces nutrition figures, which must be labelled an estimate and record their method.</summary>
    NutritionEstimate = 4,

    /// <summary>
    /// Preservation, canning, fermentation, internal temperature, or medical suitability. Requires a vetted
    /// reference or an explicit caution.
    /// </summary>
    FoodSafety = 5,
}
