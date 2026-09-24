namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What a restore needs in hand before it can decide anything: the recipe it will change, tracked and whole,
/// and the archived content it will put back.
/// </summary>
/// <remarks>
/// <para>
/// One unit rather than two calls, because the two reads answer one question and the second is worthless
/// without the first. It also collapses a query: the tracked read of the recipe <em>is</em> the existence
/// probe that the history and comparison paths have to run separately, so nothing here needs
/// <c>IRecipeRepository.ExistsAsync</c>.
/// </para>
/// <para>
/// <strong>Two kinds of absence, deliberately kept apart.</strong> A null unit means the recipe is not
/// visible in the resolved workspace — unknown and belonging-to-another-workspace alike, as everywhere else
/// (tenancy.md). A unit with a null <see cref="Snapshot"/> means the recipe is readable and has no such
/// version. Collapsing the two would tell a creator who mistyped a version number that their recipe does not
/// exist.
/// </para>
/// </remarks>
/// <param name="Recipe">
/// The aggregate, tracked and loaded whole, with the vocabulary rows naming the tags it currently carries.
/// Its <c>RowVersion</c> still holds the value the read returned, which is what the save will quote.
/// </param>
/// <param name="Snapshot">
/// The version to restore, or <c>null</c> when this recipe has no version with that number — or has one
/// carrying no archived document, which no write path produces and which means the same thing to a caller:
/// there is nothing here to restore.
/// </param>
public sealed record RecipeRestoreUnit(TaggedRecipe Recipe, RecipeVersionSnapshotRecord? Snapshot);

/// <summary>
/// Where a duplicated recipe came from, resolved: the version it copied and the recipe that version belongs
/// to, named so a reader can navigate to it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Recipe.DuplicatedFromVersionId</c> stores one column and this resolves it, which is the whole reason
/// storing the source recipe beside it was declined: a join cannot disagree with itself. The join is the
/// price, and it is a small one — it runs only for the recipes that are copies.
/// </para>
/// <para>
/// <see cref="RecipeTitle"/> is the source's title <em>now</em>, not what it was called when the copy was
/// made. That is deliberate: a creator renaming a recipe expects everything pointing at it to follow, and
/// freezing the name into the copy would leave a banner naming a recipe that no longer exists by that name.
/// The archived content the copy holds is unaffected either way — that was copied, not referenced.
/// </para>
/// </remarks>
public sealed record RecipeDuplicateSourceRecord(
    Guid VersionId,
    int VersionNumber,
    Guid RecipeId,
    string RecipeTitle);
