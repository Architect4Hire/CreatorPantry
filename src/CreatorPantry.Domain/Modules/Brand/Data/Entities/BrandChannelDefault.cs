using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One channel the brand publishes to by default. Interior to <see cref="BrandProfile"/>.
/// </summary>
/// <remarks>
/// <see cref="ChannelKey"/> is opaque. Length, markup and image-ratio constraints belong to channel
/// profiles and adapters (content.md); the write seam validates keys against that registry once it exists.
/// </remarks>
public class BrandChannelDefault : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandProfileId { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
