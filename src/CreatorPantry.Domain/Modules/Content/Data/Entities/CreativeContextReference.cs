using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One record a piece of creative work draws on. Interior to <see cref="CreativeContext"/>: it names the
/// source, never copies it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One row, one kind, and only that kind's columns.</strong> <see cref="Kind"/> says which of the id
/// columns below are set; a check constraint holds every other one null, so a row cannot name a recipe and an
/// image at once and a reader never has to guess which id is the real one.
/// </para>
/// <para>
/// <strong>Workspace-paired where the target has a table.</strong> Each id is half of a composite foreign key
/// led by <see cref="WorkspaceId"/>, so a context naming another workspace's recipe or picture is
/// unrepresentable rather than merely refused by the write seam. Every key is restricted: nothing here deletes
/// a source, and the workspace's own cascade is what removes both rows.
/// </para>
/// <para>
/// <strong>What "the target is gone" means.</strong> None of these targets is row-deleted by ordinary code — a
/// recipe is archived, a DAM asset is tombstoned, a staged image outlives its bytes, a prompt record is
/// write-once. So a reference never points at nothing; it points at something no longer usable. The row stays
/// and the context still loads. Whether the target is still usable is answered when it is read, through the
/// owning module's facade, and never stored here: a flag would be a second copy of a fact the owner already
/// holds.
/// </para>
/// <para>
/// <strong>The two exceptions are stated, not hidden.</strong> <see cref="ConceptId"/> has no key because a
/// concept is not a row — it is a target inside its request's proposal. <see cref="SocialPackageId"/> has none
/// because its table arrives with AF.6.1, the path <c>PromptRecord.GeneratedImageId</c> took before 12.6. For
/// both, the write seam must resolve the id inside the workspace before storing it.
/// </para>
/// </remarks>
public class CreativeContextReference : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid CreativeContextId { get; set; }

    public CreativeContextReferenceKind Kind { get; set; }

    /// <summary>Position among this context's references, unique within the context.</summary>
    public int SortOrder { get; set; }

    /// <summary>Set exactly when <see cref="Kind"/> is <see cref="CreativeContextReferenceKind.Recipe"/>.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>
    /// The version the creator pinned, or null to name the recipe alone. The composite foreign key constrains it
    /// to a version <em>of that recipe</em>.
    /// </summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>
    /// The concept request — an AI operation. Set, with <see cref="ConceptId"/>, exactly when
    /// <see cref="Kind"/> is <see cref="CreativeContextReferenceKind.RecipeConcept"/>.
    /// </summary>
    public Guid? ConceptRequestId { get; set; }

    /// <summary>The chosen concept inside that request. Not a foreign key: a concept is not a row.</summary>
    public Guid? ConceptId { get; set; }

    /// <summary>Set exactly when <see cref="Kind"/> is <see cref="CreativeContextReferenceKind.DamAsset"/>.</summary>
    public Guid? MediaAssetId { get; set; }

    /// <summary>The version the creator pinned, or null to follow whichever is current.</summary>
    public int? MediaAssetVersionNumber { get; set; }

    /// <summary>Set exactly when <see cref="Kind"/> is <see cref="CreativeContextReferenceKind.GeneratedImage"/>.</summary>
    public Guid? GeneratedImageId { get; set; }

    /// <summary>Set exactly when <see cref="Kind"/> is <see cref="CreativeContextReferenceKind.PromptRecord"/>.</summary>
    public Guid? PromptRecordId { get; set; }

    /// <summary>
    /// Set exactly when <see cref="Kind"/> is <see cref="CreativeContextReferenceKind.SocialPackage"/>. No
    /// foreign key until AF.6.1 lands the table.
    /// </summary>
    public Guid? SocialPackageId { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
