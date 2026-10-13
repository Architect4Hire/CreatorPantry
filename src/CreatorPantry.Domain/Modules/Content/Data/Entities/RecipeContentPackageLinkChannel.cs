using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One channel a recipe package is for, with the post approved for it when there is one. Write-once with its
/// link.
/// </summary>
/// <remarks>
/// <see cref="ChannelKey"/> is an opaque key into the channel catalogue; a channel's limits belong to its
/// writing profile (content.md). A channel may be named with no post yet — the package then says where the work
/// is going without claiming words exist for it.
/// </remarks>
public class RecipeContentPackageLinkChannel : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeContentPackageLinkId { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    /// <summary>The package channel the post belongs to, with <see cref="SocialRevisionId"/>; both or neither.</summary>
    public Guid? SocialPackageChannelId { get; set; }

    /// <summary>The revision of that channel's post that was approved.</summary>
    public Guid? SocialRevisionId { get; set; }

    public int SortOrder { get; set; }
}
