using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One use of a media asset as part of the brand. Interior to <see cref="BrandProfile"/>: it records the
/// usage, never the asset.
/// </summary>
/// <remarks>
/// <see cref="MediaAssetId"/> is workspace-paired to <c>MediaAsset</c> as of 12.9, exactly like
/// <c>RecipeAssetLink</c>: <c>(WorkspaceId, MediaAssetId) -> MediaAssets (WorkspaceId, Id)</c>, so a
/// profile showing another workspace's logo is unrepresentable. This is the one of the three links that
/// was already writable from client input, so it is the one whose stored ids that migration has to clear
/// before it can add the key. Deleting a link never deletes the asset (media.md).
/// </remarks>
public class BrandAssetLink : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandProfileId { get; set; }

    public Guid MediaAssetId { get; set; }

    public BrandAssetRole Role { get; set; }

    public int SortOrder { get; set; }
}
