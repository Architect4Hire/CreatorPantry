using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One named guide to how the brand writes and looks. Workspace-owned creator IP and an aggregate root; a
/// workspace may keep several.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A name over versions, not a body.</strong> Everything the guide says lives on its immutable
/// <see cref="BrandStyleGuideVersion"/> rows. The root holds what can change without the guide saying
/// anything different: its name, what it is for, and whether it is archived.
/// </para>
/// <para>
/// <strong>No "current" or "active" column.</strong> The latest version is the highest
/// <c>VersionNumber</c>, and the one stored activation decision is the workspace's
/// <see cref="BrandStyleGuideDefault"/>. A pointer here would be a second statement of either that could
/// disagree.
/// </para>
/// <para>
/// Never deleted by ordinary code: the version foreign key refuses it. Only workspace erasure removes a guide.
/// </para>
/// </remarks>
public class BrandStyleGuide : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What the creator keeps this guide for — "everyday voice", "holiday campaign". Free text.</summary>
    public string? Purpose { get; set; }

    public BrandStyleGuideStatus Status { get; set; } = BrandStyleGuideStatus.Active;

    /// <summary>When the guide was last archived. Kept through a restore.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Not a foreign key, matching <see cref="BrandProfile"/>: authorship outlives membership.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
