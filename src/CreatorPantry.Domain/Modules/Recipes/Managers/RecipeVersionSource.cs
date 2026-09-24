namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What produced a <c>RecipeVersion</c>. Provenance, not permission: nothing here grants a version more
/// authority than another, and a version is not more trustworthy for having come from a human.
/// </summary>
/// <remarks>
/// <see cref="AiProposalAccepted"/> exists so that a version a model helped write stays identifiable after
/// the fact. ai.md requires generated output to be traceable to the generation that produced it, and a
/// version whose origin is only recoverable from a free-text reason is not traceable.
/// </remarks>
public enum RecipeVersionSource
{
    /// <summary>The creator edited the recipe directly.</summary>
    CreatorEdit = 0,

    /// <summary>
    /// The creator reviewed an AI proposal and accepted it. The proposal is named by
    /// <c>RecipeVersion.AiProposalId</c>; acceptance was a human act, and the model never writes a version.
    /// </summary>
    AiProposalAccepted = 1,

    /// <summary>The content arrived through an import adapter and was recorded as a version on arrival.</summary>
    Import = 2,

    /// <summary>
    /// An earlier version was restored. Restoring never rewrites history — it writes a <em>new</em> version
    /// whose content came from an old one, with <c>ParentVersionId</c> naming what it was restored over.
    /// </summary>
    Restore = 3,

    /// <summary>
    /// This is version 1 of a recipe created by duplicating another. The version it was copied from is named
    /// by <c>Recipe.DuplicatedFromVersionId</c> on the new recipe, not here: the lineage belongs to the
    /// recipe, which is the thing that is a copy, rather than to one of its versions.
    /// </summary>
    /// <remarks>
    /// Its own member rather than <see cref="CreatorEdit"/>, for the reason this enum exists at all. A
    /// duplicate's first version was not typed by anyone, and a history that called it a creator edit would
    /// be indistinguishable from a hand-written recipe unless a reader also thought to look at the recipe
    /// row. Provenance that only survives in a second table is not provenance.
    /// </remarks>
    Duplicate = 4,
}
