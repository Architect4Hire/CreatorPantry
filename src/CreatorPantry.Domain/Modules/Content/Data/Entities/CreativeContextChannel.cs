using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One channel a piece of creative work is for. Interior to <see cref="CreativeContext"/>.
/// </summary>
/// <remarks>
/// <see cref="ChannelKey"/> is a <c>ContentChannel.Key</c> and opaque here; the write seam validates it against
/// the catalogue. A key that is later retired keeps its row, as a brand profile's stored keys do.
/// </remarks>
public class CreativeContextChannel : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid CreativeContextId { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    /// <summary>Position among this context's channels, unique within the context.</summary>
    public int SortOrder { get; set; }
}
