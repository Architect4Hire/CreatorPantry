using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One recipe version a <see cref="LongFormRevision"/> was written against. Interior to the revision, and
/// immutable with it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A row per recipe, because the number varies.</strong> A blog article is usually about one recipe, a
/// newsletter issue about several (B-35), and a piece written from a brief about none. Rows rather than JSON so
/// the pin is a real foreign key to an immutable version, and so staleness can find every document written
/// against one.
/// </para>
/// <para>
/// <strong>It carries the recipe itself, not a pointer to the context's reference.</strong>
/// <see cref="CreativeContextReferenceId"/> records which reference this pin came from and is deliberately not
/// a foreign key: a context's reference rows can be removed, and a revision that pinned one must go on reading
/// exactly as it did.
/// </para>
/// </remarks>
public class LongFormRevisionRecipePin : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid LongFormRevisionId { get; set; }

    /// <summary>The reference's position when it was pinned, unique within the revision.</summary>
    public int SortOrder { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>The immutable version of that recipe the document was written against.</summary>
    public Guid RecipeVersionId { get; set; }

    /// <summary>The creative-context reference this pin was taken from. Not a foreign key; see the remarks.</summary>
    public Guid CreativeContextReferenceId { get; set; }
}
