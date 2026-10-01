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
}
