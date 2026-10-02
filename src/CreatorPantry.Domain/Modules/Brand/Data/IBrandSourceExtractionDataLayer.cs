using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>
/// What one claimed extraction is about. <strong>No object key, no container, no URL</strong> — the pointer stays
/// inside the data layer, as it does for a download.
/// </summary>
public sealed record BrandSourceExtractionTarget(
    Guid OperationId,
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string MediaType,
    long SizeBytes);

public enum BrandSourceExtractionSourceOutcome
{
    Opened = 1,

    /// <summary>
    /// No such operation in the resolved workspace. One answer for an id that was never issued, another
    /// workspace's operation, and one whose workspace no longer resolves — deliberately indistinguishable.
    /// </summary>
    Missing = 2,

    /// <summary>
    /// The operation is not running under the lease this worker holds: a recovery sweep took it back while this
    /// worker was between steps. The holder's outcome is authoritative, so this one writes nothing.
    /// </summary>
    LeaseLost = 3,

    /// <summary>The document was removed while this waited. There is nothing left to extract.</summary>
    DocumentRemoved = 4,

    /// <summary>
    /// A committed version naming bytes that are not there. The upload writes the object before the row, so
    /// this means something outside the application removed it.
    /// </summary>
    ObjectMissing = 5,

    /// <summary>Private storage could not be reached. Nothing is known about the object either way.</summary>
    StorageUnavailable = 6,
}

/// <summary>
/// One document version's bytes, buffered, with what is known about the version around them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Buffered rather than streamed, deliberately.</strong> Two of the parsers need to seek — a ZIP central
/// directory is at the end of the file, and a PDF's cross-reference table is read before its pages — and an
/// object store's read stream makes no such promise. Buffering also releases the store's lease before parsing
/// starts, so a slow document does not hold a connection for the length of its own parse. The ceiling is
/// <see cref="BrandPolicy.SourceUploadMaxBytes"/>, which the upload already enforced.
/// </para>
/// <para>
/// <see cref="Content"/> is owned by the caller and must be disposed. Its bytes are untrusted input: whatever a
/// parser makes of them is data, never instruction (ai.md).
/// </para>
/// </remarks>
public sealed record BrandSourceExtractionSource(
    BrandSourceExtractionSourceOutcome Outcome,
    BrandSourceExtractionTarget? Target = null,
    Stream? Content = null) : IDisposable
{
    public void Dispose() => Content?.Dispose();
}

public enum BrandSourceExtractionWriteOutcome
{
    /// <summary>The write committed.</summary>
    Applied = 1,

    /// <summary>
    /// Another worker holds this operation now. Nothing was written and anything this attempt had stored was
    /// removed again: the holder's outcome is the one that counts.
    /// </summary>
    LeaseLost = 2,

    /// <summary>
    /// The ordinal this attempt claimed was taken by another writer. Nothing was written and this attempt's
    /// object was removed; the version has its extraction either way, so there is nothing left to do.
    /// </summary>
    AlreadyExtracted = 3,

    /// <summary>The extracted text could not be stored. Nothing was written, and the operation still holds its lease.</summary>
    StorageUnavailable = 4,
}

/// <param name="Abandoned">
/// True when the attempt bound was already spent, so the operation was failed rather than returned to the queue.
/// </param>
public sealed record BrandSourceExtractionRequeueResult(BrandSourceExtractionWriteOutcome Outcome, bool Abandoned);

/// <summary>
/// One numbered version of one document, and the two facts a refusal is decided from.
/// </summary>
/// <remarks>
/// No object key: this is the shape Business sees, and the pointer stays in the data layer as it does for a
/// download. <paramref name="CurrentVersionNumber"/> is the document's, not this version's — it is what says
/// whether this version is still the one being worked on.
/// </remarks>
public sealed record BrandSourceVersionReview(
    Guid DocumentId,
    BrandSourceDocumentStatus DocumentStatus,
    int CurrentVersionNumber,
    Guid VersionId,
    int VersionNumber);

/// <summary>
/// One extraction artifact as Business sees it: everything the row records except where the text is.
/// </summary>
/// <param name="ContentChecksum">
/// <c>sha256:</c> and the digest, present exactly when <paramref name="Status"/> is
/// <see cref="BrandSourceExtractionStatus.Succeeded"/>. A verification aid rather than an address.
/// </param>
public sealed record BrandSourceExtractionArtifact(
    Guid Id,
    int Ordinal,
    BrandSourceExtractionStatus Status,
    BrandSourceExtractionOrigin Origin,
    string? ContentChecksum,
    string? Reason,
    DateTimeOffset CreatedAt);

public enum BrandSourceExtractionReadOutcome
{
    /// <summary>The artifact was read. Its text is present exactly when its status is Succeeded.</summary>
    Read = 1,

    /// <summary>Nothing has extracted this version yet, so there is no artifact at all.</summary>
    NotExtracted = 2,

    /// <summary>
    /// A committed artifact naming text that is not there. The worker writes the object before the row, so this
    /// means something outside the application removed it.
    /// </summary>
    ObjectMissing = 3,

    /// <summary>Private storage could not be reached. Nothing is known about the text either way.</summary>
    StorageUnavailable = 4,
}

/// <param name="Text">
/// Present exactly when the artifact's status is <see cref="BrandSourceExtractionStatus.Succeeded"/>. Untrusted
/// content however it was produced.
/// </param>
public sealed record BrandSourceExtractionRead(
    BrandSourceExtractionReadOutcome Outcome,
    BrandSourceExtractionArtifact? Artifact = null,
    string? Text = null);

/// <summary>What a creator's request to read a version again needs, and nothing it does not.</summary>
/// <param name="ActorUserId">From the authenticated principal, for the audit entry.</param>
/// <param name="ExpectedExtractionId">
/// The artifact the creator was looking at, or null for a version nothing had read. Checked again here, in the
/// write's own round trip, because the Business check that preceded it was a separate one.
/// </param>
public sealed record BrandSourceExtractionRetry(
    Guid DocumentId, Guid VersionId, int VersionNumber, Guid? ExpectedExtractionId, string ActorUserId);

public enum BrandSourceRetryOutcome
{
    /// <summary>The version's operation was put back in the queue, with its attempts spent fresh.</summary>
    Queued = 1,

    /// <summary>
    /// A read was already queued or running — before the request, or because a worker claimed the row between
    /// the check and the write. Nothing was changed, and the creator gets the answer they wanted either way.
    /// </summary>
    AlreadyReading = 2,

    /// <summary>
    /// The version's current artifact is not the one the creator was looking at — someone corrected it, or another
    /// read landed — so what there is to retry has changed. Nothing was queued.
    /// </summary>
    Conflict = 3,
}

/// <summary>
/// A creator's correction, with everything the write needs and nothing it does not.
/// </summary>
/// <param name="ExpectedExtractionId">
/// The artifact this was composed against. Re-checked inside the write, not only by the caller that read it: the
/// read and the write are separate round trips and a collaborator's correction can land between them.
/// </param>
/// <param name="MembershipId">
/// From the resolved context, never a request field. The schema requires a corrected row to name its author, so
/// this is not optional in any sense.
/// </param>
public sealed record BrandSourceExtractionCorrection(
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    Guid ExpectedExtractionId,
    string Text,
    string Reason,
    Guid MembershipId,
    string ActorUserId);

public enum BrandSourceCorrectionOutcome
{
    Corrected = 1,

    /// <summary>
    /// The version's current artifact is no longer the one the correction named. Nothing was written and
    /// anything this attempt stored was removed again.
    /// </summary>
    Conflict = 2,

    /// <summary>The corrected text could not be stored. Nothing was written.</summary>
    StorageUnavailable = 3,
}

/// <param name="Artifact">Present exactly when <paramref name="Outcome"/> is <see cref="BrandSourceCorrectionOutcome.Corrected"/>.</param>
public sealed record BrandSourceCorrectionResult(
    BrandSourceCorrectionOutcome Outcome, BrandSourceExtractionArtifact? Artifact = null);

/// <summary>
/// The persistence side of running one extraction: reading the source, and recording what came of it.
/// </summary>
/// <remarks>
/// Every write here quotes the worker's lease token. That is the invariant the whole queue rests on: a worker
/// whose claim was taken by a recovery sweep must not be able to record an outcome over the top of the worker
/// that now holds it, and a token checked on every write is what makes that detectable rather than
/// last-writer-wins.
/// </remarks>
public interface IBrandSourceExtractionDataLayer
{
    /// <summary>
    /// Opens the bytes of the version one claimed operation names, in the resolved workspace.
    /// </summary>
    /// <remarks>
    /// Two steps that belong together, as for a download: the row says where the bytes are, and only then are
    /// they opened. The lease is re-checked here rather than assumed from the claim, because the claim and this
    /// read are separate round trips and a recovery sweep can land between them.
    /// </remarks>
    Task<BrandSourceExtractionSource> OpenSourceAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// Records what a parser made of the document and completes the operation, as one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For text, the object is written before the row that names it — the same order as the upload's, and for
    /// the same reason: no committed row may point at bytes that are not there. If the save then fails for any
    /// reason, the object is removed again before the failure is reported.
    /// </para>
    /// <para>
    /// For a review state there is no object at all, which the schema insists on: an extraction row carries a
    /// key exactly when its status is <see cref="BrandSourceExtractionStatus.Succeeded"/>.
    /// </para>
    /// </remarks>
    Task<BrandSourceExtractionWriteOutcome> CompleteAsync(
        BrandSourceExtractionTarget target,
        Guid leaseToken,
        string extractorId,
        BrandSourceExtractedText extracted,
        CancellationToken cancellationToken);

    /// <summary>Ends the operation with no extraction row: something stopped the work.</summary>
    Task<BrandSourceExtractionWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceExtractionFailureCategory category,
        string summary,
        CancellationToken cancellationToken);

    /// <summary>Ends the operation because there is nothing left to extract. Not a failure.</summary>
    Task<BrandSourceExtractionWriteOutcome> CancelAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the operation to the queue after a transient fault, with backoff, or fails it when the attempt
    /// bound is spent.
    /// </summary>
    /// <remarks>
    /// Explicit rather than left to the lease lapsing. Both end in the same place, but a lapse costs
    /// <see cref="BrandPolicy.ExtractionLeaseDuration"/> of doing nothing for a fault the worker already knows
    /// about, and it would report the eventual give-up as <see cref="BrandSourceExtractionFailureCategory.LeaseAbandoned"/>
    /// — which names the wrong reason.
    /// </remarks>
    Task<BrandSourceExtractionRequeueResult> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceExtractionFailureCategory category,
        string summary,
        CancellationToken cancellationToken);

    /// <summary>
    /// One numbered version of one document in the resolved workspace, or null when there is no such pair.
    /// </summary>
    /// <remarks>
    /// Straight through to the repository. Here rather than reached from Business directly because Business may
    /// not call a repository, and the two refusal facts it returns — the document's status and its current version
    /// number — are decisions Business owns and this layer only fetches.
    /// </remarks>
    Task<BrandSourceVersionReview?> FindVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// A version's current extracted text, with the artifact that records it.
    /// </summary>
    /// <remarks>
    /// Two steps that belong together, as for a download: the row says where the text is, and only then is it
    /// read. A review state carries no pointer, so no object is opened for one — which is why an
    /// <see cref="BrandSourceExtractionStatus.Unsupported"/> artifact can be read even while storage is down.
    /// </remarks>
    Task<BrandSourceExtractionRead> ReadCurrentAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Whether a read of the version is queued or running right now.</summary>
    Task<bool> IsReadingAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a version's operation back in the queue so the worker reads it again, as one save with its audit entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The same row, not a second one.</strong> The queue is keyed on (workspace, version), so a retry
    /// resets the operation rather than adding another: attempts return to zero, the lease and failure are
    /// cleared, and it is due immediately. What the worker then writes is the version's next artifact, because it
    /// asks for the next ordinal — nothing already recorded is touched, and the earlier attempt's artifact stays
    /// readable as history.
    /// </para>
    /// <para>
    /// Whether this <em>should</em> happen is Business's to decide from the artifact. This only does it, and
    /// answers honestly when it did not need to.
    /// </para>
    /// </remarks>
    Task<BrandSourceRetryOutcome> RetryAsync(BrandSourceExtractionRetry retry, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a creator's corrected text as the version's next artifact, as one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object is written before the row that names it and removed again if the save fails — the same order,
    /// and the same compensation, as the upload's and the worker's. <strong>Nothing is overwritten:</strong> the
    /// ordinal is one past the current artifact's, so the key is new, the previous artifact's text stays exactly
    /// where it was, and the uploaded file is never named on this path at all.
    /// </para>
    /// <para>
    /// The expected artifact is re-checked here as well as by the caller that read it, because the read and this
    /// write are separate round trips. Both guards on the race are honoured: that check, and the unique index on
    /// (workspace, version, ordinal) if a collaborator somehow lands between the check and the save.
    /// </para>
    /// </remarks>
    Task<BrandSourceCorrectionResult> CorrectAsync(
        BrandSourceExtractionCorrection correction, CancellationToken cancellationToken);
}

internal sealed class BrandSourceExtractionDataLayer(
    IBrandSourceExtractionRepository extractions,
    IBrandSourceObjectGateway objects,
    IAuditWriter auditWriter,
    IWorkspaceContext workspace,
    CreatorPantryDbContext context,
    IClock clock,
    ILogger<BrandSourceExtractionDataLayer> logger) : IBrandSourceExtractionDataLayer
{
    public async Task<BrandSourceExtractionSource> OpenSourceAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        if (await extractions.FindTargetAsync(operationId, cancellationToken) is not { } record)
        {
            return new BrandSourceExtractionSource(BrandSourceExtractionSourceOutcome.Missing);
        }

        if (record.OperationStatus is not BrandSourceExtractionOperationStatus.Running || record.LeasedBy != leaseToken)
        {
            return new BrandSourceExtractionSource(BrandSourceExtractionSourceOutcome.LeaseLost);
        }

        if (record.DocumentStatus is BrandSourceDocumentStatus.Removed)
        {
            return new BrandSourceExtractionSource(BrandSourceExtractionSourceOutcome.DocumentRemoved);
        }

        BrandSourceObjectContent? stored;

        try
        {
            stored = await objects.OpenReadAsync(record.ObjectKey, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "Private storage could not be reached reading the source of extraction {OperationId}.",
                operationId);

            return new BrandSourceExtractionSource(BrandSourceExtractionSourceOutcome.StorageUnavailable);
        }

        if (stored is null)
        {
            logger.LogError(
                "Extraction {OperationId} names a version with no stored object.",
                operationId);

            return new BrandSourceExtractionSource(BrandSourceExtractionSourceOutcome.ObjectMissing);
        }

        await using (stored)
        {
            var buffer = new MemoryStream(capacity: (int)Math.Min(record.SizeBytes, BrandPolicy.SourceUploadMaxBytes));

            try
            {
                await stored.Content.CopyToAsync(buffer, cancellationToken);
            }
            catch (ObjectStoreUnavailableException exception)
            {
                // A store can fail partway through a read as easily as at the start of one, and a half-read
                // file parsed as a whole one would be recorded as a document that is corrupt rather than as a
                // fault to retry.
                await buffer.DisposeAsync();

                logger.LogWarning(
                    exception,
                    "Private storage became unreachable partway through the source of extraction {OperationId}.",
                    operationId);

                return new BrandSourceExtractionSource(BrandSourceExtractionSourceOutcome.StorageUnavailable);
            }

            buffer.Position = 0;

            return new BrandSourceExtractionSource(
                BrandSourceExtractionSourceOutcome.Opened,
                new BrandSourceExtractionTarget(
                    record.OperationId,
                    record.DocumentId,
                    record.VersionId,
                    record.VersionNumber,
                    record.MediaType,
                    record.SizeBytes),
                buffer);
        }
    }

    public async Task<BrandSourceExtractionWriteOutcome> CompleteAsync(
        BrandSourceExtractionTarget target,
        Guid leaseToken,
        string extractorId,
        BrandSourceExtractedText extracted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(extracted);

        // A creator's correction is the version's text, and a machine read that was queued before it landed must
        // not be appended on top of it: the newest artifact is the current one, so it would quietly hide the
        // person's words behind the parser's. Whatever order a retry and a correction arrived in, this is where it
        // is decided — nothing is written, and the caller ends the operation as having nothing left to do.
        if (await extractions.FindCurrentArtifactAsync(target.VersionId, cancellationToken) is { Origin: BrandSourceExtractionOrigin.Corrected })
        {
            return BrandSourceExtractionWriteOutcome.AlreadyExtracted;
        }

        var ordinal = await extractions.NextOrdinalAsync(target.VersionId, cancellationToken);
        var stored = (BrandSourceObject?)null;

        if (extracted.Outcome is BrandSourceTextExtractionOutcome.Extracted)
        {
            var write = await StoreTextAsync(
                target.DocumentId, target.VersionId, ordinal, extracted.Text!, cancellationToken);

            if (write is null)
            {
                return BrandSourceExtractionWriteOutcome.StorageUnavailable;
            }

            stored = write;
        }

        var operation = await extractions.FindForUpdateAsync(target.OperationId, cancellationToken);

        if (operation is null || operation.Status is not BrandSourceExtractionOperationStatus.Running
            || operation.LeasedBy != leaseToken)
        {
            await RemoveTextAsync(target.DocumentId, target.VersionId, ordinal, stored);

            return BrandSourceExtractionWriteOutcome.LeaseLost;
        }

        var now = clock.UtcNow;

        var extraction = new BrandSourceExtraction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            BrandSourceDocumentVersionId = target.VersionId,
            Ordinal = ordinal,
            Status = Status(extracted.Outcome),
            Origin = BrandSourceExtractionOrigin.Extracted,
            ExtractedTextObjectKey = stored?.ObjectKey,
            ContentChecksum = stored?.ContentChecksum,
            Reason = Reason(extracted.Reason),

            // Null: a parser's attempt belongs to no member. A correction names its author (11A.11), and the
            // schema's own constraint insists on that for exactly those rows.
            CreatedByMembershipId = null,
            CreatedAt = now,
        };

        extractions.Add(extraction);

        // Staged in the same save as the text it will embed, so an artifact with nothing queued to embed it is
        // not a state that can exist — and a worker that dies after committing cannot lose the request.
        if (extraction.Status is BrandSourceExtractionStatus.Succeeded)
        {
            extractions.EnqueueEmbedding(
                BrandSourceEmbeddingQueue.For(workspace.WorkspaceId, target.DocumentId, extraction, now));
        }

        operation.Status = BrandSourceExtractionOperationStatus.Completed;
        operation.BrandSourceExtractionId = extraction.Id;
        operation.ExtractorId = extractorId;
        operation.StatusChangedAt = now;
        operation.CompletedAt = now;
        operation.LeasedBy = null;
        operation.LeaseExpiresAt = null;

        auditWriter.Record(new AuditEntry(

            // No actor: a worker running as the workspace's service identity is not a person, and naming the
            // creator who uploaded the file would attribute a machine's action to them.
            ActorUserId: null,
            BrandAuditActions.SourceDocumentExtracted,
            BrandAuditActions.SourceDocumentResourceType,
            target.DocumentId.ToString(),
            CorrelationId: target.OperationId,
            Summary: $"Version {target.VersionNumber} read by {extractorId}: {extracted.Outcome}."));

        try
        {
            // One save: the extraction row, the operation's completion and the audit entry commit together or
            // not at all. The operation's UPDATE carries its row version, which is what decides a race with a
            // recovery sweep that took the lease between the check above and this write.
            await context.SaveChangesAsync(cancellationToken);

            return BrandSourceExtractionWriteOutcome.Applied;
        }
        catch (DbUpdateException exception)
        {
            context.ChangeTracker.Clear();
            await RemoveTextAsync(target.DocumentId, target.VersionId, ordinal, stored);

            if (exception is DbUpdateConcurrencyException)
            {
                return BrandSourceExtractionWriteOutcome.LeaseLost;
            }

            // The other guard on the same race: the unique index on (workspace, version, ordinal). Ask the
            // question that distinguishes a lost race from a fault — is that ordinal now taken?
            if (await extractions.NextOrdinalAsync(target.VersionId, CancellationToken.None) > ordinal)
            {
                return BrandSourceExtractionWriteOutcome.AlreadyExtracted;
            }

            throw;
        }
        catch
        {
            context.ChangeTracker.Clear();
            await RemoveTextAsync(target.DocumentId, target.VersionId, ordinal, stored);
            throw;
        }
    }

    public Task<BrandSourceExtractionWriteOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceExtractionFailureCategory category,
        string summary,
        CancellationToken cancellationToken) =>
        SettleAsync(
            operationId,
            leaseToken,
            (operation, now) =>
            {
                operation.Status = BrandSourceExtractionOperationStatus.Failed;
                operation.FailureCategory = category;
                operation.FailureSummary = Summary(summary);
                operation.CompletedAt = now;
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;
            },
            cancellationToken);

    public Task<BrandSourceExtractionWriteOutcome> CancelAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken) =>
        SettleAsync(
            operationId,
            leaseToken,
            (operation, now) =>
            {
                operation.Status = BrandSourceExtractionOperationStatus.Cancelled;
                operation.CompletedAt = now;
                operation.LeasedBy = null;
                operation.LeaseExpiresAt = null;
            },
            cancellationToken);

    public async Task<BrandSourceExtractionRequeueResult> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        BrandSourceExtractionFailureCategory category,
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

                if (operation.Attempts >= BrandPolicy.ExtractionMaxAttempts)
                {
                    operation.Status = BrandSourceExtractionOperationStatus.Failed;
                    operation.FailureCategory = category;
                    operation.FailureSummary = Summary(summary);
                    operation.CompletedAt = now;
                    abandoned = true;
                }
                else
                {
                    operation.Status = BrandSourceExtractionOperationStatus.Queued;
                    operation.AvailableAt = now + BrandPolicy.ExtractionBackoffFor(operation.Attempts);
                }
            },
            cancellationToken);

        return new BrandSourceExtractionRequeueResult(outcome, abandoned);
    }

    /// <summary>
    /// Applies one state change to a claimed operation, under its lease, as one save.
    /// </summary>
    /// <remarks>
    /// Shared by the three endings that write no extraction row, so they cannot drift on the thing they must
    /// agree about: that a worker which no longer holds the lease changes nothing.
    /// </remarks>
    private async Task<BrandSourceExtractionWriteOutcome> SettleAsync(
        Guid operationId,
        Guid leaseToken,
        Action<BrandSourceExtractionOperation, DateTimeOffset> apply,
        CancellationToken cancellationToken)
    {
        var operation = await extractions.FindForUpdateAsync(operationId, cancellationToken);

        if (operation is null || operation.Status is not BrandSourceExtractionOperationStatus.Running
            || operation.LeasedBy != leaseToken)
        {
            return BrandSourceExtractionWriteOutcome.LeaseLost;
        }

        var now = clock.UtcNow;
        operation.StatusChangedAt = now;
        apply(operation, now);

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return BrandSourceExtractionWriteOutcome.Applied;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A recovery sweep took the lease between the check and the write. Nothing may stay staged for the
            // next save on this scope to commit.
            context.ChangeTracker.Clear();

            return BrandSourceExtractionWriteOutcome.LeaseLost;
        }
    }

    /// <summary>
    /// Stores one extraction's text, or null when storage could not be reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An object already at this key is this operation's own litter, and is replaced.</strong> The key
    /// names a workspace, a document, a version and an ordinal; the ordinal is one past the highest any
    /// committed row holds, and only this operation reads that version — so nothing committed can be pointing
    /// at it, and the only way something is there is an earlier attempt of this operation that stored bytes and
    /// then failed to commit. Removing it is what makes a retry deterministic instead of leaving the artifact
    /// from the attempt that failed.
    /// </para>
    /// <para>
    /// Writes are create-only, which is why this is a delete and a second put rather than an overwrite: the
    /// gateway offers no overwrite, deliberately, so that no ordinary path can replace a stored artifact.
    /// </para>
    /// </remarks>
    public Task<BrandSourceVersionReview?> FindVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken) =>
        extractions.FindVersionForReviewAsync(documentId, versionNumber, cancellationToken);

    public async Task<BrandSourceExtractionRead> ReadCurrentAsync(
        Guid versionId, CancellationToken cancellationToken)
    {
        if (await extractions.FindCurrentArtifactAsync(versionId, cancellationToken) is not { } record)
        {
            return new BrandSourceExtractionRead(BrandSourceExtractionReadOutcome.NotExtracted);
        }

        var artifact = Artifact(record);

        if (record.ExtractedTextObjectKey is null)
        {
            // A review state: no pointer, by the schema's own rule, so there is no object to open. Readable
            // even while storage is down, which is the state a creator most needs to see.
            return new BrandSourceExtractionRead(BrandSourceExtractionReadOutcome.Read, artifact);
        }

        BrandSourceObjectContent? stored;

        try
        {
            stored = await objects.OpenReadAsync(record.ExtractedTextObjectKey, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "Private storage could not be reached reading extraction {ExtractionId}'s text.",
                record.Id);

            return new BrandSourceExtractionRead(BrandSourceExtractionReadOutcome.StorageUnavailable, artifact);
        }

        if (stored is null)
        {
            // A committed row naming text that is not there. Both writers store the object before the row and
            // remove it again if the save fails, so reaching here means something outside the application
            // removed it. A fault to find, not a document to report as unextracted.
            logger.LogError("Extraction {ExtractionId} has no stored text.", record.Id);

            return new BrandSourceExtractionRead(BrandSourceExtractionReadOutcome.ObjectMissing, artifact);
        }

        await using (stored)
        {
            using var reader = new StreamReader(stored.Content, System.Text.Encoding.UTF8);

            return new BrandSourceExtractionRead(
                BrandSourceExtractionReadOutcome.Read, artifact, await reader.ReadToEndAsync(cancellationToken));
        }
    }

    public async Task<BrandSourceCorrectionResult> CorrectAsync(
        BrandSourceExtractionCorrection correction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(correction);

        var current = await extractions.FindCurrentArtifactAsync(correction.VersionId, cancellationToken);

        if (current is null || current.Id != correction.ExpectedExtractionId)
        {
            // Re-checked here as well as by the caller that read it, because the read and this write are
            // separate round trips and a collaborator's correction can land between them.
            return new BrandSourceCorrectionResult(BrandSourceCorrectionOutcome.Conflict);
        }

        // One past the artifact this was composed against, so the key is new and the previous text stays
        // exactly where it is. Nothing on this path names the uploaded file at all.
        var ordinal = current.Ordinal + 1;

        var stored = await StoreTextAsync(
            correction.DocumentId, correction.VersionId, ordinal, correction.Text, cancellationToken);

        if (stored is null)
        {
            return new BrandSourceCorrectionResult(BrandSourceCorrectionOutcome.StorageUnavailable);
        }

        var now = clock.UtcNow;

        var extraction = new BrandSourceExtraction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            BrandSourceDocumentVersionId = correction.VersionId,
            Ordinal = ordinal,
            Status = BrandSourceExtractionStatus.Succeeded,
            Origin = BrandSourceExtractionOrigin.Corrected,
            ExtractedTextObjectKey = stored.ObjectKey,
            ContentChecksum = stored.ContentChecksum,
            Reason = Reason(correction.Reason),

            // Required for a corrected row by the schema's own constraint, and taken from the resolved context
            // rather than from anything the request carried.
            CreatedByMembershipId = correction.MembershipId,
            CreatedAt = now,
        };

        extractions.Add(extraction);

        // A correction is always text, so it always has something to embed — and the artifact it replaces is
        // retired only after this one's set is current, never here.
        extractions.EnqueueEmbedding(
            BrandSourceEmbeddingQueue.For(workspace.WorkspaceId, correction.DocumentId, extraction, now));

        auditWriter.Record(new AuditEntry(

            // Named, unlike the worker's entry: this is a person's edit to creator source material.
            correction.ActorUserId,
            BrandAuditActions.SourceDocumentTextCorrected,
            BrandAuditActions.SourceDocumentResourceType,
            correction.DocumentId.ToString(),
            CorrelationId: extraction.Id,

            // The creator's stated reason is on the row, not here: an audit summary is not where free text
            // about a private document belongs.
            Summary: $"Version {correction.VersionNumber}'s text corrected at ordinal {ordinal}."));

        try
        {
            // One save: the extraction row and the audit entry commit together or not at all.
            await context.SaveChangesAsync(cancellationToken);

            return new BrandSourceCorrectionResult(
                BrandSourceCorrectionOutcome.Corrected,
                new BrandSourceExtractionArtifact(
                    extraction.Id,
                    ordinal,
                    extraction.Status,
                    extraction.Origin,
                    extraction.ContentChecksum,
                    extraction.Reason,
                    now));
        }
        catch (DbUpdateException)
        {
            // Nothing committed, so the object this attempt wrote must not outlive it, and nothing may stay
            // staged for the next save on this scope to commit.
            context.ChangeTracker.Clear();
            await RemoveTextAsync(correction.DocumentId, correction.VersionId, ordinal, stored);

            // The second guard on the same race: the unique index on (workspace, version, ordinal). Ask the
            // question that distinguishes a lost race from a fault — is that ordinal now taken?
            if (await extractions.NextOrdinalAsync(correction.VersionId, CancellationToken.None) > ordinal)
            {
                return new BrandSourceCorrectionResult(BrandSourceCorrectionOutcome.Conflict);
            }

            throw;
        }
        catch
        {
            context.ChangeTracker.Clear();
            await RemoveTextAsync(correction.DocumentId, correction.VersionId, ordinal, stored);
            throw;
        }
    }

    public Task<bool> IsReadingAsync(Guid versionId, CancellationToken cancellationToken) =>
        extractions.IsReadingAsync(versionId, cancellationToken);

    public async Task<BrandSourceRetryOutcome> RetryAsync(
        BrandSourceExtractionRetry retry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retry);

        var now = clock.UtcNow;

        // Re-read here rather than trusting the Business check that came first: a correction can land between the
        // two round trips, and a read queued behind it would be the newest artifact once the worker ran.
        var current = await extractions.FindCurrentArtifactAsync(retry.VersionId, cancellationToken);
        if (current?.Id != retry.ExpectedExtractionId || current is { Origin: BrandSourceExtractionOrigin.Corrected })
        {
            return BrandSourceRetryOutcome.Conflict;
        }

        var operation = await extractions.FindForVersionUpdateAsync(retry.VersionId, cancellationToken);
        var repaired = operation is null;

        if (operation is null)
        {
            // A committed version always has one, so this is a repair rather than a path: queue what should have
            // been queued, the way the upload would have.
            extractions.Enqueue(BrandSourceExtractionQueue.For(
                workspace.WorkspaceId, retry.DocumentId, retry.VersionId, now));
        }
        else if (operation.Status is BrandSourceExtractionOperationStatus.Queued or BrandSourceExtractionOperationStatus.Running)
        {
            return BrandSourceRetryOutcome.AlreadyReading;
        }
        else
        {
            // Every column the schema ties to a terminal state is cleared together, or a check refuses the row:
            // the failure, the completion time, the pointer to the previous run's extraction and the lease.
            // StartedAt stays: it records that work began once, not that it is running.
            operation.Status = BrandSourceExtractionOperationStatus.Queued;
            operation.Attempts = 0;
            operation.AvailableAt = now;
            operation.QueuedAt = now;
            operation.StatusChangedAt = now;
            operation.LeasedBy = null;
            operation.LeaseExpiresAt = null;
            operation.FailureCategory = null;
            operation.FailureSummary = null;
            operation.CompletedAt = null;
            operation.BrandSourceExtractionId = null;
        }

        auditWriter.Record(new AuditEntry(
            retry.ActorUserId,
            BrandAuditActions.SourceExtractionRetried,
            BrandAuditActions.SourceDocumentResourceType,
            retry.DocumentId.ToString(),
            CorrelationId: retry.VersionId,
            Summary: $"Version {retry.VersionNumber}'s text asked to be read again."));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return BrandSourceRetryOutcome.Queued;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A worker claimed the row between the read and the write, which is the outcome the creator wanted.
            context.ChangeTracker.Clear();
            return BrandSourceRetryOutcome.AlreadyReading;
        }
        catch (DbUpdateException) when (repaired)
        {
            // Two retries both found no row and both tried to add one; the unique index let the first through. The
            // other's read is queued, which is what this one was asking for.
            context.ChangeTracker.Clear();
            return BrandSourceRetryOutcome.AlreadyReading;
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }

    private static BrandSourceExtractionArtifact Artifact(BrandSourceExtractionArtifactRecord record) =>
        new(
            record.Id,
            record.Ordinal,
            record.Status,
            record.Origin,
            record.ContentChecksum,
            record.Reason,
            record.CreatedAt);

    private async Task<BrandSourceObject?> StoreTextAsync(
        Guid documentId, Guid versionId, int ordinal, string text, CancellationToken cancellationToken)
    {
        var write = await PutAsync(documentId, versionId, ordinal, text, cancellationToken);

        if (write.Outcome is BrandSourceObjectWriteOutcome.AlreadyExists)
        {
            await objects.DeleteAsync(
                BrandSourceObjectKey.ForExtractedText(workspace.WorkspaceId, documentId, versionId, ordinal),
                cancellationToken);

            write = await PutAsync(documentId, versionId, ordinal, text, cancellationToken);
        }

        if (write.Outcome is BrandSourceObjectWriteOutcome.Stored)
        {
            return write.Object;
        }

        logger.LogWarning(
            "Extracted text could not be stored: {Outcome}. "
                + "workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId} ordinal={Ordinal}",
            write.Outcome, workspace.WorkspaceId, documentId, versionId, ordinal);

        return null;
    }

    private async Task<BrandSourceObjectWrite> PutAsync(
        Guid documentId, Guid versionId, int ordinal, string text, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text), writable: false);

        // The cap the builder already honoured, restated as the store's own limit: a text longer than this
        // cannot be stored even if a parser one day forgets to stop, or a correction's validator does.
        return await objects.PutExtractedTextAsync(
            documentId, versionId, ordinal, content, BrandPolicy.ExtractedTextMaxBytes, cancellationToken);
    }

    /// <summary>Compensation, so it is not cancelled with the caller and does not replace the failure it follows.</summary>
    private async Task RemoveTextAsync(Guid documentId, Guid versionId, int ordinal, BrandSourceObject? stored)
    {
        if (stored is null)
        {
            return;
        }

        try
        {
            await objects.DeleteAsync(
                BrandSourceObjectKey.ForExtractedText(workspace.WorkspaceId, documentId, versionId, ordinal),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Extracted text could not be removed after a failed write; it is unreferenced. "
                    + "workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId} ordinal={Ordinal}",
                workspace.WorkspaceId, documentId, versionId, ordinal);
        }
    }

    private static BrandSourceExtractionStatus Status(BrandSourceTextExtractionOutcome outcome) => outcome switch
    {
        BrandSourceTextExtractionOutcome.Extracted => BrandSourceExtractionStatus.Succeeded,
        BrandSourceTextExtractionOutcome.Unsupported => BrandSourceExtractionStatus.Unsupported,
        _ => BrandSourceExtractionStatus.Failed,
    };

    private static string? Reason(string? reason) => Truncate(reason, BrandPolicy.ReasonMaxLength);

    private static string? Summary(string? summary) =>
        Truncate(summary, BrandPolicy.ExtractionFailureSummaryMaxLength);

    /// <summary>
    /// Defensive. Every reason and summary this module writes is a constant well inside its column, and a
    /// length that reached the database as a truncation error would fail a write that had already done its work.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
