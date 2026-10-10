using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// A smaller encoding of one stored picture, or the record that there will not be one (B-28, FLU-007).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A derivative, and never the picture.</strong> The source — a staged <see cref="GeneratedImage"/>
/// or one <see cref="MediaAssetVersion"/> — is the creator's original and stays exactly as it was stored
/// (media.md). Nothing that writes or removes a row here touches the source's bytes or its row; a
/// rendition can always be thrown away and made again, which is why removing one is routine and removing
/// an original is not.
/// </para>
/// <para>
/// <strong>One row per source and purpose.</strong> Exactly one of the two sources is set, which the
/// table's own check holds, and a unique index per source kind is what stops a retried job storing the
/// same rendition twice. A source that cannot be compressed has a row too, saying so, and that row holds
/// the slot: an absence would look like work still to do.
/// </para>
/// <para>
/// <strong>The byte columns are all present or all absent.</strong> A <see cref="MediaRenditionStatus.Ready"/>
/// row describes bytes that were stored and measured; a <see cref="MediaRenditionStatus.NotCompressed"/>
/// row describes none and carries a reason instead. No row claims a key, a size or a checksum it does not
/// have.
/// </para>
/// <para>
/// <strong>The bytes are in private storage and there is no URL here.</strong> <see cref="ObjectKey"/> is
/// the source's own opaque key with the purpose after it, in the source's container.
/// </para>
/// <para>
/// Rows are written once and never edited. They are deleted, with their bytes, when the source is
/// declined, expired, kept or deleted.
/// </para>
/// </remarks>
public class MediaRendition : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The staged image this was made from, or null for a library version.</summary>
    public Guid? GeneratedImageId { get; set; }

    /// <summary>The library asset this was made from, or null for a staged image.</summary>
    public Guid? MediaAssetId { get; set; }

    /// <summary>Which version of that asset. Set exactly when <see cref="MediaAssetId"/> is.</summary>
    public int? MediaAssetVersionNumber { get; set; }

    public MediaRenditionPurpose Purpose { get; set; }

    public MediaRenditionStatus Status { get; set; }

    /// <summary>Why there is no rendition. Set exactly when the status says there is none.</summary>
    public MediaRenditionReason? NotCompressedReason { get; set; }

    /// <summary>
    /// The checksum of the source bytes this was made from, with its algorithm prefix.
    /// </summary>
    /// <remarks>
    /// What lets a rendition be carried from a staged image to the library version made from it without
    /// being recomputed: the version's bytes are a verified copy, so its checksum is this one, and a row
    /// that did not match would be about a different picture.
    /// </remarks>
    public string SourceContentChecksum { get; set; } = string.Empty;

    /// <summary>The opaque name of the rendition's object. Unique: one row per set of bytes.</summary>
    public string? ObjectKey { get; set; }

    public string? MediaType { get; set; }

    public long? SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>The checksum of the rendition's own stored bytes, as the store measured it.</summary>
    public string? ContentChecksum { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
