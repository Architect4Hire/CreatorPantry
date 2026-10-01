using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One piece of private source material a creator offers as evidence of their brand: a style guide they
/// already have, a post, a newsletter, a reference image. Workspace-owned creator IP and an aggregate root.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Metadata only.</strong> The bytes are a private blob named by a
/// <see cref="BrandSourceDocumentVersion"/>; nothing here or below holds a document body, extracted text, or
/// a URL (media.md).
/// </para>
/// <para>
/// <strong>Mutable description over immutable versions.</strong> The creator may retitle, reclassify, archive
/// and restore a document; the file it describes changes only by adding a version.
/// <see cref="CurrentVersionNumber"/> is a counter rather than a foreign key, so the root and its versions do
/// not point at each other.
/// </para>
/// <para>
/// <strong>Never deleted by ordinary code.</strong> <see cref="BrandSourceDocumentStatus.Removed"/> is a
/// tombstone, and the version foreign key refuses a delete while any version exists. Only the workspace's own
/// erasure removes these rows.
/// </para>
/// </remarks>
public class BrandSourceDocument : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Title { get; set; } = string.Empty;

    public BrandSourceDocumentType DocumentType { get; set; }

    public BrandSourcePurpose Purpose { get; set; }

    /// <summary>The channel this document exemplifies, or null for none in particular. Opaque, as on <see cref="BrandChannelDefault"/>.</summary>
    public string? ChannelKey { get; set; }

    /// <summary>Who the document was written for. Free text.</summary>
    public string? Audience { get; set; }

    public BrandSourceDocumentStatus Status { get; set; } = BrandSourceDocumentStatus.Active;

    /// <summary>The newest version's number, starting at 1: a document is created with its first version.</summary>
    public int CurrentVersionNumber { get; set; } = 1;

    /// <summary>When the document was last archived. Kept through a restore and a removal.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid? RemovedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Not a foreign key, matching <see cref="BrandProfile"/>: authorship outlives membership.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<BrandSourceDocumentVersion> Versions { get; set; } = [];

    public List<BrandSourceDocumentTag> Tags { get; set; } = [];
}
