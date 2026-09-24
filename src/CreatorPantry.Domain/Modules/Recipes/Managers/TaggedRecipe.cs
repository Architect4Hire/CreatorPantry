using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A loaded aggregate together with the vocabulary rows its tag links point at. What the DataLayer composes
/// for a read that has to show tags by name.
/// </summary>
/// <remarks>
/// <para>
/// It takes two reads because <see cref="RecipeTag"/> deliberately has no navigation to
/// <see cref="WorkspaceTag"/> — it is a join row inside the recipe aggregate, and the vocabulary it points at
/// is a root of its own with a different lifetime. Rather than add a navigation to the aggregate purely to
/// serve one projection, the DataLayer does what it exists to do and composes the two repository calls.
/// </para>
/// <para>
/// <see cref="Tags"/> may be shorter than the recipe's tag links. Deleting a tag that a recipe still carries
/// is refused by the database, so the only way to lose one is for a link to be removed and its tag deleted
/// between the two reads — a race whose honest outcome is a recipe displayed with one fewer tag, not a failed
/// read of the creator's content.
/// </para>
/// </remarks>
/// <param name="DuplicatedFrom">
/// Where this recipe was copied from, when it was created by duplicating another, and <c>null</c> otherwise.
/// Resolved from <see cref="Recipe.DuplicatedFromVersionId"/> by a third read that runs only for copies.
/// </param>
/// <remarks>
/// <para>
/// <strong>Defaulted, and that is load-bearing.</strong> Every path that answers with a recipe detail
/// reconstructs this record after a write, and each one must carry forward what it did not change. Using
/// <c>with</c> expressions rather than a fresh constructor call is what makes a member added here survive
/// those paths without each of them being edited — see <c>RecipeBusiness.UpdateAsync</c>.
/// </para>
/// </remarks>
public sealed record TaggedRecipe(
    CompleteRecipe Recipe,
    IReadOnlyList<WorkspaceTag> Tags,
    RecipeDuplicateSourceRecord? DuplicatedFrom = null);
