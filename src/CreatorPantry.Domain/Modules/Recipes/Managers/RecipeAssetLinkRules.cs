using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The rules a recipe's asset links obey that more than one write path has to keep (RCPUB-005).
/// </summary>
public static class RecipeAssetLinkRules
{
    /// <summary>
    /// Turns the step images of <paramref name="removedStepIds"/> into recipe-level in-progress images.
    /// </summary>
    /// <returns>Whether any link was changed.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Removing a step never removes its picture.</strong> A creator who rewrites their method —
    /// merging two steps, say — has not asked for a photograph to be unlinked, and the first they would know
    /// of it is that it had gone. So the link stays and stops claiming a step that is no longer there:
    /// <see cref="RecipeAssetRole.Process"/> is the same kind of image without a step to belong to.
    /// </para>
    /// <para>
    /// It is also what the schema requires. The step key on a link is <c>Restrict</c>, so a step with an
    /// image still pointing at it cannot be deleted at all; this runs first, in the same save.
    /// </para>
    /// <para>
    /// A demoted link keeps its position, its caption and its version pin. Two links can end up as the same
    /// asset in the same role — a step image demoted beside an existing process image of that asset — which
    /// is left alone: both were chosen, and neither write path has grounds to pick one to drop.
    /// </para>
    /// </remarks>
    public static bool DemoteStepImages(Recipe recipe, IReadOnlyCollection<Guid> removedStepIds)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(removedStepIds);

        var changed = false;

        foreach (var link in recipe.AssetLinks)
        {
            if (link.InstructionStepId is { } stepId && removedStepIds.Contains(stepId))
            {
                Demote(link);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Makes one step image a recipe-level in-progress image. Both columns move together, as the check constraint requires.</summary>
    public static void Demote(RecipeAssetLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        link.Role = RecipeAssetRole.Process;
        link.InstructionStepId = null;
    }
}
