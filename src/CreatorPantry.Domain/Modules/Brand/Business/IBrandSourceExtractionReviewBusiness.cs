using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

/// <summary>
/// The rules of reading and correcting one version's extracted text.
/// </summary>
/// <remarks>
/// Every refusal here is a domain decision rather than an HTTP one: which document states may be read, which may
/// be written, which version may be corrected, and what makes a correction stale. Nothing here takes a workspace
/// id — the ambient <c>IWorkspaceContext</c> is resolved before this runs and every read below is filtered by it.
/// </remarks>
public interface IBrandSourceExtractionReviewBusiness
{
    /// <inheritdoc cref="Facade.IBrandSourceExtractionReviewFacade.GetAsync"/>
    Task<OperationResult<BrandSourceExtractionServiceModel>> GetAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IBrandSourceExtractionReviewFacade.CorrectAsync"/>
    /// <summary>
    /// Asks for a version's text to be read again, when reading again could change the answer.
    /// </summary>
    /// <remarks>
    /// Allowed when the last attempt <em>failed</em> (the file could not be read) or stopped before it wrote anything
    /// (storage was unreachable, every attempt was spent). Refused, with the reason in the message, when the version
    /// was already read, holds nothing to read (a scan or an image — the same bytes give the same answer), or has a
    /// creator's correction that a new read would sit behind. A read already queued or running is not an error:
    /// asking twice gets the same answer as asking once.
    /// </remarks>
    Task<OperationResult<BrandSourceExtractionServiceModel>> RetryAsync(
        string actorUserId,
        Guid documentId,
        int versionNumber,
        Guid? expectedExtractionId,
        CancellationToken cancellationToken);

    Task<OperationResult<BrandSourceExtractionServiceModel>> CorrectAsync(
        string actorUserId,
        Guid documentId,
        int versionNumber,
        Guid expectedExtractionId,
        string text,
        string reason,
        CancellationToken cancellationToken);
}

internal sealed class BrandSourceExtractionReviewBusiness(
    IBrandSourceExtractionDataLayer dataLayer, IWorkspaceContext workspace) : IBrandSourceExtractionReviewBusiness
{
    public async Task<OperationResult<BrandSourceExtractionServiceModel>> GetAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken)
    {
        var version = await dataLayer.FindVersionAsync(documentId, versionNumber, cancellationToken);

        if (!IsReadable(version))
        {
            return NotFound();
        }

        var read = await dataLayer.ReadCurrentAsync(version!.VersionId, cancellationToken);
        var reading = await dataLayer.IsReadingAsync(version.VersionId, cancellationToken);

        return read.Outcome switch
        {
            BrandSourceExtractionReadOutcome.Read => OperationResult<BrandSourceExtractionServiceModel>.Success(
                ToServiceModel(versionNumber, read.Artifact!, read.Text, reading)),

            // A 200 rather than a 404: the version exists and downloads, and a creator watching a queued
            // extraction needs to see "not yet" rather than "no such thing".
            BrandSourceExtractionReadOutcome.NotExtracted => OperationResult<BrandSourceExtractionServiceModel>.Success(
                BrandSourceExtractionServiceModel.NotExtracted(versionNumber) with { IsReading = reading }),

            // Both remaining outcomes are faults rather than missing documents: the row is there and says there
            // is text. Answered as the download answers the same situation, so retrying is the stated remedy.
            _ => OperationResult<BrandSourceExtractionServiceModel>.Failure(StorageUnavailable(
                "That document's text cannot be read right now. Try again shortly.")),
        };
    }

    public async Task<OperationResult<BrandSourceExtractionServiceModel>> CorrectAsync(
        string actorUserId,
        Guid documentId,
        int versionNumber,
        Guid expectedExtractionId,
        string text,
        string reason,
        CancellationToken cancellationToken)
    {
        var version = await dataLayer.FindVersionAsync(documentId, versionNumber, cancellationToken);

        if (!IsReadable(version))
        {
            return NotFound();
        }

        if (version!.DocumentStatus is BrandSourceDocumentStatus.Archived)
        {
            // The same line replacement draws, and for the same reason: an archive is a shelf, so the document
            // still reads and still downloads, but it is not where work is being done.
            return OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SourceArchivedConflict,
                "This document is archived. Restore it before correcting its text.",
                new Dictionary<string, string[]>()));
        }

        if (versionNumber != version.CurrentVersionNumber)
        {
            return OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SourceExtractionSupersededConflict,
                "Only the current version's text can be corrected. This version has been replaced.",
                new Dictionary<string, string[]>()));
        }

        var read = await dataLayer.ReadCurrentAsync(version.VersionId, cancellationToken);

        switch (read.Outcome)
        {
            case BrandSourceExtractionReadOutcome.NotExtracted:
                return OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
                    BrandErrorCodes.SourceExtractionPendingConflict,
                    "This document's text has not been read yet. Wait for that to finish, then correct it.",
                    new Dictionary<string, string[]>()));

            case BrandSourceExtractionReadOutcome.StorageUnavailable:
            case BrandSourceExtractionReadOutcome.ObjectMissing:
                // The current text has to be read before it can be corrected: the expected artifact is checked
                // against it, and an identical submission is answered without writing. Correcting blind would
                // mean neither check could be made.
                return OperationResult<BrandSourceExtractionServiceModel>.Failure(StorageUnavailable(
                    "That document's text cannot be read right now, so it cannot be corrected. Try again shortly."));
        }

        var current = read.Artifact!;

        if (current.Id != expectedExtractionId)
        {
            return Conflict();
        }

        if (string.Equals(read.Text, text, StringComparison.Ordinal))
        {
            // Byte-identical to what is already stored. Answered with the current artifact and nothing written:
            // an ordinal that changes nothing is provenance a later reader has to explain for no gain.
            return OperationResult<BrandSourceExtractionServiceModel>.Success(
                ToServiceModel(versionNumber, current, read.Text));
        }

        var result = await dataLayer.CorrectAsync(
            new BrandSourceExtractionCorrection(
                version.DocumentId,
                version.VersionId,
                versionNumber,
                expectedExtractionId,
                text,
                reason,

                // From the resolved context, never the request. The schema requires a corrected row to name its
                // author, so there is no path on which this is absent.
                workspace.MembershipId,
                actorUserId),
            cancellationToken);

        return result.Outcome switch
        {
            BrandSourceCorrectionOutcome.Corrected => OperationResult<BrandSourceExtractionServiceModel>.Success(
                ToServiceModel(versionNumber, result.Artifact!, text)),

            BrandSourceCorrectionOutcome.Conflict => Conflict(),

            _ => OperationResult<BrandSourceExtractionServiceModel>.Failure(StorageUnavailable(
                "That correction could not be stored, so nothing was changed. Try again shortly.")),
        };
    }

    /// <summary>
    /// Whether a version may be read at all: it exists, and its document is not a tombstone.
    /// </summary>
    /// <remarks>
    /// Archived reads normally — a shelf, not a deletion. Removed does not, and is answered identically to an
    /// unknown id so that a caller cannot learn a document exists somewhere they cannot see it.
    /// </remarks>
    public async Task<OperationResult<BrandSourceExtractionServiceModel>> RetryAsync(
        string actorUserId,
        Guid documentId,
        int versionNumber,
        Guid? expectedExtractionId,
        CancellationToken cancellationToken)
    {
        var version = await dataLayer.FindVersionAsync(documentId, versionNumber, cancellationToken);

        if (!IsReadable(version))
        {
            return NotFound();
        }

        if (version!.DocumentStatus is BrandSourceDocumentStatus.Archived)
        {
            return OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SourceArchivedConflict,
                "This document is archived. Restore it before reading its text again.",
                new Dictionary<string, string[]>()));
        }

        if (versionNumber != version.CurrentVersionNumber)
        {
            return OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SourceExtractionSupersededConflict,
                "Only the current version's text can be read again. This version has been replaced.",
                new Dictionary<string, string[]>()));
        }

        // The artifact is all the decision needs, so a storage fault reading its text is not a reason to refuse:
        // the row that says what happened is readable while the bytes it names are not.
        var read = await dataLayer.ReadCurrentAsync(version.VersionId, cancellationToken);
        var current = read.Artifact;

        if (current?.Id != expectedExtractionId)
        {
            return Conflict();
        }

        if (current is not null)
        {
            var notRetryable = NotRetryableReason(current);

            if (notRetryable is not null && !await dataLayer.IsReadingAsync(version.VersionId, cancellationToken))
            {
                return OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
                    BrandErrorCodes.SourceExtractionNotRetryableConflict,
                    notRetryable,
                    new Dictionary<string, string[]>()));
            }
        }

        var retried = await dataLayer.RetryAsync(
            new BrandSourceExtractionRetry(version.DocumentId, version.VersionId, versionNumber, expectedExtractionId, actorUserId),
            cancellationToken);

        if (retried is BrandSourceRetryOutcome.Conflict)
        {
            return Conflict();
        }

        // The same answer whether this call queued the read or found one already queued: it is reading.
        return OperationResult<BrandSourceExtractionServiceModel>.Success(
            current is null
                ? BrandSourceExtractionServiceModel.NotExtracted(versionNumber) with { IsReading = true }
                : ToServiceModel(versionNumber, current, null, isReading: true));
    }

    /// <summary>Null when reading again could change the answer; otherwise why it could not, in the creator's terms.</summary>
    private static string? NotRetryableReason(BrandSourceExtractionArtifact artifact)
    {
        if (artifact.Origin is BrandSourceExtractionOrigin.Corrected)
        {
            return "You have corrected this text, so it will not be read again. Edit it instead.";
        }

        return artifact.Status switch
        {
            BrandSourceExtractionStatus.Failed => null,
            BrandSourceExtractionStatus.Unsupported =>
                "Reading this again would give the same answer, because there is no text in it to read. Type the text in instead.",
            _ => "This document's text has already been read. To change it, edit the text instead.",
        };
    }

    private static bool IsReadable(BrandSourceVersionReview? version) =>
        version is not null and not { DocumentStatus: BrandSourceDocumentStatus.Removed };

    private static BrandSourceExtractionServiceModel ToServiceModel(
        int versionNumber, BrandSourceExtractionArtifact artifact, string? text, bool isReading = false) =>
        new(
            versionNumber,
            artifact.Status switch
            {
                BrandSourceExtractionStatus.Succeeded => BrandSourceExtractionState.Succeeded,
                BrandSourceExtractionStatus.Unsupported => BrandSourceExtractionState.Unsupported,
                _ => BrandSourceExtractionState.Failed,
            },
            artifact.Id,
            artifact.Ordinal,
            artifact.Origin,

            // Present exactly when the artifact succeeded, which is the rule the row is under too. A review
            // state carries its reason instead, and never an empty string that a client has to interpret.
            artifact.Status is BrandSourceExtractionStatus.Succeeded ? text : null,
            artifact.Reason,
            artifact.CreatedAt,
            isReading);

    private static OperationResult<BrandSourceExtractionServiceModel> NotFound() =>
        OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
            BrandErrorCodes.SourceNotFound,
            "That document version could not be found in this workspace.",
            new Dictionary<string, string[]>()));

    private static OperationResult<BrandSourceExtractionServiceModel> Conflict() =>
        OperationResult<BrandSourceExtractionServiceModel>.Failure(new OperationError(
            BrandErrorCodes.SourceExtractionConflict,
            "This document's text has changed since you read it. Read it again before correcting it.",
            new Dictionary<string, string[]>()));

    private static OperationError StorageUnavailable(string message) =>
        new(BrandErrorCodes.SourceStorageUnavailable, message, new Dictionary<string, string[]>());
}
