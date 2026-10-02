using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>
/// Everything a claimed extraction needs to know about what it is reading, including where the bytes are.
/// </summary>
/// <remarks>
/// <strong>Internal, and the interface that returns it is too.</strong> This is the second record in the module
/// to carry an <c>ObjectKey</c>, and it is kept inside the data layer the same way
/// <c>BrandSourceVersionObjectRecord</c> is kept to one hop: nothing above
/// <see cref="IBrandSourceExtractionDataLayer"/> is handed one, so nothing above it could publish one.
/// </remarks>
internal sealed record BrandSourceExtractionTargetRecord(
    Guid OperationId,
    BrandSourceExtractionOperationStatus OperationStatus,
    Guid? LeasedBy,
    Guid DocumentId,
    BrandSourceDocumentStatus DocumentStatus,
    Guid VersionId,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string ObjectKey);

/// <summary>
/// One of a version's extraction artifacts, with its text's pointer.
/// </summary>
/// <remarks>
/// Internal for the reason <see cref="BrandSourceExtractionTargetRecord"/> is: it carries an
/// <c>ExtractedTextObjectKey</c>, and nothing above <see cref="IBrandSourceExtractionDataLayer"/> is handed one,
/// so nothing above it could publish one. The public <see cref="BrandSourceExtractionArtifact"/> is the same
/// record with the pointer taken off.
/// </remarks>
internal sealed record BrandSourceExtractionArtifactRecord(
    Guid Id,
    int Ordinal,
    BrandSourceExtractionStatus Status,
    BrandSourceExtractionOrigin Origin,
    string? ExtractedTextObjectKey,
    string? ContentChecksum,
    string? Reason,
    DateTimeOffset CreatedAt);

internal interface IBrandSourceExtractionRepository
{
    /// <summary>
    /// Stages a queued extraction for a version that is itself being staged. Nothing is saved.
    /// </summary>
    /// <remarks>
    /// Called from the upload and the replacement, which save it in the same <c>SaveChangesAsync</c> as the
    /// version row. That is the whole durability argument: both commit or neither does, so there is never a
    /// committed version with no work queued and never queued work for a version that does not exist.
    /// </remarks>
    void Enqueue(BrandSourceExtractionOperation operation);

    /// <summary>
    /// What one claimed operation of the resolved workspace is about, or null when there is no such operation.
    /// </summary>
    /// <remarks>
    /// Takes no workspace id: the query filter scopes it, so an operation belonging to another workspace is
    /// absent rather than found and refused. The version is joined rather than looked up separately because a
    /// readable operation always has a readable version — the composite foreign key carrying
    /// <c>WorkspaceId</c> makes an operation that names another workspace's version unrepresentable.
    /// </remarks>
    Task<BrandSourceExtractionTargetRecord?> FindTargetAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>One operation of the resolved workspace, tracked, for the write that completes it.</summary>
    Task<BrandSourceExtractionOperation?> FindForUpdateAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// The ordinal the next extraction of one version takes: one more than the highest there is, or 1.
    /// </summary>
    /// <remarks>
    /// Read rather than counted, because an ordinal is never reused and a count would collide with the gap a
    /// deleted row would leave. Not a reservation either: the unique index on (workspace, version, ordinal) is
    /// what actually settles a race, and this is the candidate offered to it.
    /// </remarks>
    Task<int> NextOrdinalAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Stages one extraction row. Nothing is saved.</summary>
    void Add(BrandSourceExtraction extraction);

    /// <summary>Stages the embedding of a succeeded extraction, to commit in the same save as that extraction.</summary>
    void EnqueueEmbedding(BrandSourceEmbeddingOperation operation);

    /// <summary>
    /// One numbered version of one document in the resolved workspace, or null when there is no such pair.
    /// </summary>
    /// <remarks>
    /// Takes no workspace id and no status: the query filter scopes it, so another workspace's document is absent
    /// rather than found and refused, and whether a removed or archived one may be read or written is for Business
    /// to decide. The document's status and current version number ride along because both are refusal decisions
    /// and a second query for two columns would be a second round trip.
    /// </remarks>
    Task<BrandSourceVersionReview?> FindVersionForReviewAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// A version's current extraction — its highest ordinal — or null when nothing has extracted it.
    /// </summary>
    /// <remarks>
    /// The highest ordinal rather than a flag, because that is what "current" means here and the unique index on
    /// (workspace, version, ordinal) is what orders them. Projected, so no entity is tracked and a caller cannot
    /// accidentally write through one: an extraction is immutable and the interceptor would refuse anyway, but a
    /// read that cannot produce a writable entity is a better guarantee than one that is refused later.
    /// </remarks>
    Task<BrandSourceExtractionArtifactRecord?> FindCurrentArtifactAsync(
        Guid versionId, CancellationToken cancellationToken);
}

internal sealed class BrandSourceExtractionRepository(CreatorPantryDbContext context) : IBrandSourceExtractionRepository
{
    public void Enqueue(BrandSourceExtractionOperation operation) =>
        context.BrandSourceExtractionOperations.Add(operation);

    public void Add(BrandSourceExtraction extraction) => context.BrandSourceExtractions.Add(extraction);

    public void EnqueueEmbedding(BrandSourceEmbeddingOperation operation) =>
        context.BrandSourceEmbeddingOperations.Add(operation);

    public Task<BrandSourceExtractionTargetRecord?> FindTargetAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        context.BrandSourceExtractionOperations
            .AsNoTracking()
            .Where(operation => operation.Id == operationId)
            .Join(
                context.BrandSourceDocumentVersions.AsNoTracking(),
                operation => operation.BrandSourceDocumentVersionId,
                version => version.Id,
                (operation, version) => new { operation, version })
            .Join(
                context.BrandSourceDocuments.AsNoTracking(),
                row => row.operation.BrandSourceDocumentId,
                document => document.Id,
                (row, document) => new BrandSourceExtractionTargetRecord(
                    row.operation.Id,
                    row.operation.Status,
                    row.operation.LeasedBy,
                    document.Id,
                    document.Status,
                    row.version.Id,
                    row.version.VersionNumber,
                    row.version.MediaType,
                    row.version.SizeBytes,
                    row.version.ObjectKey))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandSourceExtractionOperation?> FindForUpdateAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        context.BrandSourceExtractionOperations
            .FirstOrDefaultAsync(operation => operation.Id == operationId, cancellationToken);

    public Task<BrandSourceVersionReview?> FindVersionForReviewAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken) =>
        context.BrandSourceDocuments
            .AsNoTracking()
            .Where(document => document.Id == documentId)
            .Join(
                context.BrandSourceDocumentVersions.AsNoTracking(),
                document => document.Id,
                version => version.BrandSourceDocumentId,
                (document, version) => new { document, version })
            .Where(row => row.version.VersionNumber == versionNumber)
            .Select(row => new BrandSourceVersionReview(
                row.document.Id,
                row.document.Status,
                row.document.CurrentVersionNumber,
                row.version.Id,
                row.version.VersionNumber))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandSourceExtractionArtifactRecord?> FindCurrentArtifactAsync(
        Guid versionId, CancellationToken cancellationToken) =>
        context.BrandSourceExtractions
            .AsNoTracking()
            .Where(extraction => extraction.BrandSourceDocumentVersionId == versionId)
            .OrderByDescending(extraction => extraction.Ordinal)
            .Select(extraction => new BrandSourceExtractionArtifactRecord(
                extraction.Id,
                extraction.Ordinal,
                extraction.Status,
                extraction.Origin,
                extraction.ExtractedTextObjectKey,
                extraction.ContentChecksum,
                extraction.Reason,
                extraction.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int> NextOrdinalAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var highest = await context.BrandSourceExtractions
            .AsNoTracking()
            .Where(extraction => extraction.BrandSourceDocumentVersionId == versionId)
            .Select(extraction => (int?)extraction.Ordinal)
            .MaxAsync(cancellationToken);

        return (highest ?? 0) + 1;
    }
}
