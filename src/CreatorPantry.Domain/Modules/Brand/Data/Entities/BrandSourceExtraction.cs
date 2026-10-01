using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One attempt to turn a <see cref="BrandSourceDocumentVersion"/> into text, or one creator correction of that
/// text. Write-once: a retry or a correction adds a row with the next <see cref="Ordinal"/>, so the earlier
/// artifact stays what later chunks and guide versions cited.
/// </summary>
/// <remarks>
/// The text itself is a private blob named by <see cref="ExtractedTextObjectKey"/>, present only when
/// <see cref="Status"/> is <see cref="BrandSourceExtractionStatus.Succeeded"/>. A version's current extraction
/// is its highest ordinal; a version with no row has not been extracted. The text is untrusted prompt content
/// however it was produced, corrected or not (ai.md).
/// </remarks>
public class BrandSourceExtraction : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandSourceDocumentVersionId { get; set; }

    /// <summary>1 for the first attempt on a version, then one more per retry or correction.</summary>
    public int Ordinal { get; set; }

    public BrandSourceExtractionStatus Status { get; set; }

    public BrandSourceExtractionOrigin Origin { get; set; }

    public string? ExtractedTextObjectKey { get; set; }

    /// <summary><c>sha256:</c> and the digest of the stored text, when there is any.</summary>
    public string? ContentChecksum { get; set; }

    /// <summary>The creator's note on a correction, or why an attempt produced no text.</summary>
    public string? Reason { get; set; }

    /// <summary>The correcting member, or null for a worker's attempt. Not a foreign key.</summary>
    public Guid? CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
