using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// The stored content of one <see cref="RecipeVersion"/>: a complete, structured, self-describing document
/// from which the recipe at that moment can be reconstructed exactly.
/// </summary>
/// <remarks>
/// <para>
/// In its own table purely so that reading a version's metadata does not read its content. The model
/// enforces <em>at most</em> one snapshot per version — the foreign key is on this side, so a version with
/// no snapshot is representable and EF cannot express otherwise. Making a version without its content
/// impossible belongs to the write seam that creates the pair; until that exists, the pairing is a
/// convention rather than a constraint.
/// </para>
/// <para>
/// <see cref="Document"/> is mapped as an ordinary string for now. SQL Server 2025's native <c>json</c> type
/// is the right home for it and EF Core 10 supports it, but selecting it is a provider-specific choice that
/// belongs to the migration — see <c>RecipeVersionSnapshotConfiguration</c>.
/// </para>
/// <para>
/// Immutable along with its version. It is also workspace-owned in its own right rather than relying on its
/// parent for scope: the global query filter is applied per entity type, so a snapshot without its own
/// <see cref="WorkspaceId"/> would be readable across workspaces by anything that queried this table
/// directly.
/// </para>
/// <para>
/// The document is never interpreted here. It is written by <c>RecipeSnapshotSerializer</c> and read back
/// through it, and a reader must check <see cref="RecipeVersion.SnapshotSchemaVersion"/> before trusting the
/// shape of what it finds.
/// </para>
/// </remarks>
public class RecipeVersionSnapshot : IWorkspaceOwned, IImmutableRecord
{
    /// <summary>
    /// Shares its <see cref="RecipeVersion"/>'s identity, which is what makes a <em>second</em> snapshot for
    /// one version a primary key violation rather than a state someone has to check for.
    /// </summary>
    public Guid RecipeVersionId { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The serialized <c>RecipeSnapshotDocument</c>.</summary>
    public string Document { get; set; } = string.Empty;
}
