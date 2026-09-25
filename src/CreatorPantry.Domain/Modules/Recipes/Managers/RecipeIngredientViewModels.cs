namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One ingredient group as a creator submits it, on a create or an edit. Shared by
/// <see cref="CreateRecipeViewModel"/> and <see cref="UpdateRecipeViewModel"/> rather than one type per
/// contract, for the reason <see cref="RecipeInstructionGroupInputViewModel"/> is: the shape a line takes
/// does not differ between the two — only what <see cref="Id"/> means does, and that difference is a
/// validator rule (a create refuses one; an edit reads it), not a second set of properties.
/// </summary>
public sealed record RecipeIngredientGroupInputViewModel
{
    /// <inheritdoc cref="RecipeInstructionGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    /// <summary>The heading, such as "For the streusel". Null for an unnamed group.</summary>
    public string? Title { get; init; }

    public IReadOnlyList<RecipeIngredientInputViewModel?>? Ingredients { get; init; }
}

/// <inheritdoc cref="RecipeIngredientGroupInputViewModel"/>
public sealed record RecipeIngredientInputViewModel
{
    /// <inheritdoc cref="RecipeInstructionGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    /// <summary>
    /// The line exactly as the creator writes it: "2 cups (240 g) all-purpose flour, sifted". Required, and
    /// canonical — recipes.md keeps this the recipe, whatever else on this line does or does not resolve.
    /// </summary>
    public string? DisplayText { get; init; }

    /// <summary>The parsed quantity, or the low end of a range. Null for "a pinch of salt", or when nothing has parsed it.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>The high end of a range: "2 to 3 tablespoons" submits 2 here and 3 there. Null for a single quantity.</summary>
    public decimal? QuantityUpper { get; init; }

    /// <summary>The recognised unit, or null when none was written or none was matched. Additive.</summary>
    public Guid? MeasurementUnitId { get; init; }

    /// <summary>
    /// The matched vocabulary ingredient, when one was recognised. Additive: it enriches the line and never
    /// replaces <see cref="DisplayText"/>, which stays exactly as submitted whether or not this is present.
    /// </summary>
    public Guid? IngredientId { get; init; }

    /// <summary>"finely chopped", "at room temperature" — the preparation the line asks for.</summary>
    public string? PreparationNote { get; init; }

    /// <summary>Whether the recipe works without this line.</summary>
    public bool? IsOptional { get; init; }

    /// <summary>How this line behaves when the recipe is scaled. Null defaults to <see cref="IngredientScaling.Proportional"/>.</summary>
    public IngredientScaling? ScalingBehavior { get; init; }
}
