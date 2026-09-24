namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/{versionNumber}/restore</c>: a
/// creator putting a recipe back to what one of its versions said.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which version is not a field.</strong> It is the route's own segment, so this body cannot name a
/// version any more than it can name a recipe or a workspace — and a restore is a command against a version,
/// which is where a version belongs in a URL.
/// </para>
/// <para>
/// <strong>Nothing about the recipe is here either.</strong> A restore is not a partial edit: the content
/// comes entirely from the archive, so there is no field to submit, leave alone or clear, and no
/// <c>PatchField</c> equivalent to reach for. That is the whole difference from
/// <see cref="UpdateRecipeViewModel"/>, and it is what makes "AI or a client cannot smuggle a content change
/// into a restore" structural rather than a rule.
/// </para>
/// </remarks>
public sealed record RestoreRecipeVersionViewModel
{
    /// <summary>
    /// The <c>concurrencyToken</c> from the recipe as the creator last saw it. Required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same value, the same name and the same meaning as
    /// <see cref="UpdateRecipeViewModel.ExpectedConcurrencyToken"/> — round-tripped from
    /// <see cref="RecipeDetailServiceModel.ConcurrencyToken"/>, opaque, stored and sent back rather than
    /// parsed. One concurrency contract for the whole module, and not a second one: this is the value the
    /// save itself quotes in its <c>WHERE</c> clause, so it is the only check the database can enforce.
    /// </para>
    /// <para>
    /// It is what makes this the recoverable conflict the requirement asks for. A restore composed against a
    /// state the recipe has moved past is refused with <c>409</c> rather than quietly discarding whatever a
    /// collaborator saved in between — and because the creator's decision was "put it back to version 3", not
    /// a body of prose, nothing of theirs is lost by re-reading and asking again.
    /// </para>
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }

    /// <summary>
    /// Why the creator restored this version, in their own words. Optional, and recorded on the version the
    /// restore writes.
    /// </summary>
    /// <remarks>
    /// Optional even here, where a reason is more interesting than on a routine save, because
    /// <c>RecipeVersion.Reason</c> is documented as the creator's words and a sentence the server invented
    /// would be untranslatable English in the domain. What the history records without it is not nothing:
    /// the version's source says a restore happened, and <c>restoredFromVersionId</c> says what it restored.
    /// </remarks>
    public string? Reason { get; init; }
}
