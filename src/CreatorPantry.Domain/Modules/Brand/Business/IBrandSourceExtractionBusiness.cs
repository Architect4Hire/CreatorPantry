using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Business;

/// <summary>How one claimed extraction ended, from the worker's point of view.</summary>
public enum BrandSourceExtractionRunOutcome
{
    /// <summary>Text was recovered and stored.</summary>
    Extracted = 1,

    /// <summary>
    /// The document was read and there is nothing to extract, or it could not be read at all. An extraction row
    /// records which; either way the work is done and retrying the same bytes would give the same answer.
    /// </summary>
    Reviewed = 2,

    /// <summary>A transient fault. Back in the queue with backoff.</summary>
    Requeued = 3,

    /// <summary>Terminal without an extraction: the source is gone, or the attempts are spent.</summary>
    Failed = 4,

    /// <summary>There was nothing left to extract. Not a failure.</summary>
    Cancelled = 5,

    /// <summary>
    /// Nothing was written and nothing needs to be: the lease moved to another worker, or the operation is no
    /// longer readable. Whoever holds it now is authoritative.
    /// </summary>
    Skipped = 6,
}

/// <summary>
/// The domain rules of reading one document version as text: which parser, what counts as a review state, and
/// what is worth trying again.
/// </summary>
/// <remarks>
/// Reached only from <see cref="CreatorPantry.Domain.Modules.Brand.Facade.IBrandSourceExtractionFacade"/>, which
/// the worker calls after it has resolved and validated the workspace. Nothing here takes a workspace id: the
/// ambient <c>IWorkspaceContext</c> is already resolved by the time this runs, and every read below it is
/// filtered by that.
/// </remarks>
public interface IBrandSourceExtractionBusiness
{
    /// <inheritdoc cref="Facade.IBrandSourceExtractionFacade.ExecuteAsync"/>
    Task<BrandSourceExtractionRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);
}

internal sealed class BrandSourceExtractionBusiness(
    IBrandSourceExtractionDataLayer dataLayer,
    ILogger<BrandSourceExtractionBusiness> logger) : IBrandSourceExtractionBusiness
{
    public async Task<BrandSourceExtractionRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        using var source = await dataLayer.OpenSourceAsync(operationId, leaseToken, cancellationToken);

        switch (source.Outcome)
        {
            case BrandSourceExtractionSourceOutcome.Missing:
                // The operation is not readable in the workspace the worker resolved. Nothing can be written:
                // the row itself is workspace-owned, so there is no way to record an outcome against it.
                logger.LogWarning(
                    "Extraction {OperationId} is not readable in its resolved workspace; its lease will expire.",
                    operationId);

                return BrandSourceExtractionRunOutcome.Skipped;

            case BrandSourceExtractionSourceOutcome.LeaseLost:
                return BrandSourceExtractionRunOutcome.Skipped;

            case BrandSourceExtractionSourceOutcome.DocumentRemoved:
                return Settle(
                    await dataLayer.CancelAsync(operationId, leaseToken, cancellationToken),
                    BrandSourceExtractionRunOutcome.Cancelled);

            case BrandSourceExtractionSourceOutcome.ObjectMissing:
                // Terminal, not requeued: the upload writes the object before the row that names it, so a
                // missing object is not a fault that passes. Retrying would spend the attempt bound and then
                // report LeaseAbandoned, which names the wrong reason.
                return Settle(
                    await dataLayer.FailAsync(
                        operationId,
                        leaseToken,
                        BrandSourceExtractionFailureCategory.SourceMissing,
                        "The stored file this version names is not there.",
                        cancellationToken),
                    BrandSourceExtractionRunOutcome.Failed);

            case BrandSourceExtractionSourceOutcome.StorageUnavailable:
                return await RequeueAsync(
                    operationId,
                    leaseToken,
                    BrandSourceExtractionFailureCategory.Storage,
                    "Private storage could not be reached to read the file.",
                    cancellationToken);
        }

        var target = source.Target!;
        var extractor = BrandSourceTextExtractors.For(target.MediaType);

        if (extractor is null)
        {
            // A deployment that accepts a format nothing reads. Terminal rather than requeued, because
            // retrying cannot fix it — and failed rather than recorded as a review state, because this is not a
            // fact about the document: the creator's file is fine and the server is not.
            logger.LogError(
                "Extraction {OperationId} has no reader for media type {MediaType}.",
                operationId,
                target.MediaType);

            return Settle(
                await dataLayer.FailAsync(
                    operationId,
                    leaseToken,
                    BrandSourceExtractionFailureCategory.SourceUnreadable,
                    $"No reader is registered for media type '{target.MediaType}'.",
                    cancellationToken),
                BrandSourceExtractionRunOutcome.Failed);
        }

        // Untrusted input, parsed by inert code: nothing below opens a connection, resolves an entity, runs a
        // macro or calls a model. An exception from here is a defect in the parser rather than a fact about the
        // document, so it is deliberately not caught — the worker leaves the lease to lapse and recovery bounds
        // the retries, which is how a poison document stops rather than cycles.
        var extracted = await extractor.ExtractAsync(source.Content!, cancellationToken);

        var write = await dataLayer.CompleteAsync(
            target, leaseToken, extractor.ExtractorId, extracted, cancellationToken);

        switch (write)
        {
            case BrandSourceExtractionWriteOutcome.Applied:
                return extracted.Outcome is BrandSourceTextExtractionOutcome.Extracted
                    ? BrandSourceExtractionRunOutcome.Extracted
                    : BrandSourceExtractionRunOutcome.Reviewed;

            case BrandSourceExtractionWriteOutcome.AlreadyExtracted:
                // Another writer took the ordinal this attempt offered, so the version already has the
                // extraction this was going to give it. Cancelled rather than failed: nothing went wrong, and
                // the operation still holds its lease, so it has to be ended here rather than left running.
                logger.LogInformation(
                    "Extraction {OperationId} found its version already extracted; nothing further to do.",
                    operationId);

                return Settle(
                    await dataLayer.CancelAsync(operationId, leaseToken, cancellationToken),
                    BrandSourceExtractionRunOutcome.Cancelled);

            case BrandSourceExtractionWriteOutcome.StorageUnavailable:
                return await RequeueAsync(
                    operationId,
                    leaseToken,
                    BrandSourceExtractionFailureCategory.Storage,
                    "Private storage could not be reached to store the extracted text.",
                    cancellationToken);

            default:
                return BrandSourceExtractionRunOutcome.Skipped;
        }
    }

    private async Task<BrandSourceExtractionRunOutcome> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceExtractionFailureCategory category,
        string summary,
        CancellationToken cancellationToken)
    {
        var result = await dataLayer.RequeueAsync(operationId, leaseToken, category, summary, cancellationToken);

        if (result.Outcome is not BrandSourceExtractionWriteOutcome.Applied)
        {
            return BrandSourceExtractionRunOutcome.Skipped;
        }

        return result.Abandoned
            ? BrandSourceExtractionRunOutcome.Failed
            : BrandSourceExtractionRunOutcome.Requeued;
    }

    /// <summary>
    /// The outcome a settled write reports, or <see cref="BrandSourceExtractionRunOutcome.Skipped"/> when the
    /// lease had already gone and the write changed nothing.
    /// </summary>
    private static BrandSourceExtractionRunOutcome Settle(
        BrandSourceExtractionWriteOutcome outcome, BrandSourceExtractionRunOutcome applied) =>
        outcome is BrandSourceExtractionWriteOutcome.Applied ? applied : BrandSourceExtractionRunOutcome.Skipped;
}
