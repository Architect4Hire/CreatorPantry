namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Where a recipe sits in the creator's own editorial workflow.
/// </summary>
/// <remarks>
/// <para>
/// Editorial state only. It says nothing about whether the recipe has been delivered to any external
/// provider: content.md is explicit that status does not imply publication success, and provider delivery
/// state lives on <c>Publication</c> records that do not exist yet. <see cref="Ready"/> means the creator
/// considers the recipe finished, not that anything has been posted anywhere.
/// </para>
/// <para>
/// The numeric values are what existing rows store, so renumbering one silently changes the state every
/// recipe already written is in. Append new states at the end rather than renumbering. Zero is
/// <see cref="Draft"/> deliberately: an unset status meaning "being written" is the right default, unlike
/// <see cref="RecipeAssetRole"/> where zero would be a claim.
/// </para>
/// </remarks>
public enum RecipeStatus
{
    /// <summary>Being written. The default for a newly created recipe.</summary>
    Draft = 0,

    /// <summary>The creator considers it finished. Not a publication claim.</summary>
    Ready = 1,

    /// <summary>Retired from the working set. Still readable, and its versions remain intact.</summary>
    Archived = 2,
}
