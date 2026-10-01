namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Stable, machine-readable codes for the brand module. The <c>.not_found</c> suffix maps to 404.</summary>
public static class BrandErrorCodes
{
    /// <summary>
    /// The workspace has no brand profile yet. Distinct from the workspace itself being unknown or
    /// inaccessible, which the tenancy pipeline answers earlier with its own code, so a caller who reaches this
    /// one is already a member and learns nothing about any other workspace.
    /// </summary>
    public const string ProfileNotFound = "brand.profile.not_found";

    /// <summary>The request failed shape or domain validation. Falls through to 400.</summary>
    public const string InvalidRequest = "brand.profile.invalid_request";

    /// <summary>The caller's role is below Editor. Maps to 403.</summary>
    public const string Forbidden = "brand.profile.forbidden";

    /// <summary>The profile moved on since the state the edit was composed against. Maps to 409.</summary>
    public const string Conflict = "brand.profile.conflict";

    /// <summary>A profile already exists for this workspace; edit it instead. Maps to 409.</summary>
    public const string AlreadyExistsConflict = "brand.profile.exists.conflict";

    /// <summary>
    /// Logo links cannot be accepted yet: the server cannot verify an asset id belongs to this workspace until
    /// the media seam exists. Maps to 422.
    /// </summary>
    public const string AssetsUnprocessable = "brand.assets.unprocessable";

    /// <summary>The caller's role is below Editor. Maps to 403.</summary>
    public const string SourceForbidden = "brand.source.forbidden";

    /// <summary>The upload's metadata or file part failed shape validation. Falls through to 400.</summary>
    public const string SourceInvalidRequest = "brand.source.invalid_request";

    /// <summary>
    /// No such source document, or version of one, in the resolved workspace. Maps to 404.
    /// </summary>
    /// <remarks>
    /// One code for five situations that must be indistinguishable: an id that was never issued, another
    /// workspace's document, a document the workspace removed, a version number the document does not have,
    /// and a version number it does not have <em>yet</em>. A caller who could tell these apart could use a
    /// read to learn that a document exists somewhere they cannot see it, or to count a workspace's versions
    /// (tenancy.md).
    /// </remarks>
    public const string SourceNotFound = "brand.source.not_found";

    /// <summary>
    /// The document moved on since the read the replacement was composed against. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Says only that the caller is holding a stale picture, never what changed or who changed it: the remedy
    /// is to read the document again, and a client told more would learn about edits it may not be entitled to
    /// a history of.
    /// </remarks>
    public const string SourceConflict = "brand.source.conflict";

    /// <summary>
    /// The document is archived, so it cannot take a new version. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SourceConflict"/> because the remedy is different and a client can act on it:
    /// restore the document, then replace its file. Deliberately not a 404 — archiving is a shelf, a document
    /// on it still reads and still downloads, so pretending it does not exist would be a worse answer than
    /// refusing the write.
    /// </remarks>
    public const string SourceArchivedConflict = "brand.source.archived.conflict";

    /// <summary>The file is larger than its format allows. Maps to 413.</summary>
    public const string SourceFileTooLarge = "brand.source.file.payload_too_large";

    /// <summary>
    /// The bytes are not one of the accepted formats, or are not the format the filename claims. Maps to 422.
    /// </summary>
    public const string SourceFileUnsupported = "brand.source.file.unsupported.unprocessable";

    /// <summary>The bytes start like an accepted format but do not hold together as one. Maps to 422.</summary>
    public const string SourceFileCorrupt = "brand.source.file.corrupt.unprocessable";

    /// <summary>The malware scan refused the file. Maps to 422, and says nothing of what was recognised.</summary>
    public const string SourceFileRejected = "brand.source.file.rejected.unprocessable";

    /// <summary>No scan verdict could be had, so the file was not accepted. Maps to 503.</summary>
    public const string SourceScanUnavailable = "brand.source.scan.unavailable";

    /// <summary>Private storage could not be reached, so nothing was recorded. Maps to 503.</summary>
    public const string SourceStorageUnavailable = "brand.source.storage.unavailable";

    /// <summary>
    /// The version's text has not been extracted yet, so there is nothing to correct. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Not a 404: the version exists, downloads, and reads as <c>NotExtracted</c> through the review route. And
    /// not an invitation to write the first artifact either — a correction at ordinal 1 would race the worker
    /// that is about to write it, and the loser of that race is whichever one the creator cares about.
    /// </remarks>
    public const string SourceExtractionPendingConflict = "brand.source.extraction.pending.conflict";

    /// <summary>
    /// The extracted text moved on since the read this correction was composed against. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Says only that the caller is holding a stale copy, never what changed: the remedy is to read the text
    /// again, and a client told more would learn about a collaborator's edits it may not be entitled to a history
    /// of. Nothing is written, and the creator's attempted text comes back to them in their own client rather
    /// than being merged into something they have not seen.
    /// </remarks>
    public const string SourceExtractionConflict = "brand.source.extraction.conflict";

    /// <summary>
    /// The version is not the document's current one, so its text cannot be corrected. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SourceExtractionConflict"/> because the remedy is different and a client can act
    /// on it: correct the current version instead. The restriction exists because only the current version's text
    /// has consumers — chunking, retrieval and guide drafting all read it — so a correction on a superseded
    /// version would be a write whose effect the creator could never see. Reading an older version's text stays
    /// allowed; history is readable.
    /// </remarks>
    public const string SourceExtractionSupersededConflict = "brand.source.extraction.superseded.conflict";
}
