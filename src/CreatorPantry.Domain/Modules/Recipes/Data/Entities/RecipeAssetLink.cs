using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One use of a media asset by a recipe. Interior to the <see cref="Recipe"/> aggregate: it records the
/// usage, never the asset.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MediaAssetId"/> deliberately has <strong>no foreign key</strong>. The asset aggregate does not
/// exist yet; it arrives with the media library, and its own migration adds the constraint. Until then this
/// is an unconstrained identifier, and the gap is recorded here rather than left to be discovered.
/// </para>
/// <para>
/// Nothing about the asset is copied here. Alt text in particular stays on the asset, where media.md keeps
/// it: duplicating it per usage creates a second copy that goes stale silently and an alt text that no
/// longer describes the pixels is worse than none. <see cref="Caption"/> is different — a caption is written
/// for this recipe and belongs to the usage.
/// </para>
/// <para>
/// Deleting a recipe cascades to these links and stops there. The asset itself is independently owned
/// creator property with its own retention rules, and no recipe delete may remove it.
/// </para>
/// </remarks>
public class RecipeAssetLink : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>Position among this recipe's linked assets, unique within the recipe.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The linked asset. Workspace-scoped by the link's own <see cref="WorkspaceId"/> today; constrained by a
    /// real foreign key once the asset aggregate lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things follow from the missing constraint, and both are easy to get wrong later.
    /// </para>
    /// <para>
    /// The foreign key that eventually lands must be composite —
    /// <c>(WorkspaceId, MediaAssetId) -> MediaAssets (WorkspaceId, Id)</c> — and never on
    /// <c>MediaAssetId</c> alone. This link's own workspace says nothing about the asset's, so a
    /// single-column key would happily point one workspace's recipe at another's photograph.
    /// </para>
    /// <para>
    /// Until then, this is the field a request body will carry, which makes it the one place in the
    /// aggregate where a client-supplied identifier reaches storage unchecked. The write seam must validate
    /// it against the resolved workspace through the media facade before persisting.
    /// </para>
    /// </remarks>
    public Guid MediaAssetId { get; set; }

    /// <summary>What the asset is doing here. At most one <see cref="RecipeAssetRole.Hero"/> per recipe.</summary>
    public RecipeAssetRole Role { get; set; }

    /// <summary>The caption written for this recipe. Not alt text, which belongs to the asset.</summary>
    public string? Caption { get; set; }
}
