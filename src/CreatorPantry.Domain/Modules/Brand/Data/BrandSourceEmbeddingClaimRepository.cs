using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>
/// What a worker learns by claiming an embedding: where to look and under which lease, and nothing about the
/// document. No title, no text, no object key.
/// </summary>
public sealed record BrandSourceEmbeddingClaim(
    Guid OperationId, Guid WorkspaceId, Guid ExtractionId, Guid LeaseToken, int Attempts);

/// <summary>A chunk set the sweep found due for deletion, named by identifiers alone.</summary>
public sealed record BrandSourceRetirableSet(Guid WorkspaceId, Guid SetId);

/// <summary>An artifact the sweep found with no current set for the configured model, by identifiers alone.</summary>
public sealed record BrandSourceEmbeddingBackfill(Guid WorkspaceId, Guid ExtractionId);

/// <summary>
/// The cross-workspace half of the embedding queue: claiming the next due operation, recovering lapsed leases,
/// and finding work for the maintenance sweep.
/// </summary>
/// <remarks>
/// <para>
/// This is the fourth background queue claim, and it carries the same two conditions as the others (tenancy.md).
/// Every query here returns <strong>identifiers only</strong> — operation, workspace, extraction, set — never a
/// passage, a title or a key. And the Worker resolves and validates the workspace through
/// <c>IWorkspaceResolutionFacade</c> before anything is read or written on the strength of an identifier it
/// found: finding a row is not authorization, and every read or write of creator data the sweep leads to is made
/// by a facade under the workspace's own query filter. The two exceptions are
/// <see cref="ClaimNextAsync"/> and <see cref="RecoverAbandonedLeasesAsync"/>, which themselves write
/// <em>queue state</em> across workspaces — status, attempts and lease fields on the operation row, which holds
/// no creator content, and the ownership interceptor still refuses any change to <c>WorkspaceId</c>. The file is listed in <c>BulkOperationBoundaryTests.Exemptions</c>.
/// </para>
/// </remarks>
internal sealed class BrandSourceEmbeddingClaimRepository(CreatorPantryDbContext context)
{
    public async Task<BrandSourceEmbeddingClaim?> ClaimNextAsync(
        Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await context.BrandSourceEmbeddingOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == BrandSourceEmbeddingOperationStatus.Queued
                && operation.AvailableAt <= now)
            .OrderBy(operation => operation.AvailableAt)
            .Take(BrandPolicy.EmbeddingClaimBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            candidate.Status = BrandSourceEmbeddingOperationStatus.Running;
            candidate.StatusChangedAt = now;
            candidate.StartedAt ??= now;
            candidate.Attempts++;
            candidate.LeasedBy = leaseToken;
            candidate.LeaseExpiresAt = now + BrandPolicy.EmbeddingLeaseDuration;

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return new BrandSourceEmbeddingClaim(
                    candidate.Id, candidate.WorkspaceId, candidate.BrandSourceExtractionId, leaseToken, candidate.Attempts);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker won this row; try the next.
                context.Entry(candidate).State = EntityState.Detached;
            }
        }

        return null;
    }

    public async Task<(int Requeued, int Abandoned)> RecoverAbandonedLeasesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lapsed = await context.BrandSourceEmbeddingOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == BrandSourceEmbeddingOperationStatus.Running
                && operation.LeaseExpiresAt != null
                && operation.LeaseExpiresAt < now)
            .Take(BrandPolicy.EmbeddingClaimBatchSize)
            .ToListAsync(cancellationToken);

        var requeued = 0;
        var abandoned = 0;

        foreach (var operation in lapsed)
        {
            operation.LeasedBy = null;
            operation.LeaseExpiresAt = null;
            operation.StatusChangedAt = now;

            if (operation.Attempts >= BrandPolicy.EmbeddingMaxAttempts)
            {
                // The bound: without it a document that kills its worker every time cycles forever.
                operation.Status = BrandSourceEmbeddingOperationStatus.Failed;
                operation.FailureCategory = BrandSourceEmbeddingFailureCategory.LeaseAbandoned;
                operation.FailureSummary =
                    $"Claimed {operation.Attempts} times and never completed; no attempts remain.";
                operation.CompletedAt = now;
                abandoned++;
            }
            else
            {
                operation.Status = BrandSourceEmbeddingOperationStatus.Queued;
                operation.AvailableAt = now + BrandPolicy.EmbeddingBackoffFor(operation.Attempts, Random.Shared.NextDouble());
                requeued++;
            }
        }

        if (lapsed.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return (requeued, abandoned);
    }

    /// <summary>Superseded sets past retention and building sets nobody has touched for a day.</summary>
    public async Task<IReadOnlyList<BrandSourceRetirableSet>> FindRetirableSetsAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var supersededBefore = now - BrandPolicy.SupersededSetRetention;
        var abandonedBefore = now - BrandPolicy.AbandonedBuildAge;

        return await context.BrandSourceChunkSets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(set =>
                (set.Status == BrandSourceChunkSetStatus.Superseded
                    && (set.SupersededAt == null || set.SupersededAt <= supersededBefore))
                || (set.Status == BrandSourceChunkSetStatus.Building && set.CreatedAt <= abandonedBefore))
            .OrderBy(set => set.CreatedAt)
            .Take(BrandPolicy.EmbeddingMaintenanceBatchSize)
            .Select(set => new BrandSourceRetirableSet(set.WorkspaceId, set.Id))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Latest succeeded artifacts of live documents that have no current set from <paramref name="model"/> cut
    /// by the current chunker, no operation already waiting, and no earlier failure that retrying cannot fix.
    /// </summary>
    /// <remarks>
    /// This is how a changed deployment or chunker re-embeds what it already embedded, and how an extraction
    /// whose own enqueue was missed — or refused for want of a deployment — is picked up once one exists.
    /// A failure other than "no deployment is configured" is excluded for the model it failed under: without
    /// that, a document that always fails would be queued again every minute for the life of the deployment.
    /// </remarks>
    public async Task<IReadOnlyList<BrandSourceEmbeddingBackfill>> FindBackfillAsync(
        string model, CancellationToken cancellationToken)
    {
        return await (
            from extraction in context.BrandSourceExtractions.IgnoreQueryFilters().AsNoTracking()
            where extraction.Status == BrandSourceExtractionStatus.Succeeded
                && !context.BrandSourceExtractions.IgnoreQueryFilters().Any(newer =>
                    newer.WorkspaceId == extraction.WorkspaceId
                    && newer.BrandSourceDocumentVersionId == extraction.BrandSourceDocumentVersionId
                    && newer.Ordinal > extraction.Ordinal)
            join version in context.BrandSourceDocumentVersions.IgnoreQueryFilters().AsNoTracking()
                on new { extraction.WorkspaceId, Id = extraction.BrandSourceDocumentVersionId }
                equals new { version.WorkspaceId, version.Id }
            join document in context.BrandSourceDocuments.IgnoreQueryFilters().AsNoTracking()
                on new { version.WorkspaceId, Id = version.BrandSourceDocumentId }
                equals new { document.WorkspaceId, document.Id }
            where document.Status != BrandSourceDocumentStatus.Removed
                && !context.BrandSourceChunkSets.IgnoreQueryFilters().Any(set =>
                    set.WorkspaceId == extraction.WorkspaceId
                    && set.BrandSourceExtractionId == extraction.Id
                    && set.Status == BrandSourceChunkSetStatus.Current
                    && set.EmbeddingModel == model
                    && set.ChunkerId == BrandSourceChunker.Id)
                && !context.BrandSourceEmbeddingOperations.IgnoreQueryFilters().Any(operation =>
                    operation.WorkspaceId == extraction.WorkspaceId
                    && operation.BrandSourceExtractionId == extraction.Id
                    && (operation.Status == BrandSourceEmbeddingOperationStatus.Queued
                        || operation.Status == BrandSourceEmbeddingOperationStatus.Running
                        || (operation.Status == BrandSourceEmbeddingOperationStatus.Failed
                            && operation.FailureCategory != BrandSourceEmbeddingFailureCategory.ProviderNotConfigured
                            && (operation.EmbeddingModel == null || operation.EmbeddingModel == model))))
            orderby extraction.CreatedAt
            select new BrandSourceEmbeddingBackfill(extraction.WorkspaceId, extraction.Id))
            .Take(BrandPolicy.EmbeddingMaintenanceBatchSize)
            .ToListAsync(cancellationToken);
    }
}
