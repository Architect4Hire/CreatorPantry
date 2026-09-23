using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A recipe loaded with every one of its children, plus the metadata of its most recent version. The unit a
/// repository returns when the caller needs the whole aggregate rather than a row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The type exists to make a claim checkable.</strong> A bare <see cref="Recipe"/> loaded without its
/// <c>Include</c>s is indistinguishable from a recipe that genuinely has no ingredients: both have empty
/// collections. That ambiguity is harmless in a read and catastrophic in a capture, where it archives an
/// empty recipe into an immutable version that can never be corrected and whose emptiness nobody will notice
/// until they try to restore it. <c>RecipeSnapshotMapper.Capture</c> therefore takes this type and not a
/// <see cref="Recipe"/>.
/// </para>
/// <para>
/// <strong>What it is not.</strong> Everything lives in one assembly, so this is not unforgeable — anything
/// in the domain could construct one around a half-loaded recipe. What it does is move the requirement out
/// of a comment and into a signature, so that doing the wrong thing takes a deliberate act with a name that
/// says what is being claimed. That is the whole of the guarantee, and overstating it would be worse than
/// not having it.
/// </para>
/// <para>
/// <see cref="CurrentVersion"/> carries metadata only: the version row, never its snapshot document. Loading
/// a recipe must not drag its archive along, which is the reason the two live in separate tables.
/// </para>
/// </remarks>
public sealed class CompleteRecipe
{
    /// <param name="recipe">A recipe whose child collections have all been loaded.</param>
    /// <param name="currentVersion">
    /// The highest-numbered version of that recipe, or <c>null</c> when it has no history yet.
    /// </param>
    public CompleteRecipe(Recipe recipe, RecipeVersion? currentVersion)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        Recipe = recipe;
        CurrentVersion = currentVersion;
    }

    public Recipe Recipe { get; }

    public RecipeVersion? CurrentVersion { get; }
}
