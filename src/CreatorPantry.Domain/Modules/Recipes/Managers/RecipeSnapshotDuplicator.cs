using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Projects a snapshot document into a detached aggregate for a <em>different</em> recipe: the same content,
/// under freshly minted identities. Pure — no persistence, no clock, no workspace, no policy.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not <see cref="RecipeSnapshotMapper.Restore"/>.</strong> That method deliberately
/// keeps every archived id, because a restore puts content back where it came from and a diff has to be able
/// to match a line to itself. A duplicate is the opposite case: the ids are primary keys, so two recipes
/// cannot share them, and a copy that reused them would collide on insert — or, worse, be diffable against
/// the wrong recipe's history.
/// </para>
/// <para>
/// <strong>It borrows the mapper rather than reading the document.</strong> Everything about which document
/// field becomes which column lives in one place, so a field added to the archive is forgotten in one file
/// instead of three, and <c>RecipeSnapshotCompletenessTests</c> already watches that one. What is left here
/// is identity and nothing else.
/// </para>
/// <para>
/// <strong>Tags are dropped, deliberately.</strong> A tag link is a reference into the workspace's own
/// vocabulary rather than content of the recipe, and <c>RecipeDataLayer.CreateAsync</c> already owns
/// attaching tags to a new recipe — reusing existing rows, creating missing ones, inside the same
/// transaction, before the snapshot is captured. Letting this build the links instead would be a second tag
/// path for created recipes, and it would have to re-answer on its own whether each archived id still exists.
/// The caller resolves them and hands them to the create.
/// </para>
/// <para>
/// <strong>No policy is applied.</strong> The aggregate comes back carrying the archived title and status,
/// because deciding that a copy is a Draft called something new is a domain rule and belongs to Business.
/// <c>WorkspaceId</c> is left unset throughout, as on a create: the ownership interceptor stamps it from the
/// resolved context, and a duplicate that carried a workspace would be one that could place a copy in
/// another.
/// </para>
/// </remarks>
public static class RecipeSnapshotDuplicator
{
    /// <summary>
    /// Copies <paramref name="document"/> into a new aggregate belonging to <paramref name="recipeId"/>.
    /// </summary>
    /// <param name="document">The archived content to copy.</param>
    /// <param name="recipeId">The id the new recipe will have. Minted by the caller, like any create.</param>
    /// <returns>
    /// A detached aggregate with every child carrying a new identity, no tag links, and the archived title
    /// and status still in place for the caller to overrule.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The document carries no schema version, or one this build cannot read.
    /// </exception>
    public static Recipe Duplicate(RecipeSnapshotDocument document, Guid recipeId)
    {
        // Validates the schema version on the way through; see RecipeSnapshotDocument.EnsureReadable.
        var recipe = RecipeSnapshotMapper.Restore(document, recipeId);

        recipe.Id = recipeId;

        foreach (var group in recipe.IngredientGroups)
        {
            group.Id = Guid.NewGuid();

            foreach (var line in group.Ingredients)
            {
                line.Id = Guid.NewGuid();

                // Re-pointed at the group's new id, not the archived one. The line keeps its RecipeId, which
                // the mapper already set to the new recipe.
                line.RecipeIngredientGroupId = group.Id;
            }
        }

        foreach (var group in recipe.InstructionGroups)
        {
            group.Id = Guid.NewGuid();

            foreach (var step in group.Steps)
            {
                step.Id = Guid.NewGuid();
                step.RecipeInstructionGroupId = group.Id;
            }
        }

        foreach (var item in recipe.Equipment)
        {
            item.Id = Guid.NewGuid();
        }

        foreach (var link in recipe.AssetLinks)
        {
            // The link is new; the asset it names is not. Nothing about the asset is copied and no ownership
            // moves — RecipeAssetLink records a usage, and both recipes are in the one workspace that owns
            // the asset, so the copy references exactly the same media.
            link.Id = Guid.NewGuid();
        }

        recipe.Tags.Clear();

        return recipe;
    }
}
