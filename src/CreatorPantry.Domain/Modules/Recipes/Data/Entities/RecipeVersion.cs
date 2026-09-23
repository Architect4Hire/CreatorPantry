using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One immutable point in a recipe's history: what it said, who captured it, why, and what it was derived
/// from. Workspace-owned creator intellectual property, and an aggregate root in its own right.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not a child of <see cref="Recipe"/>,</strong> though it references one. The six entities in the
/// recipe aggregate have no lifetime of their own and cascade away with it; a version is the opposite — it
/// must outlive the edits that supersede it, is read on its own (a history list, a diff, a restore), and has
/// an identity creators cite. So it points at the workspace directly, like a root does, and its reference to
/// the recipe is <c>Restrict</c>.
/// </para>
/// <para>
/// The consequence is deliberate and worth stating plainly: <strong>a recipe with versions cannot simply be
/// deleted.</strong> The database refuses it. Removing a recipe becomes an explicit operation that decides
/// what happens to the history first, which is what "do not cascade-delete immutable versions" has to mean
/// if it is to mean anything.
/// </para>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>, whatever code path arrives there. A correction is a new version, never an edit to this
/// row. Restoring an old version writes a new one too — history is appended to, never rewritten.
/// </para>
/// </remarks>
public class RecipeVersion : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>
    /// Sequential from 1 within one recipe, and unique there. A number creators refer to, so it is never
    /// reused even when a version is superseded.
    /// </summary>
    public int VersionNumber { get; set; }

    public RecipeVersionSource Source { get; set; }

    public RecipeVersionReadiness Readiness { get; set; }

    /// <summary>Why this version exists, in the creator's words. Optional; a routine save has no reason.</summary>
    public string? Reason { get; set; }

    /// <inheritdoc cref="Recipe.CreatedByMembershipId"/>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The version this one was derived from; null only for version 1. The lineage, kept as an explicit edge
    /// rather than inferred from <see cref="VersionNumber"/>, because a restore produces a version whose
    /// content came from an old one and whose parent is what it replaced — an ordering the numbers alone
    /// cannot express.
    /// </summary>
    public Guid? ParentVersionId { get; set; }

    /// <summary>
    /// The live recipe's concurrency token at the moment this snapshot was taken, tying the version to the
    /// exact row state it came from. Null when the originating state has no token to quote — a version
    /// captured as part of the same transaction that created the recipe.
    /// </summary>
    public byte[]? BasedOnRecipeRowVersion { get; set; }

    /// <summary>
    /// The AI generation whose proposal the creator accepted, when <see cref="Source"/> is
    /// <see cref="RecipeVersionSource.AiProposalAccepted"/>.
    /// </summary>
    /// <remarks>
    /// No foreign key: the generation aggregate does not exist yet. When it does, the constraint must be
    /// composite — <c>(WorkspaceId, AiProposalId)</c> against the generation's workspace-leading key, never
    /// on the id alone — for the same reason <see cref="RecipeAssetLink.MediaAssetId"/> carries that note.
    /// Until then the write seam validates it against the resolved workspace.
    /// </remarks>
    public Guid? AiProposalId { get; set; }

    /// <summary>
    /// The <see cref="RecipeSnapshotDocument.SchemaVersion"/> of the stored snapshot, denormalized here so a
    /// reader can tell whether it understands a document before loading it — and so a migration can find
    /// every row of an old shape without deserializing the whole archive.
    /// </summary>
    public int SnapshotSchemaVersion { get; set; }

    /// <summary>
    /// The content itself, in its own table. Separated because listing a recipe's history is the common read
    /// and loading a snapshot is the rare one: keeping the document out of this entity makes "do not drag
    /// the whole archive into a history list" structural rather than something every future query has to
    /// remember to project away.
    /// </summary>
    public RecipeVersionSnapshot? Snapshot { get; set; }
}
