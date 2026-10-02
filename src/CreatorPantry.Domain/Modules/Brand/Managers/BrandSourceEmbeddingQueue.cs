using CreatorPantry.Domain.Modules.Brand.Data.Entities;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Builds the queued embedding a newly committed extraction carries with it.</summary>
/// <remarks>
/// Shared by the extraction worker's commit, the creator's correction and the maintenance backfill so the
/// three cannot drift on what a queued row looks like. Only an extraction that produced text is queued: an
/// unsupported or failed one has nothing to embed, and the database refuses the row anyway.
/// </remarks>
public static class BrandSourceEmbeddingQueue
{
    /// <param name="workspaceId">From the resolved context, never a request field.</param>
    public static BrandSourceEmbeddingOperation For(
        Guid workspaceId, Guid documentId, BrandSourceExtraction extraction, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        return For(workspaceId, documentId, extraction.Id, extraction.BrandSourceDocumentVersionId, now);
    }

    public static BrandSourceEmbeddingOperation For(
        Guid workspaceId, Guid documentId, Guid extractionId, Guid versionId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = documentId,
            BrandSourceExtractionId = extractionId,
            BrandSourceDocumentVersionId = versionId,
            SourceStatus = BrandSourceExtractionStatus.Succeeded,
            Status = BrandSourceEmbeddingOperationStatus.Queued,
            Attempts = 0,
            AvailableAt = now,
            QueuedAt = now,
            StatusChangedAt = now,
        };
}
