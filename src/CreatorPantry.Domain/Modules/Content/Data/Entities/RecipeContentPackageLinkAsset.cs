using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One picture in a recipe package, at one exact version. Write-once with its link.
/// </summary>
public class RecipeContentPackageLinkAsset : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeContentPackageLinkId { get; set; }

    public Guid MediaAssetId { get; set; }

    /// <summary>Always pinned: "the picture as it stands" is not something a package can be approved against.</summary>
    public int MediaAssetVersionNumber { get; set; }

    public int SortOrder { get; set; }
}
