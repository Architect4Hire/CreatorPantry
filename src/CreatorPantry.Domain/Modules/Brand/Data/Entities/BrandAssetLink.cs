using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One use of a media asset as part of the brand. Interior to <see cref="BrandProfile"/>: it records the
/// usage, never the asset.
/// </summary>
/// <remarks>
/// <see cref="MediaAssetId"/> has <strong>no foreign key</strong> until the media aggregate exists, exactly
/// like <c>RecipeAssetLink</c>. The key that eventually lands must be composite —
/// <c>(WorkspaceId, MediaAssetId) -> MediaAssets (WorkspaceId, Id)</c> — and until then the write seam must
/// validate the id against the resolved workspace through the media facade. Deleting a link never deletes the
/// asset (media.md).
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
