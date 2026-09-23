namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Whether a captured version is a working snapshot or one the creator has declared finished.
/// </summary>
/// <remarks>
/// <para>
/// Editorial only, like <see cref="RecipeStatus"/>, and for the same reason: content.md keeps provider
/// delivery state on publication records. <see cref="Ready"/> means the creator considers this version
/// usable as a source, never that it has been published anywhere.
/// </para>
/// <para>
/// Zero is <see cref="Draft"/> deliberately. An unset readiness meaning "not declared finished" is the
/// conservative reading — the opposite of <see cref="RecipeAssetRole"/>, where zero would have asserted that
/// an image was the hero. A default that under-claims is safe; one that over-claims is not.
/// </para>
/// </remarks>
public enum RecipeVersionReadiness
{
    /// <summary>Captured while the recipe was being worked on. Immutable, but not declared finished.</summary>
    Draft = 0,

    /// <summary>The creator declared this version finished and fit to be used as a source.</summary>
    Ready = 1,
}
