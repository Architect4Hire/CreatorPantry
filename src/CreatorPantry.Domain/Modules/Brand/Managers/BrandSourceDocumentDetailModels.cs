namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// One brand source document as a client reads it on its own: what the creator said about it, its current
/// version's metadata, whether its text has been extracted, and the token the next change must quote.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never an object key, a container or a URL</strong>, and never the document's bytes or its extracted
/// text. The file is reached through the download route, which streams it; there is no address for a client to
/// hold (media.md).
/// </para>
/// <para>
/// Three things the list row deliberately does not carry:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>ConcurrencyToken</c>, because a reader of a list is not about to change anything, and a token on every
/// row would be forty tokens nobody quotes. A reader of one document is the caller a replacement or an
/// archive comes from.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>CurrentVersion.ContentChecksum</c>, which is a verification aid rather than an address: it is what the
/// download's <c>ETag</c> is built from, so a client that has both can tell whether the bytes it holds are
/// still the bytes this version names. On forty rows it would be noise.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>ArchivedAt</c>, because a list is already filtered to one status and the date adds nothing to a row
/// whose status the caller asked for.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>One version, not a history.</strong> A document has exactly one until a replacement adds another,
/// and listing past versions is that feature's to publish — adding an empty-but-for-one array here would be a
/// shape shipped before anything could fill it.
/// </para>
/// </remarks>
/// <param name="ArchivedAt">When the document was last archived, kept through a restore. Null for one never archived.</param>
/// <param name="ConcurrencyToken">
/// Opaque. Stored and sent back on the next change; never parsed, compared or ordered by.
/// </param>
public sealed record BrandSourceDocumentDetailServiceModel(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    string? ChannelKey,
    string? Audience,
    IReadOnlyList<string> Tags,
    BrandSourceDocumentStatus Status,
    BrandSourceDocumentVersionDetailServiceModel CurrentVersion,
    BrandSourceExtractionSummaryServiceModel Extraction,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <param name="MediaType">Established from the file's bytes, not from what the client declared.</param>
/// <param name="OriginalFileName">
/// The creator's own filename, for display. <strong>Not</strong> what a download is named: see
/// <see cref="BrandSourceDownloadFileName"/>.
/// </param>
/// <param name="ContentChecksum"><c>sha256:</c> and the digest of the stored bytes. Also the download's entity tag.</param>
public sealed record BrandSourceDocumentVersionDetailServiceModel(
    Guid Id,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string OriginalFileName,
    string ContentChecksum,
    DateTimeOffset CreatedAt);

/// <summary>
/// What a repository read of one document found: the root's own columns, its current version's, and the
/// outcome of that version's latest extraction attempt.
/// </summary>
/// <remarks>
/// Projected in SQL like the list's row, and for the same reason: no entity is materialised, so no object key
/// or extracted text is ever loaded into a layer that could return one. <c>ObjectKey</c> is absent here
/// deliberately — the download reads it through its own narrower query, which returns nothing else.
/// </remarks>
public sealed record BrandSourceDocumentDetailRecord(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    string? ChannelKey,
    string? Audience,
    BrandSourceDocumentStatus Status,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    byte[] RowVersion,
    Guid VersionId,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string OriginalFileName,
    string ContentChecksum,
    DateTimeOffset VersionCreatedAt,
    BrandSourceExtractionStatus? ExtractionStatus,
    BrandSourceExtractionOrigin? ExtractionOrigin,
    DateTimeOffset? ExtractionAt);

/// <summary>
/// Where one version's bytes are and what they are, for the download alone.
/// </summary>
/// <remarks>
/// <para>
/// The only type in this module that carries an <c>ObjectKey</c> out of the data layer, and it exists so that
/// the key's blast radius is one hop: Business hands it straight back down to the gateway and puts nothing
/// from it in a ServiceModel. Nothing on the wire is built from this record except the title, the version
/// number and the media type.
/// </para>
/// <para>
/// <c>Title</c> rides along because the download's filename is built from it and a second query for one column
/// would be a second round trip for a name.
/// </para>
/// </remarks>
public sealed record BrandSourceVersionObjectRecord(
    string Title,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string ContentChecksum,
    string ObjectKey);

/// <summary>
/// One version's bytes, open for reading, with everything the response needs around them.
/// </summary>
/// <remarks>
/// <para>
/// A stream rather than a byte array: the cap is 20 MB and a response that buffered it would hold that per
/// concurrent download for no gain. The response owns it for its whole length.
/// </para>
/// <para>
/// <strong>Disposing this releases the store's lease, not merely the stream</strong> — the same shape, and for
/// the same reason, as <see cref="BrandSourceObjectContent"/>: a provider may hold more than a stream open for
/// a read, and disposing the stream alone would leak whatever else it is.
/// </para>
/// <para>
/// <c>FileName</c> is already safe — <c>a-z0-9</c>, hyphens and one dot — so the controller quotes it into the
/// header without escaping anything. There is no object key and no URL on this type.
/// </para>
/// </remarks>
public sealed class BrandSourceDownload(
    IAsyncDisposable lease,
    Stream content,
    string fileName,
    string mediaType,
    long sizeBytes,
    string contentChecksum) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string FileName { get; } = fileName;

    /// <summary>The version's stored media type. What the response says the bytes are.</summary>
    public string MediaType { get; } = mediaType;

    public long SizeBytes { get; } = sizeBytes;

    /// <summary><c>sha256:</c> and the digest. The response's entity tag, which is strong because a version is immutable.</summary>
    public string ContentChecksum { get; } = contentChecksum;

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}
