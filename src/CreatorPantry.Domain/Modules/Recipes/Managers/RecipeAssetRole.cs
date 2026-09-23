namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What one linked media asset is doing on a recipe.
/// </summary>
/// <remarks>
/// <para>
/// The role belongs to the <em>link</em>, not to the asset. The same photograph can be one recipe's hero and
/// another's process shot, and media.md keeps the asset's own facts — alt text, rights, provenance — on the
/// asset itself so there is one place to correct them.
/// </para>
/// <para>
/// There is no step-scoped role yet. Linking an image to a single instruction step needs a second foreign
/// key into the instruction tree, which would give the same rows two cascade paths from the recipe; it is
/// designed in Phase 12 alongside the asset model it points at.
/// </para>
/// <para>
/// Numbering starts at 1, leaving zero — the value an unset <c>RecipeAssetRole</c> field holds — meaning
/// nothing. Were <see cref="Hero"/> zero, a link created without anyone choosing a role would become the
/// lead image by default, and the second such link would surface as a unique-index violation rather than as
/// the missing-field error it actually is. <c>CK_RecipeAssetLinks_Role_Specified</c> rejects zero outright.
/// </para>
/// <para>
/// The numeric value of <see cref="Hero"/> is named literally by
/// <c>UX_RecipeAssetLinks_Workspace_Recipe_Hero</c>. Do not renumber; append new roles at the end and update
/// that index in the same migration.
/// </para>
/// </remarks>
public enum RecipeAssetRole
{
    /// <summary>The single lead image. At most one per recipe, enforced by a filtered unique index.</summary>
    Hero = 1,

    /// <summary>A finished-dish image beyond the hero.</summary>
    Gallery = 2,

    /// <summary>An in-progress image belonging to the recipe as a whole rather than to one numbered step.</summary>
    Process = 3,
}
