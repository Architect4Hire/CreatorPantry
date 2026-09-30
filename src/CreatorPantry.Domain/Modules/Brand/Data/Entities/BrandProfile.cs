using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// The workspace's brand identity facts: who the brand is, who it is for, where it publishes by default, and
/// the locale and time zone its work is scheduled in. Workspace-owned creator IP; exactly one per workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Facts, not style.</strong> There is no voice, tone, tenor, writing-style or visual-direction field
/// here and none may be added: <c>BrandStyleGuideVersion</c> (Phase 11A, DEC-010) is their sole source of
/// truth, and a second copy on the profile would be a competing one. <c>BrandProfileModelShapeTests</c> fails
/// on such a property name.
/// </para>
/// <para>
/// <strong>Mutable, versioned by counter.</strong> Unlike style-guide versions the profile is edited in place.
/// <see cref="Revision"/> counts edits; <see cref="RowVersion"/> catches concurrent ones. A generation's
/// <c>BrandContextPackage</c> snapshots the fields it used, so pinning does not need this row to be immutable.
/// </para>
/// <para>
/// <see cref="TimeZoneId"/> is the workspace's scheduling zone (publishing.md stores UTC plus the originating
/// workspace zone). Logos live in <see cref="AssetLinks"/> only, so there is no second pointer to drift.
/// </para>
/// </remarks>
public class BrandProfile : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string BrandName { get; set; } = string.Empty;

    public string? ShortDescription { get; set; }

    /// <summary>The audience a piece is written for unless a project says otherwise. Free text.</summary>
    public string? DefaultAudience { get; set; }

    /// <summary>BCP-47 language tag, e.g. <c>en-US</c>.</summary>
    public string? Locale { get; set; }

    /// <summary>IANA zone identifier, validated by the write seam through <c>ITimeZoneConverter</c>.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Edit counter, starting at 1 and incremented by the write seam on every change.</summary>
    public int Revision { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Not a foreign key, matching <c>Recipe</c>: authorship outlives membership.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<BrandChannelDefault> ChannelDefaults { get; set; } = [];

    public List<BrandLink> Links { get; set; } = [];

    public List<BrandAssetLink> AssetLinks { get; set; } = [];
}
