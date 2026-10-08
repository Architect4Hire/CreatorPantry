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
/// <see cref="Step"/> is the one role that names something besides the recipe: the instruction step the image
/// belongs to (12.10i). That is a second foreign key into the instruction tree, so it is <c>Restrict</c> rather
/// than cascading — the recipe already cascades into these rows, and a second cascade path is what SQL Server
/// refuses. Removing a step therefore never removes its image: the edit demotes the link to
/// <see cref="Process"/>, a recipe-level in-progress image, so a picture a creator chose is not lost because
/// they reworded their method.
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

    /// <summary>
    /// An image made for a social channel: cropped, titled or composed for a feed rather than for the recipe page.
    /// </summary>
    Social = 4,

    /// <summary>
    /// An image belonging to one numbered instruction step. The link names the step in
    /// <c>RecipeAssetLink.InstructionStepId</c>, and <c>CK_RecipeAssetLinks_Step_Paired</c> refuses either
    /// without the other.
    /// </summary>
    Step = 5,
}
