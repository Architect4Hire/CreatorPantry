using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Business;

/// <summary>How one claimed embedding operation ended, for the worker's pass summary.</summary>
public enum BrandSourceEmbeddingRunOutcome
{
    /// <summary>A whole set was built and is now the one retrieval reads.</summary>
    Embedded = 1,

    /// <summary>Handed back for another attempt after a failure worth retrying.</summary>
    Requeued = 2,

    Failed = 3,

    Cancelled = 4,

    /// <summary>Nothing was changed: the lease was lost, or the operation is unreadable in this workspace.</summary>
    Skipped = 5,
}

/// <summary>
/// Chunks an extracted artifact, embeds it in bounded batches, and replaces the current set only once the new
/// one is whole.
/// </summary>
public interface IBrandSourceEmbeddingBusiness
{
    Task<BrandSourceEmbeddingRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    Task<bool> RetireSetAsync(Guid setId, CancellationToken cancellationToken);

    Task<bool> EnqueueAsync(Guid extractionId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandSourceEmbeddingBusiness"/>
/// <remarks>
/// <para>
/// <strong>The order is the safety.</strong> Open and verify the artifact, name the model, cut the passages
/// (pure code), resume or begin a <c>Building</c> set, embed and store one batch at a time, and only then
/// promote. Until the last step nothing retrieval reads has changed, so a failure at any point before it —
/// a provider outage, a crash, a lost lease — leaves the current set exactly as it was.
/// </para>
/// <para>
/// Nothing here logs a passage: an embedding job reads creator prose and its failures are described by
/// category and operation id only (ai.md).
/// </para>
/// </remarks>
internal sealed class BrandSourceEmbeddingBusiness(
    IBrandSourceEmbeddingDataLayer dataLayer,
    ILogger<BrandSourceEmbeddingBusiness> logger) : IBrandSourceEmbeddingBusiness
{
    public Task<bool> RetireSetAsync(Guid setId, CancellationToken cancellationToken) =>
        dataLayer.RetireSetAsync(setId, cancellationToken);

    public Task<bool> EnqueueAsync(Guid extractionId, CancellationToken cancellationToken) =>
        dataLayer.EnqueueAsync(extractionId, cancellationToken);

    public async Task<BrandSourceEmbeddingRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(operationId, leaseToken, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not failure. Handed back uncharged and without a backoff, so the next worker takes it
            // up where this one stopped — the batches already stored are kept and resumed. Not cancellable by
            // the token that was just cancelled.
            await dataLayer.ReleaseAsync(operationId, leaseToken, CancellationToken.None);

            throw;
        }
    }

    private async Task<BrandSourceEmbeddingRunOutcome> RunAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var source = await dataLayer.OpenSourceAsync(operationId, leaseToken, cancellationToken);

        switch (source.Outcome)
        {
            case BrandSourceEmbeddingSourceOutcome.Missing:
                // Not readable in the workspace the worker resolved, which is what a claim naming another
                // workspace's row looks like. Nothing can be written: the row is workspace-owned.
                logger.LogWarning(
                    "Embedding {OperationId} is not readable in its resolved workspace; its lease will expire.",
                    operationId);

                return BrandSourceEmbeddingRunOutcome.Skipped;

            case BrandSourceEmbeddingSourceOutcome.LeaseLost:
                return BrandSourceEmbeddingRunOutcome.Skipped;

            case BrandSourceEmbeddingSourceOutcome.DocumentRemoved:
            case BrandSourceEmbeddingSourceOutcome.ArtifactSuperseded:
                return Settle(
                    await dataLayer.CancelAsync(operationId, leaseToken, cancellationToken),
                    BrandSourceEmbeddingRunOutcome.Cancelled);

            case BrandSourceEmbeddingSourceOutcome.ObjectMissing:
                return await FailAsync(
                    operationId,
                    leaseToken,
                    BrandSourceEmbeddingFailureCategory.SourceMissing,
                    "The extracted text this operation names is not there.",
                    cancellationToken);

            case BrandSourceEmbeddingSourceOutcome.ArtifactCorrupt:
                return await FailAsync(
                    operationId,
                    leaseToken,
                    BrandSourceEmbeddingFailureCategory.SourceCorrupt,
                    "The extracted text no longer matches the checksum recorded for it.",
                    cancellationToken);

            case BrandSourceEmbeddingSourceOutcome.StorageUnavailable:
                return await RequeueAsync(
                    operationId,
                    leaseToken,
                    BrandSourceEmbeddingFailureCategory.Storage,
                    "Private storage could not be reached to read the extracted text.",
                    cancellationToken);
        }

        var model = dataLayer.DescribeModel();

        switch (model.Status)
        {
            case BrandSourceEmbeddingModelStatus.NotConfigured:
                return await FailAsync(
                    operationId,
                    leaseToken,
                    BrandSourceEmbeddingFailureCategory.ProviderNotConfigured,
                    "No embedding deployment is configured for this host.",
                    cancellationToken);

            case BrandSourceEmbeddingModelStatus.Unidentified:
                // Provenance is the point of a set. A vector with no named model cannot be told from a
                // neighbour's later, so none is written rather than one with a guessed name.
                return await FailAsync(
                    operationId,
                    leaseToken,
                    BrandSourceEmbeddingFailureCategory.ModelUnidentified,
                    "The embedding deployment does not report which model it is.",
                    cancellationToken);
        }

        var passages = BrandSourceChunker.Chunk(source.Text!);

        if (passages.Count == 0)
        {
            return await FailAsync(
                operationId,
                leaseToken,
                BrandSourceEmbeddingFailureCategory.NoPassages,
                "The extracted text is blank, so there is nothing to embed.",
                cancellationToken);
        }

        var build = await dataLayer.BeginBuildAsync(
            operationId, leaseToken, model.ModelId!, BrandSourceChunker.Id, passages.Count, cancellationToken);

        if (build.Outcome is not BrandSourceEmbeddingWriteOutcome.Applied)
        {
            return BrandSourceEmbeddingRunOutcome.Skipped;
        }

        if (build.WrittenCount > 0)
        {
            logger.LogInformation(
                "Embedding {OperationId} resumes after {Written} of {Total} stored passages.",
                operationId,
                build.WrittenCount,
                passages.Count);
        }

        for (var next = build.WrittenCount; next < passages.Count; next += BrandPolicy.EmbeddingBatchSize)
        {
            var batch = passages.Skip(next).Take(BrandPolicy.EmbeddingBatchSize).ToList();

            var embedded = await dataLayer.EmbedAsync(batch.Select(passage => passage.Text).ToList(), cancellationToken);

            switch (embedded.Outcome)
            {
                case BrandSourceEmbeddingBatchOutcome.NotConfigured:
                    return await FailAsync(
                        operationId,
                        leaseToken,
                        BrandSourceEmbeddingFailureCategory.ProviderNotConfigured,
                        "No embedding deployment is configured for this host.",
                        cancellationToken);

                case BrandSourceEmbeddingBatchOutcome.Unavailable:
                    return await RequeueAsync(
                        operationId,
                        leaseToken,
                        BrandSourceEmbeddingFailureCategory.ProviderUnavailable,
                        "The embedding provider could not be reached or refused the request.",
                        cancellationToken);

                case BrandSourceEmbeddingBatchOutcome.Invalid:
                    // Terminal: the same input to the same deployment gives the same wrong answer, and a
                    // wrong-width vector is never stored to find that out later.
                    return await FailAsync(
                        operationId,
                        leaseToken,
                        BrandSourceEmbeddingFailureCategory.InvalidEmbedding,
                        "The provider returned the wrong number of vectors, or vectors of the wrong width.",
                        cancellationToken);
            }

            var write = await dataLayer.WriteBatchAsync(
                operationId, leaseToken, build.SetId, batch, embedded.Vectors!, cancellationToken);

            if (write is not BrandSourceEmbeddingWriteOutcome.Applied)
            {
                return BrandSourceEmbeddingRunOutcome.Skipped;
            }
        }

        switch (await dataLayer.PromoteAsync(
            operationId, leaseToken, build.SetId, passages.Count, model.ModelId!, cancellationToken))
        {
            case BrandSourceEmbeddingWriteOutcome.Applied:
                return BrandSourceEmbeddingRunOutcome.Embedded;

            case BrandSourceEmbeddingWriteOutcome.Incomplete:
                // The set does not hold what was written to it — which a correct run cannot produce, since
                // batches are atomic. Not promoted; retried, and the retry resumes from what the set does
                // hold or, if that is not a prefix of this run, discards it and rebuilds.
                return await RequeueAsync(
                    operationId,
                    leaseToken,
                    BrandSourceEmbeddingFailureCategory.InvalidEmbedding,
                    "The built set did not hold every passage, so it was not made current.",
                    cancellationToken);

            default:
                return BrandSourceEmbeddingRunOutcome.Skipped;
        }
    }

    private async Task<BrandSourceEmbeddingRunOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceEmbeddingFailureCategory category,
        string summary,
        CancellationToken cancellationToken) =>
        Settle(
            await dataLayer.FailAsync(operationId, leaseToken, category, summary, cancellationToken),
            BrandSourceEmbeddingRunOutcome.Failed);

    private async Task<BrandSourceEmbeddingRunOutcome> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceEmbeddingFailureCategory category,
        string summary,
        CancellationToken cancellationToken)
    {
        var result = await dataLayer.RequeueAsync(operationId, leaseToken, category, summary, cancellationToken);

        if (result.Outcome is not BrandSourceEmbeddingWriteOutcome.Applied)
        {
            return BrandSourceEmbeddingRunOutcome.Skipped;
        }

        return result.Abandoned
            ? BrandSourceEmbeddingRunOutcome.Failed
            : BrandSourceEmbeddingRunOutcome.Requeued;
    }

    private static BrandSourceEmbeddingRunOutcome Settle(
        BrandSourceEmbeddingWriteOutcome outcome, BrandSourceEmbeddingRunOutcome applied) =>
        outcome is BrandSourceEmbeddingWriteOutcome.Applied ? applied : BrandSourceEmbeddingRunOutcome.Skipped;
}
