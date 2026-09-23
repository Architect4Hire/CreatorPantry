namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One instruction group as a creator submits it, on a create or an edit. Shared by
/// <see cref="CreateRecipeViewModel"/> and <see cref="UpdateRecipeViewModel"/> rather than one type per
/// contract, because the shape a step takes does not differ between the two — only what <see cref="Id"/>
/// means does, and that difference is a validator rule (a create refuses one; an edit reads it), not a
/// second set of properties.
/// </summary>
public sealed record RecipeInstructionGroupInputViewModel
{
    /// <summary>
    /// Names the existing group this should become. Null stages a new group. Refused on a create, where
    /// nothing exists yet to name.
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>The heading, such as "For the streusel". Null for an unnamed group.</summary>
    public string? Title { get; init; }

    public IReadOnlyList<RecipeInstructionStepInputViewModel?>? Steps { get; init; }
}

/// <inheritdoc cref="RecipeInstructionGroupInputViewModel"/>
public sealed record RecipeInstructionStepInputViewModel
{
    /// <inheritdoc cref="RecipeInstructionGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    /// <summary>The step, exactly as the creator writes it. Required.</summary>
    public string? Text { get; init; }

    /// <summary>The shared <c>CookingTechnique</c> vocabulary this step performs, when recognised. Additive.</summary>
    public Guid? TechniqueId { get; init; }

    public int? DurationMinutes { get; init; }

    /// <summary>The temperature the step calls for, paired with <see cref="TemperatureUnitId"/>.</summary>
    public decimal? TemperatureValue { get; init; }

    /// <summary>The unit the temperature is expressed in. Must itself be a temperature unit.</summary>
    public Guid? TemperatureUnitId { get; init; }

    /// <summary>A creator's aside on this step — a tip, a warning, a doneness cue.</summary>
    public string? Note { get; init; }
}
