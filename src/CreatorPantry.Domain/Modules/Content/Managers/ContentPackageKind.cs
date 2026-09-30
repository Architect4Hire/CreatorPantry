namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Which package a content proposal is about. One proposal per recipe and kind: alternatives are revisions,
/// not sibling proposals.
/// </summary>
/// <remarks>Stored as its number. Append new kinds; never renumber.</remarks>
public enum ContentPackageKind
{
    /// <summary>Headnote, introduction, tips, substitutions, storage/reheating, FAQ and call to action.</summary>
    Editorial = 0,

    /// <summary>SEO recommendations. Recommendations only; never invented keyword metrics.</summary>
    Seo = 1,
}
