namespace CreatorPantry.Domain.Modules.Brand.Managers;

public static class BrandAuditActions
{
    public const string ResourceType = "BrandProfile";

    public const string Created = "brand.profile.created";

    public const string Updated = "brand.profile.updated";

    public const string SourceDocumentResourceType = "BrandSourceDocument";

    public const string SourceDocumentUploaded = "brand.source.uploaded";

    /// <summary>A new version of an existing document's file. Earlier versions are kept and stay readable.</summary>
    public const string SourceDocumentReplaced = "brand.source.replaced";

    /// <summary>Shelved: out of the pickers, still readable, reversible.</summary>
    public const string SourceDocumentArchived = "brand.source.archived";

    public const string SourceDocumentUnarchived = "brand.source.unarchived";

    /// <summary>
    /// Soft-deleted. Nothing is deleted; the row, its versions and its stored objects all survive, and an
    /// Owner can put it back.
    /// </summary>
    public const string SourceDocumentRemoved = "brand.source.removed";

    /// <summary>
    /// Brought back out of the bin by an Owner. The <c>RemovedAt</c> and <c>RemovedBy</c> columns are cleared
    /// by the schema's own consistency constraint, so this pair of entries is the only lasting record that
    /// the document was ever removed.
    /// </summary>
    public const string SourceDocumentRestored = "brand.source.restored";

    /// <summary>
    /// One version was read as text by the Worker.
    /// </summary>
    /// <remarks>
    /// The only entry in this module with no actor, and correctly so: a background worker running as the
    /// workspace's service identity is not a person, and naming the creator who uploaded the file would
    /// attribute a machine's action to them. The entry says which version, which parser and how it ended —
    /// never a word of the document, which is what makes it safe to log at all.
    /// </remarks>
    public const string SourceDocumentExtracted = "brand.source.extracted";

    /// <summary>
    /// A creator replaced one version's extracted text with their own.
    /// </summary>
    /// <remarks>
    /// Names the actor, unlike <see cref="SourceDocumentExtracted"/>: this one is a person's edit to creator
    /// source material. The summary says which version and which ordinal and nothing else — the creator's stated
    /// reason is on the extraction row, and an audit summary is not where free text about a private document
    /// belongs.
    /// </remarks>
    public const string SourceDocumentTextCorrected = "brand.source.extraction.corrected";
}
