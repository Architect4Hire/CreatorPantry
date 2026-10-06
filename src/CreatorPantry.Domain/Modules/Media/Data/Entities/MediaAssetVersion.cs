using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One set of bytes belonging to an asset, exactly as it arrived. Write-once: a new file is a new version,
/// and <see cref="ImmutableRecordInterceptor"/> refuses every update and delete of an existing one.
/// </summary>
/// <remarks>
/// <para>
/// Every column is a fact about those bytes that was true when they were stored and stays true. Anything a
/// creator can change — title, tags, alt text, rights — is on <see cref="MediaAsset"/>, which is what lets
/// this row be immutable (DAM-010's "immutable original").
/// </para>
/// <para>
/// <see cref="ObjectKey"/> is a server-generated name inside a private container. It is never built from
/// <see cref="OriginalFileName"/>, never a URL, and never returned to a client (media.md).
/// </para>
/// </remarks>
public class MediaAssetVersion : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The asset these bytes belong to, workspace-paired so a neighbour's is unrepresentable.</summary>
    public Guid MediaAssetId { get; set; }

    /// <summary>
    /// 1 for the first, then one more per replacement. Unique per asset.
    /// </summary>
    /// <remarks>
    /// The unique index is what DAM-010 means by "concurrent uploads cannot share a version number": two
    /// requests that both read <c>CurrentVersionNumber</c> as 3 cannot both commit a version 4, whatever
    /// the application layer believes about its own ordering.
    /// </remarks>
    public int VersionNumber { get; set; }

    /// <summary>The media type established from the bytes, not one a client or a provider declared.</summary>
    public string MediaType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary><c>sha256:</c> and the digest of the stored bytes.</summary>
    public string ContentChecksum { get; set; } = string.Empty;

    /// <summary>The creator's own filename, for display only. Never used to build a key or a header.</summary>
    public string? OriginalFileName { get; set; }

    /// <summary>The opaque name of the object in private storage. Unique: one row per set of bytes.</summary>
    public string ObjectKey { get; set; } = string.Empty;

    public MediaAssetVersionSource Source { get; set; }

    /// <summary>
    /// The staged image these bytes were copied from, when they were. Null for an upload.
    /// </summary>
    /// <remarks>
    /// Workspace-paired to <c>GeneratedImage</c>, so another workspace's staged image is unrepresentable
    /// rather than merely refused. It survives 12.8's retention sweep removing the staged copy: the sweep
    /// clears bytes and keeps rows, so this still resolves to the provider, model and prompt that made it.
    /// </remarks>
    public Guid? SourceGeneratedImageId { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership, as on the asset itself.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
