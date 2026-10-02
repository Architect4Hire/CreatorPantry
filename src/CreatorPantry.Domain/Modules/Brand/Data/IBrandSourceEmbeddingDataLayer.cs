using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public enum BrandSourceEmbeddingSourceOutcome
{
    /// <summary>The artifact was read and verified; text is present.</summary>
    Opened = 1,

    /// <summary>Not readable in the resolved workspace. Nothing can be recorded against it.</summary>
    Missing = 2,

    LeaseLost = 3,

    /// <summary>The document was removed while the job waited.</summary>
    DocumentRemoved = 4,

    /// <summary>A newer artifact exists for the version, and has its own operation.</summary>
    ArtifactSuperseded = 5,

    ObjectMissing = 6,

    StorageUnavailable = 7,

    /// <summary>The bytes no longer hash to the checksum the extraction recorded, or are not UTF-8.</summary>
    ArtifactCorrupt = 8,
}

public sealed record BrandSourceEmbeddingTarget(Guid OperationId, Guid DocumentId, Guid VersionId, Guid ExtractionId);

public sealed record BrandSourceEmbeddingSource(
    BrandSourceEmbeddingSourceOutcome Outcome,
    BrandSourceEmbeddingTarget? Target = null,
    string? Text = null);

public enum BrandSourceEmbeddingWriteOutcome
{
    Applied = 1,

    /// <summary>This worker no longer holds the operation. Nothing it did was kept.</summary>
    LeaseLost = 2,

    /// <summary>The set does not hold the passages it was expected to. It is not promoted.</summary>
    Incomplete = 3,
}

/// <param name="WrittenCount">How many leading passages an earlier attempt already stored in this set.</param>
public sealed record BrandSourceEmbeddingBuild(
    BrandSourceEmbeddingWriteOutcome Outcome, Guid SetId = default, int WrittenCount = 0);

public sealed record BrandSourceEmbeddingRequeueResult(BrandSourceEmbeddingWriteOutcome Outcome, bool Abandoned);

/// <summary>
/// Persistence and provider choices for the embedding job: reads the pinned artifact, builds a chunk set beside
/// the current one, and swaps it in only when it is whole.
/// </summary>
public interface IBrandSourceEmbeddingDataLayer
{
    /// <summary>
    /// Reads the operation's extracted artifact, under its lease, and verifies it against its recorded checksum.
    /// </summary>
    Task<BrandSourceEmbeddingSource> OpenSourceAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    BrandSourceEmbeddingModel DescribeModel();

    Task<BrandSourceEmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the <c>Building</c> set a crashed attempt left for this extraction and model and offers its
    /// stored passages for reuse, or discards it and starts a new one. Records the model on the operation.
    /// </summary>
    Task<BrandSourceEmbeddingBuild> BeginBuildAsync(
        Guid operationId,
        Guid leaseToken,
        string model,
        string chunkerId,
        int totalPassages,
        CancellationToken cancellationToken);

    /// <summary>Stores one embedded batch in the building set and renews the lease, in one save.</summary>
    Task<BrandSourceEmbeddingWriteOutcome> WriteBatchAsync(
        Guid operationId,
        Guid leaseToken,
        Guid setId,
        IReadOnlyList<BrandSourceChunkPassage> passages,
        IReadOnlyList<float[]> vectors,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes a whole set current and the sets it replaces superseded, and completes the operation — one
    /// transaction, so no reader sees two current sets or none.
    /// </summary>
    Task<BrandSourceEmbeddingWriteOutcome> PromoteAsync(
        Guid operationId, Guid leaseToken, Guid setId, int passageCount, string model, CancellationToken cancellationToken);

    Task<BrandSourceEmbeddingWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceEmbeddingFailureCategory category,
        string summary,
        CancellationToken cancellationToken);

    Task<BrandSourceEmbeddingWriteOutcome> CancelAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    Task<BrandSourceEmbeddingRequeueResult> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceEmbeddingFailureCategory category,
        string summary,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hands a running operation back, uncharged, for a worker that is shutting down: not a failure and not an
    /// attempt, so a deploy cannot spend a document's retries.
    /// </summary>
    Task<BrandSourceEmbeddingWriteOutcome> ReleaseAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>Deletes a set that is superseded past retention, or building and abandoned. False if neither.</summary>
    Task<bool> RetireSetAsync(Guid setId, CancellationToken cancellationToken);

    /// <summary>Queues an embedding for an artifact that has none current. False if it need not or cannot be.</summary>
    Task<bool> EnqueueAsync(Guid extractionId, CancellationToken cancellationToken);
}

internal sealed class BrandSourceEmbeddingDataLayer(
    IBrandSourceEmbeddingRepository embeddings,
    IBrandSourceObjectGateway objects,
    IBrandSourceEmbeddingGateway provider,
    IAuditWriter auditWriter,
    IWorkspaceContext workspace,
    CreatorPantryDbContext context,
    IClock clock,
    ILogger<BrandSourceEmbeddingDataLayer> logger) : IBrandSourceEmbeddingDataLayer
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<BrandSourceEmbeddingSource> OpenSourceAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        if (await embeddings.FindTargetAsync(operationId, cancellationToken) is not { } record)
        {
            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.Missing);
        }

        if (record.OperationStatus is not BrandSourceEmbeddingOperationStatus.Running || record.LeasedBy != leaseToken)
        {
            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.LeaseLost);
        }

        if (record.DocumentStatus is BrandSourceDocumentStatus.Removed)
        {
            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.DocumentRemoved);
        }

        if (record.HasNewerExtraction)
        {
            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.ArtifactSuperseded);
        }

        byte[] bytes;

        try
        {
            await using var stored = await objects.OpenReadAsync(record.ExtractedTextObjectKey, cancellationToken);

            if (stored is null)
            {
                logger.LogError("Embedding {OperationId} names an extraction with no stored text.", operationId);

                return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.ObjectMissing);
            }

            using var buffer = new MemoryStream();
            await stored.Content.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "Private storage could not be reached reading the artifact of embedding {OperationId}.",
                operationId);

            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.StorageUnavailable);
        }

        // The chunk offsets and checksums are measured against these exact bytes, so they are verified before
        // anything is cut from them: a chunk of text that is not the artifact's is provenance that lies.
        if ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) != record.ContentChecksum)
        {
            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.ArtifactCorrupt);
        }

        string text;

        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new BrandSourceEmbeddingSource(BrandSourceEmbeddingSourceOutcome.ArtifactCorrupt);
        }

        return new BrandSourceEmbeddingSource(
            BrandSourceEmbeddingSourceOutcome.Opened,
            new BrandSourceEmbeddingTarget(record.OperationId, record.DocumentId, record.VersionId, record.ExtractionId),
            text);
    }

    public BrandSourceEmbeddingModel DescribeModel() => provider.Describe();

    public Task<BrandSourceEmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        provider.EmbedAsync(texts, cancellationToken);

    public async Task<BrandSourceEmbeddingBuild> BeginBuildAsync(
        Guid operationId,
        Guid leaseToken,
        string model,
        string chunkerId,
        int totalPassages,
        CancellationToken cancellationToken)
    {
        var operation = await embeddings.FindForUpdateAsync(operationId, cancellationToken);

        if (!HoldsLease(operation, leaseToken))
        {
            return new BrandSourceEmbeddingBuild(BrandSourceEmbeddingWriteOutcome.LeaseLost);
        }

        operation!.EmbeddingModel = model;

        var existing = await embeddings.FindBuildingSetAsync(operation.BrandSourceExtractionId, model, cancellationToken);
        var written = 0;

        if (existing is not null)
        {
            written = existing.ChunkerId == chunkerId
                ? await embeddings.CountChunksAsync(existing.Id, cancellationToken)
                : -1;

            if (written is >= 0 && written <= totalPassages)
            {
                // The artifact is immutable and the chunker deterministic, so what an earlier attempt stored is
                // exactly what this one would have written: resume after it rather than pay for it twice.
                return await SaveBuildAsync(new BrandSourceEmbeddingBuild(
                    BrandSourceEmbeddingWriteOutcome.Applied, existing.Id, written));
            }

            // A different chunker, or more passages than exist: not a prefix of this run. Discard it, and save
            // that delete before the replacement is added so the one-building-set index never sees both.
            embeddings.Remove(existing);

            if (!await SaveAsync(cancellationToken))
            {
                return new BrandSourceEmbeddingBuild(BrandSourceEmbeddingWriteOutcome.LeaseLost);
            }
        }

        var set = new BrandSourceChunkSet
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            BrandSourceDocumentId = operation.BrandSourceDocumentId,
            BrandSourceDocumentVersionId = operation.BrandSourceDocumentVersionId,
            BrandSourceExtractionId = operation.BrandSourceExtractionId,
            SourceStatus = BrandSourceExtractionStatus.Succeeded,
            Status = BrandSourceChunkSetStatus.Building,
            ChunkerId = chunkerId,
            EmbeddingModel = model,
            EmbeddingDimension = BrandPolicy.EmbeddingDimension,
            ChunkCount = 0,
            CreatedAt = clock.UtcNow,
        };

        embeddings.Add(set);

        return await SaveBuildAsync(new BrandSourceEmbeddingBuild(BrandSourceEmbeddingWriteOutcome.Applied, set.Id, 0));

        async Task<BrandSourceEmbeddingBuild> SaveBuildAsync(BrandSourceEmbeddingBuild build) =>
            await SaveAsync(cancellationToken) ? build : new BrandSourceEmbeddingBuild(BrandSourceEmbeddingWriteOutcome.LeaseLost);
    }

    public async Task<BrandSourceEmbeddingWriteOutcome> WriteBatchAsync(
        Guid operationId,
        Guid leaseToken,
        Guid setId,
        IReadOnlyList<BrandSourceChunkPassage> passages,
        IReadOnlyList<float[]> vectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentNullException.ThrowIfNull(vectors);

        if (passages.Count == 0 || passages.Count != vectors.Count)
        {
            throw new ArgumentException("A batch needs one vector per passage.", nameof(vectors));
        }

        // Local to the writer, so "a wrong-width vector is never stored" does not rest on the gateway alone.
        if (vectors.Any(vector => vector.Length != BrandPolicy.EmbeddingDimension))
        {
            throw new ArgumentException("A vector is not the width the column holds.", nameof(vectors));
        }

        var operation = await embeddings.FindForUpdateAsync(operationId, cancellationToken);

        if (!HoldsLease(operation, leaseToken))
        {
            return BrandSourceEmbeddingWriteOutcome.LeaseLost;
        }

        var set = await embeddings.FindSetForUpdateAsync(setId, cancellationToken);

        if (set is null || set.Status is not BrandSourceChunkSetStatus.Building
            || set.BrandSourceExtractionId != operation!.BrandSourceExtractionId)
        {
            return BrandSourceEmbeddingWriteOutcome.LeaseLost;
        }

        embeddings.Add(passages.Select((passage, index) => new BrandSourceChunk
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            BrandSourceChunkSetId = set.Id,
            Ordinal = passage.Ordinal,
            StartByteOffset = passage.StartByteOffset,
            ByteLength = passage.ByteLength,
            ContentChecksum = passage.ContentChecksum,
            Text = passage.Text,
            Embedding = new SqlVector<float>(vectors[index]),
        }));

        // Renewed with every batch, so the lease has to outlast one provider call and not the whole document —
        // which is also what lets recovery stay quick for a worker that actually died.
        var now = clock.UtcNow;
        operation.LeaseExpiresAt = now + BrandPolicy.EmbeddingLeaseDuration;

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return BrandSourceEmbeddingWriteOutcome.Applied;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();

            return BrandSourceEmbeddingWriteOutcome.LeaseLost;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();

            // The unique index on (workspace, set, ordinal): another worker already stored these passages,
            // which means it holds the lease this one lost. Anything else is a fault and is not hidden.
            if (await embeddings.CountChunksAsync(setId, CancellationToken.None) >= passages[^1].Ordinal)
            {
                return BrandSourceEmbeddingWriteOutcome.LeaseLost;
            }

            throw;
        }
    }

    public async Task<BrandSourceEmbeddingWriteOutcome> PromoteAsync(
        Guid operationId, Guid leaseToken, Guid setId, int passageCount, string model, CancellationToken cancellationToken)
    {
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A retrying strategy re-runs the whole unit, so each attempt starts from a clean tracker.
            context.ChangeTracker.Clear();

            var operation = await embeddings.FindForUpdateAsync(operationId, cancellationToken);

            if (!HoldsLease(operation, leaseToken))
            {
                return BrandSourceEmbeddingWriteOutcome.LeaseLost;
            }

            var set = await embeddings.FindSetForUpdateAsync(setId, cancellationToken);

            if (set is null || set.Status is not BrandSourceChunkSetStatus.Building
                || set.BrandSourceExtractionId != operation!.BrandSourceExtractionId)
            {
                return BrandSourceEmbeddingWriteOutcome.LeaseLost;
            }

            if (await embeddings.CountChunksAsync(setId, cancellationToken) != passageCount)
            {
                return BrandSourceEmbeddingWriteOutcome.Incomplete;
            }

            var now = clock.UtcNow;
            var replaced = await embeddings.FindReplacedByAsync(
                operation.BrandSourceExtractionId, operation.BrandSourceDocumentVersionId, setId, cancellationToken);

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // First, and saved on its own: the one-current-set index would refuse the new set becoming
                // current while the old one still is, and EF does not promise which UPDATE goes first. Both
                // saves are one transaction, so a reader still never sees two current sets, or none.
                if (replaced.Count > 0)
                {
                    foreach (var old in replaced)
                    {
                        old.Status = BrandSourceChunkSetStatus.Superseded;
                        old.SupersededAt = now;
                    }

                    await context.SaveChangesAsync(cancellationToken);
                }

                set.Status = BrandSourceChunkSetStatus.Current;
                set.ChunkCount = passageCount;
                set.EmbeddedAt = now;

                operation.Status = BrandSourceEmbeddingOperationStatus.Completed;
                operation.BrandSourceChunkSetId = set.Id;
                operation.StatusChangedAt = now;
                operation.CompletedAt = now;
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;

                auditWriter.Record(new AuditEntry(

                    // No actor: a worker running as the workspace's service identity is not a person.
                    ActorUserId: null,
                    BrandAuditActions.SourceDocumentEmbedded,
                    BrandAuditActions.SourceDocumentResourceType,
                    operation.BrandSourceDocumentId.ToString(),
                    CorrelationId: operation.Id,
                    Summary: $"{passageCount} passages embedded by {model}; {replaced.Count} earlier set(s) superseded."));

                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return BrandSourceEmbeddingWriteOutcome.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A recovery sweep took the lease, or another worker promoted first. Nothing of this attempt
                // may stay staged or committed.
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();

                return BrandSourceEmbeddingWriteOutcome.LeaseLost;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                throw;
            }
        });
    }

    public Task<BrandSourceEmbeddingWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceEmbeddingFailureCategory category,
        string summary,
        CancellationToken cancellationToken) =>
        SettleAsync(
            operationId,
            leaseToken,
            (operation, now) =>
            {
                operation.Status = BrandSourceEmbeddingOperationStatus.Failed;
                operation.FailureCategory = category;
                operation.FailureSummary = Summary(summary);
                operation.CompletedAt = now;
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;
            },
            cancellationToken);

    public Task<BrandSourceEmbeddingWriteOutcome> CancelAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken) =>
        SettleAsync(
            operationId,
            leaseToken,
            (operation, now) =>
            {
                operation.Status = BrandSourceEmbeddingOperationStatus.Cancelled;
                operation.CompletedAt = now;
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;
            },
            cancellationToken);

    public async Task<BrandSourceEmbeddingRequeueResult> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceEmbeddingFailureCategory category,
        string summary,
        CancellationToken cancellationToken)
    {
        var abandoned = false;

        var outcome = await SettleAsync(
            operationId,
            leaseToken,
            (operation, now) =>
            {
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;

                if (operation.Attempts >= BrandPolicy.EmbeddingMaxAttempts)
                {
                    operation.Status = BrandSourceEmbeddingOperationStatus.Failed;
                    operation.FailureCategory = category;
                    operation.FailureSummary = Summary(summary);
                    operation.CompletedAt = now;
                    abandoned = true;
                }
                else
                {
                    operation.Status = BrandSourceEmbeddingOperationStatus.Queued;
                    operation.AvailableAt = now
                        + BrandPolicy.EmbeddingBackoffFor(operation.Attempts, Random.Shared.NextDouble());
                }
            },
            cancellationToken);

        return new BrandSourceEmbeddingRequeueResult(outcome, abandoned);
    }

    public Task<BrandSourceEmbeddingWriteOutcome> ReleaseAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        // A batch whose save was cancelled part-way is still staged on this context; committing the release
        // must not commit it too.
        context.ChangeTracker.Clear();

        return SettleAsync(
            operationId,
            leaseToken,
            (operation, now) =>
            {
                operation.Status = BrandSourceEmbeddingOperationStatus.Queued;
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;
                operation.AvailableAt = now;
                operation.Attempts = Math.Max(0, operation.Attempts - 1);
            },
            cancellationToken);
    }

    public async Task<bool> RetireSetAsync(Guid setId, CancellationToken cancellationToken)
    {
        var set = await embeddings.FindSetForUpdateAsync(setId, cancellationToken);

        if (set is null)
        {
            return false;
        }

        var now = clock.UtcNow;

        var retirable = set.Status switch
        {
            // A null SupersededAt is a set replaced before the time was recorded: nothing is owed a window.
            BrandSourceChunkSetStatus.Superseded =>
                set.SupersededAt is null || set.SupersededAt <= now - BrandPolicy.SupersededSetRetention,

            // A build nobody is finishing. Re-checked here, in the workspace, because a live operation may
            // have claimed it since the sweep looked.
            BrandSourceChunkSetStatus.Building =>
                set.CreatedAt <= now - BrandPolicy.AbandonedBuildAge
                && !await embeddings.HasLiveOperationAsync(set.BrandSourceExtractionId, cancellationToken),

            _ => false,
        };

        if (!retirable)
        {
            return false;
        }

        // The chunks go with it through the cascade the database owns, so nothing here loads six kilobytes of
        // vector per row to delete it.
        embeddings.Remove(set);

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();

            return false;
        }
    }

    public async Task<bool> EnqueueAsync(Guid extractionId, CancellationToken cancellationToken)
    {
        if (await embeddings.FindLatestSucceededAsync(extractionId, cancellationToken) is not { } target
            || await embeddings.HasLiveOperationAsync(extractionId, cancellationToken))
        {
            return false;
        }

        embeddings.Enqueue(BrandSourceEmbeddingQueue.For(
            workspace.WorkspaceId, target.DocumentId, target.ExtractionId, target.VersionId, clock.UtcNow));

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException)
        {
            // The live-operation index: a commit queued one between the check and the write. That is the
            // outcome this wanted, reached by another route.
            context.ChangeTracker.Clear();

            return false;
        }
    }

    private static bool HoldsLease(BrandSourceEmbeddingOperation? operation, Guid leaseToken) =>
        operation is { Status: BrandSourceEmbeddingOperationStatus.Running } && operation.LeasedBy == leaseToken;

    /// <summary>Saves, reporting a lost race as false. Nothing may stay staged for the next save to commit.</summary>
    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();

            return false;
        }
    }

    private async Task<BrandSourceEmbeddingWriteOutcome> SettleAsync(
        Guid operationId,
        Guid leaseToken,
        Action<BrandSourceEmbeddingOperation, DateTimeOffset> apply,
        CancellationToken cancellationToken)
    {
        var operation = await embeddings.FindForUpdateAsync(operationId, cancellationToken);

        if (!HoldsLease(operation, leaseToken))
        {
            return BrandSourceEmbeddingWriteOutcome.LeaseLost;
        }

        var now = clock.UtcNow;
        operation!.StatusChangedAt = now;
        apply(operation, now);

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return BrandSourceEmbeddingWriteOutcome.Applied;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();

            return BrandSourceEmbeddingWriteOutcome.LeaseLost;
        }
    }

    private static string? Summary(string? summary) =>
        string.IsNullOrWhiteSpace(summary)
            ? null
            : summary.Length <= BrandPolicy.ExtractionFailureSummaryMaxLength
                ? summary
                : summary[..BrandPolicy.ExtractionFailureSummaryMaxLength];
}
