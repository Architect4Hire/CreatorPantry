namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Where a render's bytes live and what they are, for a live asset's current version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one place an object key leaves the data layer, and it stops at the gateway.</strong> It is read so
/// the gateway can be asked to open it and goes no further: nothing above the DataLayer sees this record, and
/// nothing a client receives carries a key, a container or a URL (media.md).
/// </para>
/// <para>
/// <strong><see cref="Title"/> is the one piece of creator text here, and 12.9g added it.</strong> The download names
/// its file from the title, so the alternative was a second query for one column — and 12.9f's claim that this read
/// touches nothing else about the asset is now "nothing else <em>but</em> the title". Still the narrowest read in the
/// module: no description, no alt text, no tags, no rights.
/// </para>
/// </remarks>
public sealed record MediaAssetVersionObjectRecord(
    int VersionNumber,
    string ObjectKey,
    string MediaType,
    long SizeBytes,
    string ContentChecksum,
    string? OriginalFileName,
    string Title);

/// <summary>Why a render could not be served.</summary>
public enum MediaAssetOpenOutcome
{
    Opened = 1,

    /// <summary>
    /// There is nothing to render in the resolved workspace.
    /// </summary>
    /// <remarks>
    /// One outcome for four causes on purpose: an unknown id, a neighbour's id, a soft-deleted asset, and an asset
    /// whose current version row is missing are all "not here" to a caller. Distinguishing them would let a client
    /// count another workspace's library or learn that an asset they cannot see exists (tenancy.md).
    /// </remarks>
    NotFound = 2,

    /// <summary>
    /// The version is there and its bytes could not be reached.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="NotFound"/>: the asset exists and retrying is the remedy, which is a different
    /// thing to tell a creator than "it is gone". The same split the staged-image open and the brand source
    /// download both make.
    /// </remarks>
    StorageUnavailable = 3,
}

/// <summary>One asset's current version opened for reading, with the facts a response needs beside the bytes.</summary>
/// <remarks>
/// <para>
/// The media type, size and checksum come from the <em>store</em> rather than the database row, so the response
/// states what it is actually sending. The two agree because a version write refuses to commit a row whose facts
/// storage disagrees with.
/// </para>
/// <para>
/// <see cref="ContentChecksum"/> is what the strong <c>ETag</c> is built from. A version's bytes are write-once, so
/// the tag identifies this representation for as long as it exists — and a new version changes it, which is
/// exactly what a route serving "the current version" needs.
/// </para>
/// </remarks>
public sealed class MediaAssetRender : IAsyncDisposable
{
    private readonly IAsyncDisposable lease;

    public MediaAssetRender(Gateways.MediaAssetObjectContent content, int versionNumber, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        lease = content;
        Content = content.Content;
        MediaType = content.MediaType;
        SizeBytes = content.SizeBytes;
        ContentChecksum = content.ContentChecksum;
        VersionNumber = versionNumber;
        FileName = fileName;
    }

    /// <summary>A rendition of the version rather than the version's own bytes (AF.5.6).</summary>
    public MediaAssetRender(
        CreatorPantry.Domain.Managers.Storage.StoredObjectContent content,
        int versionNumber,
        MediaRenditionPurpose rendition,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        lease = content;
        Content = content.Content;
        MediaType = content.Object.MediaType;
        SizeBytes = content.Object.SizeBytes;
        ContentChecksum = content.Object.ContentChecksum;
        VersionNumber = versionNumber;
        FileName = fileName;
        Rendition = rendition;
    }

    /// <summary>
    /// Which rendition these bytes are, or null when they are the version as it was stored.
    /// </summary>
    /// <remarks>
    /// What was served, not what was asked for: a version with no such rendition is served as stored, and
    /// this is null.
    /// </remarks>
    public MediaRenditionPurpose? Rendition { get; }

    public Stream Content { get; }

    public string MediaType { get; }

    public long SizeBytes { get; }

    public string ContentChecksum { get; }

    /// <summary>Which version these bytes are, so a caller can log or correlate without a second read.</summary>
    public int VersionNumber { get; }

    /// <summary>
    /// The name a download should be saved as, or null for a render.
    /// </summary>
    /// <remarks>
    /// Built here rather than in the controller so one piece of code decides the name and the header it lands in,
    /// and so the render route cannot accidentally publish it: a render names no file, and a null is what makes that
    /// a shape rather than a convention somebody has to remember. Already slug-safe by construction — ASCII letters,
    /// digits, single hyphens and one dot — so there is nothing for a header to quote or encode.
    /// </remarks>
    public string? FileName { get; }

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}

/// <param name="Render">Present exactly when the outcome is <see cref="MediaAssetOpenOutcome.Opened"/>.</param>
public sealed record MediaAssetOpen(MediaAssetOpenOutcome Outcome, MediaAssetRender? Render = null);
