using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>Everything the embedding job needs to know about the artifact its operation names.</summary>
internal sealed record BrandSourceEmbeddingTargetRecord(
    Guid OperationId,
    BrandSourceEmbeddingOperationStatus OperationStatus,
    Guid? LeasedBy,
    Guid DocumentId,
    BrandSourceDocumentStatus DocumentStatus,
    Guid ExtractionId,
    Guid VersionId,
    int ExtractionOrdinal,
    string ExtractedTextObjectKey,
    string ContentChecksum,
    bool HasNewerExtraction);

internal interface IBrandSourceEmbeddingRepository
{
    Task<BrandSourceEmbeddingTargetRecord?> FindTargetAsync(Guid operationId, CancellationToken cancellationToken);

    Task<BrandSourceEmbeddingOperation?> FindForUpdateAsync(Guid operationId, CancellationToken cancellationToken);

    Task<BrandSourceChunkSet?> FindBuildingSetAsync(
        Guid extractionId, string model, CancellationToken cancellationToken);

    Task<BrandSourceChunkSet?> FindSetForUpdateAsync(Guid setId, CancellationToken cancellationToken);

    Task<int> CountChunksAsync(Guid setId, CancellationToken cancellationToken);

    /// <summary>
    /// The sets a newly current set replaces: every current set of the same extraction (another model's
    /// vectors are not comparable with this one's) and of any older extraction of the same version.
    /// </summary>
    Task<List<BrandSourceChunkSet>> FindReplacedByAsync(
        Guid extractionId, Guid versionId, Guid setId, CancellationToken cancellationToken);

    Task<bool> HasLiveOperationAsync(Guid extractionId, CancellationToken cancellationToken);

    /// <summary>The extraction, only if it is this workspace's, succeeded, and is its version's latest.</summary>
    Task<BrandSourceBackfillTargetRecord?> FindLatestSucceededAsync(Guid extractionId, CancellationToken cancellationToken);

    void Add(BrandSourceChunkSet set);

    void Add(IEnumerable<BrandSourceChunk> chunks);

    void Remove(BrandSourceChunkSet set);

    void Enqueue(BrandSourceEmbeddingOperation operation);
}

internal sealed record BrandSourceBackfillTargetRecord(Guid DocumentId, Guid ExtractionId, Guid VersionId);

internal sealed class BrandSourceEmbeddingRepository(CreatorPantryDbContext context) : IBrandSourceEmbeddingRepository
{
    public Task<BrandSourceEmbeddingTargetRecord?> FindTargetAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        (from operation in context.BrandSourceEmbeddingOperations.AsNoTracking()
         where operation.Id == operationId
         join extraction in context.BrandSourceExtractions.AsNoTracking()
             on operation.BrandSourceExtractionId equals extraction.Id
         join document in context.BrandSourceDocuments.AsNoTracking()
             on operation.BrandSourceDocumentId equals document.Id
         select new BrandSourceEmbeddingTargetRecord(
             operation.Id,
             operation.Status,
             operation.LeasedBy,
             document.Id,
             document.Status,
             extraction.Id,
             extraction.BrandSourceDocumentVersionId,
             extraction.Ordinal,
             extraction.ExtractedTextObjectKey!,
             extraction.ContentChecksum!,
             context.BrandSourceExtractions.Any(newer =>
                 newer.BrandSourceDocumentVersionId == extraction.BrandSourceDocumentVersionId
                 && newer.Ordinal > extraction.Ordinal)))
        .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandSourceEmbeddingOperation?> FindForUpdateAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        context.BrandSourceEmbeddingOperations
            .FirstOrDefaultAsync(operation => operation.Id == operationId, cancellationToken);

    public Task<BrandSourceChunkSet?> FindBuildingSetAsync(
        Guid extractionId, string model, CancellationToken cancellationToken) =>
        context.BrandSourceChunkSets.FirstOrDefaultAsync(
            set => set.BrandSourceExtractionId == extractionId
                && set.EmbeddingModel == model
                && set.Status == BrandSourceChunkSetStatus.Building,
            cancellationToken);

    public Task<BrandSourceChunkSet?> FindSetForUpdateAsync(Guid setId, CancellationToken cancellationToken) =>
        context.BrandSourceChunkSets.FirstOrDefaultAsync(set => set.Id == setId, cancellationToken);

    public Task<int> CountChunksAsync(Guid setId, CancellationToken cancellationToken) =>
        context.BrandSourceChunks.CountAsync(chunk => chunk.BrandSourceChunkSetId == setId, cancellationToken);

    public Task<List<BrandSourceChunkSet>> FindReplacedByAsync(
        Guid extractionId, Guid versionId, Guid setId, CancellationToken cancellationToken) =>
        (from set in context.BrandSourceChunkSets
         join extraction in context.BrandSourceExtractions
             on set.BrandSourceExtractionId equals extraction.Id
         where set.Id != setId
             && set.Status == BrandSourceChunkSetStatus.Current
             && extraction.BrandSourceDocumentVersionId == versionId
             && (set.BrandSourceExtractionId == extractionId
                 || extraction.Ordinal < context.BrandSourceExtractions
                     .Where(own => own.Id == extractionId)
                     .Select(own => own.Ordinal)
                     .FirstOrDefault())
         select set)
        .ToListAsync(cancellationToken);

    public Task<bool> HasLiveOperationAsync(Guid extractionId, CancellationToken cancellationToken) =>
        context.BrandSourceEmbeddingOperations.AnyAsync(
            operation => operation.BrandSourceExtractionId == extractionId
                && (operation.Status == BrandSourceEmbeddingOperationStatus.Queued
                    || operation.Status == BrandSourceEmbeddingOperationStatus.Running),
            cancellationToken);

    public Task<BrandSourceBackfillTargetRecord?> FindLatestSucceededAsync(
        Guid extractionId, CancellationToken cancellationToken) =>
        (from extraction in context.BrandSourceExtractions.AsNoTracking()
         where extraction.Id == extractionId
             && extraction.Status == BrandSourceExtractionStatus.Succeeded
             && !context.BrandSourceExtractions.Any(newer =>
                 newer.BrandSourceDocumentVersionId == extraction.BrandSourceDocumentVersionId
                 && newer.Ordinal > extraction.Ordinal)
         join version in context.BrandSourceDocumentVersions.AsNoTracking()
             on extraction.BrandSourceDocumentVersionId equals version.Id
         join document in context.BrandSourceDocuments.AsNoTracking()
             on version.BrandSourceDocumentId equals document.Id
         where document.Status != BrandSourceDocumentStatus.Removed
         select new BrandSourceBackfillTargetRecord(document.Id, extraction.Id, version.Id))
        .FirstOrDefaultAsync(cancellationToken);

    public void Add(BrandSourceChunkSet set) => context.BrandSourceChunkSets.Add(set);

    public void Add(IEnumerable<BrandSourceChunk> chunks) => context.BrandSourceChunks.AddRange(chunks);

    public void Remove(BrandSourceChunkSet set) => context.BrandSourceChunkSets.Remove(set);

    public void Enqueue(BrandSourceEmbeddingOperation operation) =>
        context.BrandSourceEmbeddingOperations.Add(operation);
}
