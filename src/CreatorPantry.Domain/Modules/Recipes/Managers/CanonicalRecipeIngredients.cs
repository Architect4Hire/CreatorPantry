namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One ingredient group reduced to its meaning: text trimmed, blanks resolved to null, order preserved
/// exactly as submitted. Shared by <see cref="CanonicalCreateRecipe"/> and <see cref="CanonicalRecipePatch"/>
/// for the reason <see cref="RecipeIngredientGroupInputViewModel"/> is: only what <see cref="Id"/> means
/// differs between a create and an edit, and Business is where that difference is applied.
/// </summary>
/// <remarks>
/// Order is not sorted the way <see cref="RecipeTagName"/> is, for the same reason
/// <see cref="CanonicalInstructionGroup"/> is not: array position <em>is</em> the creator's intended
/// <c>SortOrder</c>, so two requests that list the same lines in a different order are two different
/// requests, not one.
/// </remarks>
public sealed record CanonicalIngredientGroup
{
    /// <inheritdoc cref="RecipeIngredientGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    public string? Title { get; init; }

    public required IReadOnlyList<CanonicalIngredientLine> Ingredients { get; init; }
}

/// <inheritdoc cref="CanonicalIngredientGroup"/>
public sealed record CanonicalIngredientLine
{
    /// <inheritdoc cref="RecipeIngredientGroupInputViewModel.Id"/>
    public Guid? Id { get; init; }

    /// <summary>Never trimmed away to nothing — recipes.md keeps this the creator's own wording.</summary>
    public required string DisplayText { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? QuantityUpper { get; init; }

    public Guid? MeasurementUnitId { get; init; }

    public Guid? IngredientId { get; init; }

    public string? PreparationNote { get; init; }

    public bool IsOptional { get; init; }

    public IngredientScaling ScalingBehavior { get; init; }
}

/// <summary>Canonicalization shared by <see cref="CanonicalCreateRecipe"/> and <see cref="CanonicalRecipePatch"/>.</summary>
internal static class CanonicalIngredients
{
    public static IReadOnlyList<CanonicalIngredientGroup> From(IReadOnlyList<RecipeIngredientGroupInputViewModel?>? groups)
    {
        if (groups is null)
        {
            return [];
        }

        var result = new List<CanonicalIngredientGroup>(groups.Count);

        foreach (var group in groups)
        {
            if (group is null)
            {
                continue;
            }

            result.Add(new CanonicalIngredientGroup
            {
                Id = group.Id,
                Title = Text(group.Title),
                Ingredients = FromLines(group.Ingredients),
            });
        }

        return result;
    }

    private static IReadOnlyList<CanonicalIngredientLine> FromLines(IReadOnlyList<RecipeIngredientInputViewModel?>? lines)
    {
        if (lines is null)
        {
            return [];
        }

        var result = new List<CanonicalIngredientLine>(lines.Count);

        foreach (var line in lines)
        {
            if (line is null)
            {
                continue;
            }

            result.Add(new CanonicalIngredientLine
            {
                Id = line.Id,
                DisplayText = Text(line.DisplayText) ?? string.Empty,
                Quantity = line.Quantity,
                QuantityUpper = line.QuantityUpper,
                MeasurementUnitId = line.MeasurementUnitId,
                IngredientId = line.IngredientId,
                PreparationNote = Text(line.PreparationNote),
                IsOptional = line.IsOptional ?? false,
                ScalingBehavior = line.ScalingBehavior ?? IngredientScaling.Proportional,
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
