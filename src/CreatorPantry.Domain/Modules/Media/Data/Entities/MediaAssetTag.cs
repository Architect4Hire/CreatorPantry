using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One asset carrying one of the workspace's tags.
/// </summary>
/// <remarks>
/// It links <c>WorkspaceTag</c>, the vocabulary the recipe library already uses, rather than introducing a
/// third tag table. Brand has its own <c>BrandSourceTag</c>, and that is the thing not to repeat: a
/// creator who tags a recipe "weeknight" and an asset "weeknight" means the same word, and two vocabularies
/// make that two words that merely look alike. The tag entity lives in the Recipes module; only its type
/// crosses, because a foreign key does.
/// </remarks>
public class MediaAssetTag : IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid MediaAssetId { get; set; }

    public Guid WorkspaceTagId { get; set; }
}
