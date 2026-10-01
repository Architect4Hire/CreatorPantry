using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One uploaded file, exactly as it arrived. Write-once: replacing a document adds a version, and
/// <see cref="ImmutableRecordInterceptor"/> refuses every update and delete of an existing one.
/// </summary>
/// <remarks>
/// <para>
/// Every column is a fact about the original that was true at upload and stays true. Anything learned later —
/// extracted text, a correction — is a <see cref="BrandSourceExtraction"/> row, which is why this one can be
/// immutable.
/// </para>
/// <para>
/// <see cref="ObjectKey"/> is a server-generated name inside a private container. It is never built from
/// <see cref="OriginalFileName"/>, never a URL, and never returned to a client.
/// </para>
/// </remarks>
public class BrandSourceDocumentVersion : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandSourceDocumentId { get; set; }

    /// <summary>1 for the first upload, then one more per replacement. Unique per document.</summary>
    public int VersionNumber { get; set; }

    /// <summary>The media type established from the file's signature, not the one the client declared.</summary>
    public string MediaType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary><c>sha256:</c> and the digest of the stored bytes.</summary>
    public string ContentChecksum { get; set; } = string.Empty;

    /// <summary>The creator's own filename, for display and download naming only.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>Not a foreign key: authorship outlives membership, as on the document itself.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
