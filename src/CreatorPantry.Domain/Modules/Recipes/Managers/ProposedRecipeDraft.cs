namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A complete recipe a creator accepted from a generated draft, in this module's own vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The create-shaped sibling of <see cref="ProposedRecipeChange"/>.</strong> That type carries an
/// accepted change to a recipe that already exists; this carries a whole recipe that does not exist yet. Both
/// exist for the same reason: the AI module may not construct this module's ViewModels, so a purpose-built
/// input type owned here is what a caller fills in. Translating a proposal into it is the caller's job, and
/// nothing about that translation is this module's business.
/// </para>
/// <para>
/// <strong>Three things are absent because the recipe create contract has nowhere to put them</strong>, and
/// they are listed rather than quietly dropped, the way <see cref="ProposedRecipeTarget"/> lists its own:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>Equipment.</strong> <c>RecipeEquipment</c> is a real entity, but
/// <see cref="CreateRecipeViewModel"/> has no field for it, so a creator's own typed recipe cannot carry it
/// either. Giving a generated draft a route into a column a creator has no route into would be the more
/// surprising of the two.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>A yield unit.</strong> <c>Recipe.YieldUnitId</c> is a <c>MeasurementUnit</c> reference, and ai.md
/// forbids a model naming an identifier — a draft proposes the unit as free text. That text is the creator's
/// to reconcile afterwards in the editor; the proposed wording survives in <see cref="YieldText"/> meanwhile.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>A serving size.</strong> <c>CK_Recipes_ServingSize_RequiresYieldUnit</c> makes it meaningless
/// without a yield unit, and there is none. <see cref="ServingCount"/> is here because "serves 12" needs no
/// unit at all.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed record ProposedRecipeDraft
{
    /// <summary>Required. A recipe with no title is the one thing acceptance cannot work around.</summary>
    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Notes { get; init; }

    public int? PrepTimeMinutes { get; init; }

    public int? CookTimeMinutes { get; init; }

    public int? RestTimeMinutes { get; init; }

    /// <summary>
    /// Carried as proposed, and never summed from the other three.
    /// </summary>
    /// <remarks>
    /// recipes.md stores total time independently and says no write path sums it. A total computed here would
    /// be this seam inventing a number the creator never saw and the model never said.
    /// </remarks>
    public int? TotalTimeMinutes { get; init; }

    /// <summary>The yield exactly as phrased: "Makes 12 muffins". The canonical form (recipes.md).</summary>
    public string? YieldText { get; init; }

    /// <summary>The measured batch. Permitted without a unit — "makes 12" is how creators routinely write it.</summary>
    public decimal? YieldQuantity { get; init; }

    /// <summary>How many it serves. Unitless, and complete on its own.</summary>
    public decimal? ServingCount { get; init; }

    public required IReadOnlyList<ProposedRecipeDraftIngredientGroup> IngredientGroups { get; init; }

    public required IReadOnlyList<ProposedRecipeDraftInstructionGroup> Instructions { get; init; }
}

/// <param name="Title">
/// Null for the ungrouped case, which is the ordinary one: recipes.md makes grouping opt-in, and a recipe has
/// one ingredient list until the creator asks for sections.
/// </param>
public sealed record ProposedRecipeDraftIngredientGroup(
    string? Title,
    IReadOnlyList<ProposedRecipeDraftIngredient> Ingredients);

/// <param name="DisplayText">
/// The line as the creator accepted it — the model's wording, or their own rewrite of it.
/// </param>
/// <remarks>
/// Every accepted line is recorded as <see cref="IngredientDisplayTextSource.Creator"/>. A creator read this
/// line and let it stand, which makes it theirs; and recipes.md is explicit that a line marked
/// <see cref="IngredientDisplayTextSource.Composed"/> may be re-derived from its own spans, which would
/// rewrite text a creator approved. Recorded rather than inferred, for the reason that rule gives: a line that
/// happens to read exactly like its spans may still be one they meant to keep word for word.
/// </remarks>
public sealed record ProposedRecipeDraftIngredient
{
    public required string DisplayText { get; init; }

    /// <summary>The creator's own words for the ingredient's name. No id: a model may not name one.</summary>
    public string? IngredientNameText { get; init; }

    /// <summary>The unit as written — "cups", "large". No id, for the same reason.</summary>
    public string? UnitText { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? QuantityUpper { get; init; }

    public string? PreparationNote { get; init; }

    public bool IsOptional { get; init; }
}

/// <inheritdoc cref="ProposedRecipeDraftIngredientGroup"/>
public sealed record ProposedRecipeDraftInstructionGroup(
    string? Title,
    IReadOnlyList<ProposedRecipeDraftStep> Steps);

/// <remarks>
/// No temperature. <c>RecipeInstructionStep</c> stores a value against a unit id, and a model may not name
/// one — a bare number whose scale is unknown is exactly what recipes.md refuses to let anything record as a
/// safety-relevant fact.
/// </remarks>
public sealed record ProposedRecipeDraftStep(string Text, string? Note, int? DurationMinutes);
