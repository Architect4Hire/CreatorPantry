using System.Globalization;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// Reads the recipe versions a long-form revision is pinned to, as the lines the claim scanner checks a
/// section's figures against (LF.3.2).
/// </summary>
/// <remarks>
/// Its own seam so the section writes depend on this one read and not on everything the recipes module's
/// facade can do.
/// </remarks>
public interface ILongFormClaimSourceReader
{
    /// <summary>
    /// The pinned versions, in pin order. A version that can no longer be read is left out: it contributes no
    /// facts, so a figure only it stated is reported rather than waved through.
    /// </summary>
    Task<IReadOnlyList<LongFormClaimRecipe>> ReadAsync(
        IReadOnlyList<LongFormRevisionRecipePin> pins, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <strong>The version that was pinned, not the recipe as it stands.</strong> Read through the recipes
/// module's facade by the ids the revision holds, under the workspace filter: a section is held to the recipe
/// the piece was planned against.
/// </para>
/// <para>
/// <strong>Every line, as entered.</strong> Ingredient display text and step text are the creator's own
/// words, and all of them are read — there is no cap here, because a figure in line sixty-one is still a
/// figure the recipe states.
/// </para>
/// </remarks>
internal sealed class LongFormClaimSourceReader(IRecipeFacade recipes) : ILongFormClaimSourceReader
{
    public async Task<IReadOnlyList<LongFormClaimRecipe>> ReadAsync(
        IReadOnlyList<LongFormRevisionRecipePin> pins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pins);

        var pinned = new List<LongFormClaimRecipe>(pins.Count);

        foreach (var pin in pins.OrderBy(pin => pin.SortOrder))
        {
            var snapshot = await recipes.GetSnapshotAsync(pin.RecipeId, pin.RecipeVersionId, cancellationToken);

            if (!snapshot.Succeeded)
            {
                continue;
            }

            var recipe = snapshot.Value!.Document;
            var lines = new List<string>();

            lines.AddRange(recipe.IngredientGroups
                .OrderBy(group => group.SortOrder)
                .SelectMany(group => group.Ingredients.OrderBy(line => line.SortOrder))
                .Select(line => line.DisplayText));
            lines.AddRange(recipe.InstructionGroups
                .OrderBy(group => group.SortOrder)
                .SelectMany(group => group.Steps.OrderBy(step => step.SortOrder))
                .Select(step => step.Text));

            if (!string.IsNullOrWhiteSpace(recipe.Recipe.YieldText))
            {
                lines.Add(recipe.Recipe.YieldText);
            }

            foreach (var minutes in new[]
                     {
                         recipe.Recipe.PrepTimeMinutes, recipe.Recipe.CookTimeMinutes,
                         recipe.Recipe.RestTimeMinutes, recipe.Recipe.TotalTimeMinutes,
                     })
            {
                if (minutes is { } value)
                {
                    lines.Add($"{value.ToString(CultureInfo.InvariantCulture)} minutes");
                }
            }

            pinned.Add(new LongFormClaimRecipe(pin.CreativeContextReferenceId, recipe.Recipe.Title, lines));
        }

        return pinned;
    }
}
