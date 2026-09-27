namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract AIREC-001's answer must satisfy: a set of distinct recipe concepts pitched from a creator's
/// structured brief, not a diff against an existing recipe. This type <em>is</em> the schema —
/// <see cref="AiConceptOutputSchema"/> exports it for the prompt — and the answer is held to it by strict
/// deserialization, the same guarantee <see cref="AiOutputDocument"/> makes for the recipe-diff capabilities.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no nutrition or allergen-guarantee field anywhere in this type, and there will not be.</strong>
/// ai.md forbids presenting a fabricated safety or nutrition claim, and a model that tried to add one —
/// <c>"allergenFree": true</c>, say — fails strict shape validation as an unknown field rather than being
/// silently stored or silently dropped. <see cref="AiRecipeConcept.DietaryNotes"/> exists for the one thing
/// that is safe to say: a descriptive, non-guaranteed observation.
/// </para>
/// <para>
/// Nothing here is persisted verbatim as a unit. <see cref="RecipeConceptsAiTaskHandler"/> translates a
/// validated document into <c>AiStructuredChange</c> rows against a server-minted id per concept, the same
/// storage the recipe-diff capabilities use — never <see cref="AiDiffCalculator"/>, which has no pinned recipe
/// to resolve this against.
/// </para>
/// </remarks>
public sealed record AiConceptOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Two to five distinct recipe pitches. See <see cref="AiPolicy.MinConceptCount"/>.</summary>
    public required IReadOnlyList<AiRecipeConcept> Concepts { get; init; }

    /// <summary>What the creator should be told alongside the concepts, including assumptions.</summary>
    public IReadOnlyList<AiConceptOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One recipe concept: a pitch, not a finished recipe and not a canonical source.</summary>
public sealed record AiRecipeConcept
{
    public required string Title { get; init; }

    public required string Summary { get; init; }

    /// <summary>What makes this concept different from the others returned alongside it.</summary>
    public required string DistinctnessRationale { get; init; }

    /// <summary>Where the brief was silent, vague, or partly conflicting, what the model took as given.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    /// <summary>Short phrases naming key ingredients — a pitch, never a formal ingredient list.</summary>
    public IReadOnlyList<string> SuggestedIngredients { get; init; } = [];

    /// <summary>
    /// Descriptive, non-guaranteed dietary observations only — e.g. "reads as vegetarian based on the
    /// ingredients suggested." Never a guarantee about allergens, safety, or nutrition (ai.md).
    /// </summary>
    public IReadOnlyList<string> DietaryNotes { get; init; } = [];

    /// <summary>A qualitative note on fit within the creator's stated time budget. Never an exact duration.</summary>
    public string? TimeBudgetNote { get; init; }

    /// <summary>A qualitative note on fit with the creator's stated skill level.</summary>
    public string? SkillLevelFit { get; init; }
}

/// <summary>One thing to tell the creator about the answer as a whole, or about one concept.</summary>
public sealed record AiConceptOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// The concept this is about, as an index into <see cref="AiConceptOutputDocument.Concepts"/>. Null for a
    /// warning about the answer as a whole — for instance, that no concept could satisfy a contradictory brief.
    /// </summary>
    public int? ConceptIndex { get; init; }
}
