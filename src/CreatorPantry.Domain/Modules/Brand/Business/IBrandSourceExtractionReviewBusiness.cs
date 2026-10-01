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

        return read.Outcome switch
        {
            BrandSourceExtractionReadOutcome.Read => OperationResult<BrandSourceExtractionServiceModel>.Success(
                ToServiceModel(versionNumber, read.Artifact!, read.Text)),

            // A 200 rather than a 404: the version exists and downloads, and a creator watching a queued
            // extraction needs to see "not yet" rather than "no such thing".
            BrandSourceExtractionReadOutcome.NotExtracted => OperationResult<BrandSourceExtractionServiceModel>.Success(
                BrandSourceExtractionServiceModel.NotExtracted(versionNumber)),

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
    private static bool IsReadable(BrandSourceVersionReview? version) =>
        version is not null and not { DocumentStatus: BrandSourceDocumentStatus.Removed };

    private static BrandSourceExtractionServiceModel ToServiceModel(
        int versionNumber, BrandSourceExtractionArtifact artifact, string? text) =>
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
            artifact.CreatedAt);

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
