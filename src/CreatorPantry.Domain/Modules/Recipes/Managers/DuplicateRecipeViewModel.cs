namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/duplicate</c>: a creator making
/// an independent copy of one of their recipes to take in a different direction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No concurrency token, and this is the only recipe write without one.</strong> Nothing is being
/// overwritten — the source is read and a new recipe is written — so there is no prior state for the request
/// to have been composed against and no conflict to report. Asking for a token here would be ceremony that
/// could only ever refuse a request that was going to succeed.
/// </para>
/// <para>
/// <strong>No content fields either.</strong> Everything but the title comes from the source version, so
/// there is nothing to submit and no way for a request — or a model — to smuggle content into a copy.
/// Editing the copy afterwards is an ordinary <c>PATCH</c> against a recipe that is now the creator's own.
/// </para>
/// </remarks>
public sealed record DuplicateRecipeViewModel
{
    /// <summary>The copy's title. Required.</summary>
    /// <remarks>
    /// Required rather than defaulted, because the alternative is the server inventing English — "Copy of …"
    /// is a phrase no part of the domain can localize, and the create path already refuses to invent a
    /// version reason for the same reason. It also means the copy is never momentarily indistinguishable
    /// from its source in the creator's own library.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>
    /// Which of the source recipe's versions to copy, by number as the history lists it. Optional; omitting
    /// it copies the recipe as it currently stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version <em>number</em> rather than an id, matching the comparison and restore routes: a number is
    /// what a creator reads in a history and cites, and it is unique within its recipe, so a number naming
    /// another recipe's version matches nothing here rather than being refused by a check.
    /// </para>
    /// <para>
    /// Omitting it is not a different operation. "As it currently stands" resolves to the recipe's current
    /// version, and the copy is made from that version's archived document exactly as a named one would be —
    /// which is safe because every change that alters a recipe writes a version, so the current version's
    /// snapshot <em>is</em> the live content. One code path, and lineage that always names a real version.
    /// </para>
    /// </remarks>
    public int? SourceVersionNumber { get; init; }
}
