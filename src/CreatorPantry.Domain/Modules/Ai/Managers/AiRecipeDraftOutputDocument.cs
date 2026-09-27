namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract AIREC-002's answer must satisfy: one complete structured recipe draft, not a diff against an
/// existing recipe. This type <em>is</em> the schema — <see cref="AiRecipeDraftOutputSchema"/> exports it for
/// the prompt — and the answer is held to it by strict deserialization, the same guarantee
/// <see cref="AiOutputDocument"/> makes for the recipe-diff capabilities and <see cref="AiConceptOutputDocument"/>
/// makes for AIREC-001.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No vocabulary identifier appears anywhere in this type, and none will be added.</strong> No
/// <c>measurementUnitId</c>, <c>ingredientId</c>, <c>techniqueId</c>, <c>equipmentTypeId</c>, or
/// <c>temperatureUnitId</c> — the same rule <see cref="AiDiffFields"/>'s own remarks state for the recipe-diff
/// capabilities: a model naming an identifier is a model inventing one, since it cannot know which row it
/// refers to. <see cref="AiRecipeDraftIngredientLine.UnitText"/> and
/// <see cref="AiRecipeDraftIngredientLine.IngredientNameText"/> are free text, resolved later by the same
/// deterministic matching an ordinary creator-typed line already goes through — never by this capability.
/// </para>
/// <para>
/// <strong>There is deliberately no structured temperature field anywhere on
/// <see cref="AiRecipeDraftInstructionStep"/>.</strong> A temperature value is meaningless without a unit, the
/// model cannot supply one (the same identifier problem above), and a value with an implied-but-unstated scale
/// is exactly the safety-critical fact ai.md and this capability's own RESTRICTION forbid inventing. A step
/// that calls for an oven temperature says so in <see cref="AiRecipeDraftInstructionStep.Text"/>, in prose —
/// the same way a creator-typed step already carries one until somebody fills in the structured field by hand.
/// </para>
/// <para>
/// <strong>Optional-as-a-whole content is a flag, not a separate list.</strong> An ingredient group, an
/// instruction group, and an equipment item each carry their own <c>IsOptional</c>, mirroring how a single
/// ingredient line already can be optional. There is no separate "optional components" structure: the
/// per-line flag already exists in the domain, and extending it to the group/item level covers the same idea
/// without inventing a second, parallel way to say it.
/// </para>
/// <para>
/// <strong>Warnings are whole-draft only.</strong> Unlike <see cref="AiConceptOutputDocument"/>'s
/// per-concept warnings, nothing here addresses one specific ingredient or step — see
/// <see cref="RecipeFirstDraftAiTaskHandler"/>'s remarks for why, and for the deliberate simplification that
/// is.
/// </para>
/// <para>
/// Nothing here is persisted verbatim as a unit. <see cref="RecipeFirstDraftAiTaskHandler"/> translates a
/// validated document into <c>AiStructuredChange</c> rows against server-minted ids, never
/// <see cref="AiDiffCalculator"/>, which has no pinned recipe to resolve this against.
/// </para>
/// </remarks>
public sealed record AiRecipeDraftOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    public required string Title { get; init; }

    /// <summary>A short summary for listings and cards.</summary>
    public string? Description { get; init; }

    public AiRecipeDraftYield? Yield { get; init; }

    public AiRecipeDraftTiming? Timing { get; init; }

    /// <summary>The recipe's ingredient list, in proposed order. At least one group, each with at least one line.</summary>
    public required IReadOnlyList<AiRecipeDraftIngredientGroup> IngredientGroups { get; init; }

    /// <summary>The recipe's method, in proposed order. At least one group, each with at least one step.</summary>
    public required IReadOnlyList<AiRecipeDraftInstructionGroup> Instructions { get; init; }

    public IReadOnlyList<AiRecipeDraftEquipmentItem> Equipment { get; init; } = [];

    /// <summary>The creator's working notes, proposed.</summary>
    public string? Notes { get; init; }

    /// <summary>What the model could not confidently resolve on its own and needs the creator to answer.</summary>
    public IReadOnlyList<string> UnresolvedQuestions { get; init; } = [];

    /// <summary>What the creator should be told alongside the draft, including assumptions.</summary>
    public IReadOnlyList<AiRecipeDraftWarning> Warnings { get; init; } = [];
}

/// <summary>
/// The yield's three separable facts (recipes.md), each optional independently. The unit is free text, never
/// a <c>MeasurementUnitId</c>.
/// </summary>
public sealed record AiRecipeDraftYield
{
    public string? YieldText { get; init; }

    public decimal? YieldQuantity { get; init; }

    public string? YieldUnitText { get; init; }

    public decimal? ServingCount { get; init; }

    public decimal? ServingSize { get; init; }
}

/// <summary>The four time fields, each independent — none is derived from the others.</summary>
public sealed record AiRecipeDraftTiming
{
    public int? PrepTimeMinutes { get; init; }

    public int? CookTimeMinutes { get; init; }

    public int? RestTimeMinutes { get; init; }

    public int? TotalTimeMinutes { get; init; }
}

/// <summary>One ingredient group: a heading and its lines. An untitled group is the recipe's only list.</summary>
public sealed record AiRecipeDraftIngredientGroup
{
    public string? Title { get; init; }

    /// <summary>Whether the whole group is skippable, not merely secondary to the main draw.</summary>
    public bool IsOptional { get; init; }

    public required IReadOnlyList<AiRecipeDraftIngredientLine> Ingredients { get; init; }
}

/// <summary>One ingredient line. Mirrors the fields a creator-typed line submits, minus every vocabulary id.</summary>
public sealed record AiRecipeDraftIngredientLine
{
    /// <summary>The line as it should read: "2 cups all-purpose flour, sifted". Required and canonical.</summary>
    public required string DisplayText { get; init; }

    /// <summary>The ingredient-name span, additive to <see cref="DisplayText"/>.</summary>
    public string? IngredientNameText { get; init; }

    /// <summary>The unit span as written, free text.</summary>
    public string? UnitText { get; init; }

    /// <summary>The parsed quantity, or the low end of a range. Null when nothing could be confidently parsed.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>The high end of a range. Null for a single quantity.</summary>
    public decimal? QuantityUpper { get; init; }

    public string? PreparationNote { get; init; }

    public bool IsOptional { get; init; }
}

/// <summary>One instruction group: a heading and its steps.</summary>
public sealed record AiRecipeDraftInstructionGroup
{
    public string? Title { get; init; }

    /// <inheritdoc cref="AiRecipeDraftIngredientGroup.IsOptional"/>
    public bool IsOptional { get; init; }

    public required IReadOnlyList<AiRecipeDraftInstructionStep> Steps { get; init; }
}

/// <summary>One instruction step. See the type-level remark for why there is no structured temperature field.</summary>
public sealed record AiRecipeDraftInstructionStep
{
    public required string Text { get; init; }

    /// <summary>A tip, a warning, a doneness cue.</summary>
    public string? Note { get; init; }

    public int? DurationMinutes { get; init; }
}

/// <summary>One piece of equipment the recipe calls for.</summary>
public sealed record AiRecipeDraftEquipmentItem
{
    public required string DisplayText { get; init; }

    public string? Note { get; init; }

    public bool IsOptional { get; init; }
}

/// <summary>One thing to tell the creator about the draft as a whole: an assumption, a caution, or an open question.</summary>
public sealed record AiRecipeDraftWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }
}
