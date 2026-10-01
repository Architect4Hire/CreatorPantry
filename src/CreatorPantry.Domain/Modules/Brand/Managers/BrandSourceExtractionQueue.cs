using CreatorPantry.Domain.Modules.Brand.Data.Entities;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Builds the queued extraction a newly committed document version carries with it.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the upload and the replacement so the two cannot drift on what a queued row looks like, which is
/// the sort of difference that produces work that never runs on one of the two paths.
/// </para>
/// <para>
/// <strong>Every version is queued, images included.</strong> An image has no text and its extraction will be a
/// review state, which is the point: a version with no operation would be left reading as <em>not extracted</em>
/// in the library, and a creator cannot tell that apart from <em>still working on it</em>. Recording "there is
/// nothing to read, and here is why" is a worse-looking answer that is a better one.
/// </para>
/// </remarks>
public static class BrandSourceExtractionQueue
{
    /// <summary>
    /// The row to stage beside a version, claimable from the moment it commits.
    /// </summary>
    /// <param name="workspaceId">
    /// From the resolved context, never a request field: the row is keyed on it, so it cannot be left for the
    /// ownership interceptor to stamp — and the interceptor still refuses any other value.
    /// </param>
    /// <param name="now">The version's own timestamp, so the two agree about when this document arrived.</param>
    public static BrandSourceExtractionOperation For(
        Guid workspaceId, Guid documentId, Guid versionId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = documentId,
            BrandSourceDocumentVersionId = versionId,
            Status = BrandSourceExtractionOperationStatus.Queued,
            Attempts = 0,

            // Due immediately. There is no reason to delay: the version has committed, nothing else has to
            // happen first, and a creator watching the library wants the state to settle.
            AvailableAt = now,
            QueuedAt = now,
            StatusChangedAt = now,
        };
}
