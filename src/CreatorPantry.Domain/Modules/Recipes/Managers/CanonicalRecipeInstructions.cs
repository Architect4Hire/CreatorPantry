namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One instruction group reduced to its meaning: text trimmed, blanks resolved to null, order preserved
/// exactly as submitted. Shared by <see cref="CanonicalCreateRecipe"/> and <see cref="CanonicalRecipePatch"/>
/// for the reason <see cref="RecipeInstructionGroupInputViewModel"/> is: only what <see cref="Id"/> means differs
/// between a create and an edit, and Business is where that difference is applied.
/// </summary>
/// <remarks>
/// Order is not sorted the way <see cref="RecipeTagName"/> is. A recipe's tags are a set; its method is not —
/// array position <em>is</em> the creator's intended <c>SortOrder</c>, so two requests that list the same
/// steps in a different order are two different requests, not one.
/// </remarks>
public sealed record CanonicalInstructionGroup
{
    /// <inheritdoc cref="RecipeInstructionGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    public string? Title { get; init; }

    public required IReadOnlyList<CanonicalInstructionStep> Steps { get; init; }
}

/// <inheritdoc cref="CanonicalInstructionGroup"/>
public sealed record CanonicalInstructionStep
{
    /// <inheritdoc cref="RecipeInstructionGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    public required string Text { get; init; }

    public Guid? TechniqueId { get; init; }

    public int? DurationMinutes { get; init; }

    public decimal? TemperatureValue { get; init; }

    public Guid? TemperatureUnitId { get; init; }

    public string? Note { get; init; }
}

/// <summary>Canonicalization shared by <see cref="CanonicalCreateRecipe"/> and <see cref="CanonicalRecipePatch"/>.</summary>
internal static class CanonicalInstructions
{
    public static IReadOnlyList<CanonicalInstructionGroup> From(IReadOnlyList<RecipeInstructionGroupInputViewModel?>? groups)
    {
        if (groups is null)
        {
            return [];
        }

        var result = new List<CanonicalInstructionGroup>(groups.Count);

        foreach (var group in groups)
        {
            if (group is null)
            {
                continue;
            }

            result.Add(new CanonicalInstructionGroup
            {
                Id = group.Id,
                Title = Text(group.Title),
                Steps = FromSteps(group.Steps),
            });
        }

        return result;
    }

    private static IReadOnlyList<CanonicalInstructionStep> FromSteps(IReadOnlyList<RecipeInstructionStepInputViewModel?>? steps)
    {
        if (steps is null)
        {
            return [];
        }

        var result = new List<CanonicalInstructionStep>(steps.Count);

        foreach (var step in steps)
        {
            if (step is null)
            {
                continue;
            }

            result.Add(new CanonicalInstructionStep
            {
                Id = step.Id,
                Text = Text(step.Text) ?? string.Empty,
                TechniqueId = step.TechniqueId,
                DurationMinutes = step.DurationMinutes,
                TemperatureValue = step.TemperatureValue,
                TemperatureUnitId = step.TemperatureUnitId,
                Note = Text(step.Note),
            });
        }

        return result;
    }

    /// <summary>Trims, and collapses what is left of an empty field to <c>null</c>.</summary>
    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
