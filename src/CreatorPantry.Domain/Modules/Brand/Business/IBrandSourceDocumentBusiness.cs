using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

public interface IBrandSourceDocumentBusiness
{
    /// <summary>
    /// Decides whether a file may be accepted: its size, what its bytes are, whether it holds together as that
    /// format, and the malware verdict. Writes nothing, so a refusal leaves nothing behind.
    /// </summary>
    Task<OperationResult<BrandSourcePreparedUpload>> PrepareAsync(
        UploadBrandSourceDocumentViewModel model, BrandSourceUploadFile file, CancellationToken cancellationToken);

    /// <summary>Stores a prepared upload as a new document at version 1.</summary>
    Task<OperationResult<BrandSourceDocumentServiceModel>> StoreAsync(
        string actorUserId, UploadBrandSourceDocumentViewModel model, BrandSourcePreparedUpload upload, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentDataLayer.AbandonAsync"/>
    Task AbandonAsync(Guid documentId, Guid versionId);
    /// <summary>
    /// Decides whether this document may take a new version and whether the file offered may be accepted.
    /// Writes nothing, so a refusal leaves nothing behind — not a row and not an object.
    /// </summary>
    /// <returns>
    /// The prepared replacement, or <c>brand.source.not_found</c>, <c>brand.source.archived.conflict</c>,
    /// <c>brand.source.conflict</c>, or any of the file refusals an upload can give.
    /// </returns>
    /// <remarks>
    /// The document is checked <strong>before</strong> the file is read, hashed or scanned, so a caller
    /// holding a stale token or naming a document they cannot see pays nothing for 20 MB.
    /// </remarks>
    Task<OperationResult<BrandSourcePreparedReplacement>> PrepareReplacementAsync(
        Guid documentId, string? expectedConcurrencyToken, BrandSourceUploadFile file, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a prepared replacement as the document's next version, keeping every earlier one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-reads the document tracked and re-decides the three refusals on it: this runs inside the idempotent
    /// unit, which a retrying execution strategy may run more than once, and the row it writes against has to
    /// be the row it just read.
    /// </para>
    /// <para>
    /// <strong>Nothing earlier is touched.</strong> The previous version's row is immutable and is not
    /// loaded; its object is not named, overwritten or deleted; and any approved style-guide version citing
    /// it stays pinned to it, because the pin is to a version and not to a document. Marking such a guide
    /// stale is for explicit staleness rules, not for this write.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandSourceDocumentServiceModel>> StoreReplacementAsync(
        string actorUserId, string? expectedConcurrencyToken, BrandSourcePreparedReplacement replacement, CancellationToken cancellationToken);

    /// <summary>
    /// Shelves a document, brings it back off the shelf, soft-deletes it, or restores a soft-deleted one.
    /// </summary>
    /// <param name="command">
    /// Which command was asked for. The route decided it; nothing the caller sent did.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>A repeat is not a move.</strong> A document already in the target state is answered with
    /// itself: no audit entry and no new token, because writing either for a move that did not happen would
    /// put a lie in the two records that have to be trustworthy. That is also what makes every one of these
    /// commands safe to retry without an idempotency key.
    /// </para>
    /// <para>
    /// <strong>Only a restore can see a tombstone.</strong> Archive, unarchive and remove answer
    /// <c>not_found</c> for a removed document exactly as the read and the download do, so a repeat removal
    /// is a 404 rather than a no-op. A restore is the one command that reads through the tombstone, which is
    /// what makes it the only way back.
    /// </para>
    /// <para>
    /// The token is checked before the repeat answer, as the recipe lifecycle checks it: a caller quoting a
    /// stale token has not seen what the document looks like now, and telling them "already archived" would
    /// hide a collaborator's work from them.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The document at its new state, <c>null</c> for a removal — a tombstone is published in one place only,
    /// and that is the removed-document list — or <c>brand.source.not_found</c> / <c>brand.source.conflict</c>.
    /// </returns>
    Task<OperationResult<BrandSourceDocumentDetailServiceModel?>> TransitionAsync(
        string actorUserId,
        Guid documentId,
        BrandSourceDocumentLifecycleCommand command,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// What would be left behind if this document were removed. Any member may read, like the detail.
    /// </summary>
    /// <remarks>
    /// Readable for an archived document as well as an active one, because the confirmation step this feeds
    /// is reached from both. Not for a removed one: that is a 404 like every other read of a tombstone.
    /// </remarks>
    Task<OperationResult<BrandSourceDocumentUsageServiceModel>> UsageAsync(
        Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the workspace's removed documents, newest removal first.
    /// </summary>
    /// <remarks>
    /// Owner only, and checked here rather than only at the route: a worker or an AI plugin reaching this
    /// seam would otherwise read a bin no role gate had been applied to. It is the one read in this module
    /// that answers for removed documents, and the only place a tombstone's concurrency token is published.
    /// </remarks>
    Task<OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>> ListRemovedAsync(
        RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken);


    /// <summary>
    /// One page of the library as a client reads it. No role gate: Viewer is the lowest role, so a resolved
    /// workspace context already is the authorization.
    /// </summary>
    Task<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>> ListAsync(
        BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IBrandSourceDocumentFacade.ListGroundingCandidatesAsync"/>
    Task<IReadOnlyList<BrandSourcePassageSelector>> ListGroundingCandidatesAsync(
        IReadOnlyCollection<BrandSourcePurpose> purposes,
        string? channelKey,
        string? audience,
        int limit,
        CancellationToken cancellationToken);

    Task<BrandSourceVisualReferenceListServiceModel> ListVisualReferencesAsync(
        int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One source document as a client reads it on its own.
    /// </summary>
    /// <returns>
    /// The document, or <c>brand.source.not_found</c> for an id that was never issued, another workspace's
    /// document, or one this workspace has removed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>A removed document is not found.</strong> <c>Removed</c> is a tombstone the library never lists,
    /// and a read that answered for one would make the status a way to keep reading something the workspace
    /// has said it is done with. An <strong>archived</strong> one still reads: archiving is a shelf, not a
    /// deletion, and a creator unarchives from the thing they are looking at.
    /// </para>
    /// <para>
    /// No role gate: Viewer is the lowest role, so a resolved workspace context already is the authorization —
    /// the same reasoning as the list.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandSourceDocumentDetailServiceModel>> GetAsync(
        Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one numbered version's original bytes for download.
    /// </summary>
    /// <returns>
    /// The open download — which the caller disposes — or <c>brand.source.not_found</c> for an unknown
    /// document, another workspace's, a removed one, or a version number this document does not have, and
    /// <c>brand.source.storage.unavailable</c> when the bytes cannot be reached.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The four not-found cases are one answer</strong>, so a caller cannot use a version number to
    /// count a document's versions or an id to learn that a document exists where they cannot see it.
    /// </para>
    /// <para>
    /// <strong>A missing object is a fault, not a missing document.</strong> The metadata is readable and the
    /// document plainly exists, so answering 404 would tell a creator their document is gone when it is their
    /// storage that is wrong. It is reported as unavailable, which is both true and retryable.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandSourceDownload>> OpenVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);
}

internal sealed class BrandSourceDocumentBusiness(
    IBrandSourceDocumentDataLayer dataLayer,
    IWorkspaceContext workspace,
    IContentChannelCatalog channels,
    IClock clock) : IBrandSourceDocumentBusiness
{
    private const string CannotUpload = "The file could not be uploaded.";

    public async Task<OperationResult<BrandSourcePreparedUpload>> PrepareAsync(
        UploadBrandSourceDocumentViewModel model, BrandSourceUploadFile file, CancellationToken cancellationToken)
    {
        // A retired channel cannot be newly chosen. Checked before the file is read, so it costs no scan.
        if (BrandProfileInputChecks.Normalize(model.ChannelKey) is { } channelKey && channels.Find(channelKey) is { IsActive: false })
        {
            return Refused(OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest,
                CannotUpload,
                [(nameof(model.ChannelKey), "That channel is no longer available.")]));
        }

        var accepted = await AcceptAsync(file, cancellationToken);

        return accepted.Succeeded
            ? OperationResult<BrandSourcePreparedUpload>.Success(new BrandSourcePreparedUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                accepted.Value!.MediaType,
                accepted.Value.SizeBytes,
                accepted.Value.ContentChecksum,
                accepted.Value.FileName,
                file.Content))
            : OperationResult<BrandSourcePreparedUpload>.Failure(accepted.Error!);
    }

    public async Task<OperationResult<BrandSourcePreparedReplacement>> PrepareReplacementAsync(
        Guid documentId, string? expectedConcurrencyToken, BrandSourceUploadFile file, CancellationToken cancellationToken)
    {
        // Whether this document may take a version at all, before 20 MB is read, hashed and handed to a
        // scanner. The same three answers the storing step will reach again on the tracked row — this pass
        // exists to spend nothing on a request that cannot succeed, not to decide anything on its own.
        if ((await RefuseReplacementAsync(documentId, expectedConcurrencyToken, cancellationToken)).Error is { } refusal)
        {
            return OperationResult<BrandSourcePreparedReplacement>.Failure(refusal);
        }

        var accepted = await AcceptAsync(file, cancellationToken);

        return accepted.Succeeded
            ? OperationResult<BrandSourcePreparedReplacement>.Success(new BrandSourcePreparedReplacement(
                documentId,

                // New, which is what makes the object key new: the previous version's bytes are not reachable
                // from anything this request will write.
                Guid.NewGuid(),
                accepted.Value!.MediaType,
                accepted.Value.SizeBytes,
                accepted.Value.ContentChecksum,
                accepted.Value.FileName,
                file.Content))
            : OperationResult<BrandSourcePreparedReplacement>.Failure(accepted.Error!);
    }

    /// <summary>
    /// What a file is, whether it holds together, and whether it is safe. The whole of the file-acceptance
    /// rule, shared by the first upload and every replacement so the two cannot drift.
    /// </summary>
    private async Task<OperationResult<BrandSourceAcceptedFile>> AcceptAsync(
        BrandSourceUploadFile file, CancellationToken cancellationToken)
    {
        var fileName = BrandSourceFileName.Clean(file.FileName)
            ?? throw new ArgumentException("An upload reaches Business with a usable filename.", nameof(file));

        var inspection = await BrandSourceFileInspector.InspectAsync(file.Content, fileName, cancellationToken);

        if (inspection.Outcome != BrandSourceInspectionOutcome.Accepted)
        {
            return OperationResult<BrandSourceAcceptedFile>.Failure(Refusal(inspection.Outcome));
        }

        file.Content.Position = 0;
        var verdict = await dataLayer.ScanAsync(file.Content, cancellationToken);
        file.Content.Position = 0;

        return verdict switch
        {
            MalwareScanVerdict.Clean => OperationResult<BrandSourceAcceptedFile>.Success(new BrandSourceAcceptedFile(
                inspection.MediaType!, inspection.SizeBytes, inspection.ContentChecksum!, fileName)),

            // Says nothing of what was recognised: that is for the scanner's own log, not for whoever sent it.
            MalwareScanVerdict.Infected => OperationResult<BrandSourceAcceptedFile>.Failure(FileError(
                BrandErrorCodes.SourceFileRejected, "This file did not pass the safety check and was not uploaded.")),

            // Anything that is not a clean verdict is a refusal, including a verdict this code does not know.
            _ => OperationResult<BrandSourceAcceptedFile>.Failure(new OperationError(
                BrandErrorCodes.SourceScanUnavailable,
                "Files cannot be checked for safety right now, so nothing was uploaded. Try again shortly.",
                new Dictionary<string, string[]>())),
        };
    }

    /// <summary>
    /// Why this document cannot take a new version, or null when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One place deciding all three refusals, called once before the file is read and once on the tracked row
    /// inside the idempotent unit. The second call is the one that counts; the first only saves the work.
    /// </para>
    /// <para>
    /// <strong>Removed is not found</strong>, exactly as on the read and the download: a tombstone the library
    /// never lists cannot become writable again through this route. <strong>Archived is a conflict</strong>,
    /// because a shelved document plainly exists — a creator can read it and download it — and the remedy is
    /// to restore it, which a 404 would hide.
    /// </para>
    /// <para>
    /// The token is compared last, so a caller holding a stale token for a document they cannot see learns
    /// only that they cannot see it.
    /// </para>
    /// </remarks>
    private async Task<(BrandSourceDocument? Document, OperationError? Error)> RefuseReplacementAsync(
        Guid documentId, string? expectedConcurrencyToken, CancellationToken cancellationToken)
    {
        var document = await dataLayer.FindForUpdateAsync(documentId, cancellationToken);

        var error = document switch
        {
            null or { Status: BrandSourceDocumentStatus.Removed } => NotFound(),

            { Status: BrandSourceDocumentStatus.Archived } => new OperationError(
                BrandErrorCodes.SourceArchivedConflict,
                "This document is archived. Restore it before replacing its file.",
                new Dictionary<string, string[]>()),

            _ when !BrandConcurrencyToken.Matches(expectedConcurrencyToken, document.RowVersion) => ReplaceConflict(),

            _ => null,
        };

        return (error is null ? document : null, error);
    }

    public async Task<OperationResult<BrandSourceDocumentServiceModel>> StoreAsync(
        string actorUserId, UploadBrandSourceDocumentViewModel model, BrandSourcePreparedUpload upload, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;

        // From the resolved context, never the request: the tag links carry it in their key, so it cannot be
        // left for the ownership interceptor to stamp, and the interceptor still refuses any other value.
        var workspaceId = workspace.WorkspaceId;

        var document = new BrandSourceDocument
        {
            Id = upload.DocumentId,
            WorkspaceId = workspaceId,
            Title = BrandProfileInputChecks.Normalize(model.Title)!,
            DocumentType = model.DocumentType!.Value,
            Purpose = model.Purpose!.Value,
            ChannelKey = BrandProfileInputChecks.Normalize(model.ChannelKey),
            Audience = BrandProfileInputChecks.Normalize(model.Audience),
            Status = BrandSourceDocumentStatus.Active,
            CurrentVersionNumber = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = membershipId,
            UpdatedByMembershipId = membershipId,
        };

        var version = new BrandSourceDocumentVersion
        {
            Id = upload.VersionId,
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = document.Id,
            VersionNumber = 1,
            MediaType = upload.MediaType,
            SizeBytes = upload.SizeBytes,
            ContentChecksum = upload.ContentChecksum,
            OriginalFileName = upload.OriginalFileName,
            CreatedByMembershipId = membershipId,
            CreatedAt = now,
        };

        var tags = (model.Tags ?? [])
            .Select(tag => BrandProfileInputChecks.Normalize(tag)!)
            .Select(name => new BrandSourceTagInput(name, NameNormalization.NormalizeName(name)))
            .ToList();

        // Queued with the version, not after it. Every version is queued, whatever its format: an image's
        // extraction will be a review state saying there is no text, and a version with no operation would be
        // left reading as "not extracted", which a creator cannot tell from "still working".
        var extraction = BrandSourceExtractionQueue.For(workspaceId, document.Id, version.Id, now);

        var result = await dataLayer.StoreAsync(
            document, version, tags, extraction, Audit(actorUserId, document), upload.Content, cancellationToken);

        return result.Outcome switch
        {
            BrandSourceStoreOutcome.Stored => OperationResult<BrandSourceDocumentServiceModel>.Success(
                ToServiceModel(document, version, result.TagNames)),

            BrandSourceStoreOutcome.StorageUnavailable => OperationResult<BrandSourceDocumentServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SourceStorageUnavailable,
                "Files cannot be stored right now, so nothing was uploaded. Try again shortly.",
                new Dictionary<string, string[]>())),

            _ => OperationResult<BrandSourceDocumentServiceModel>.Failure(FileError(
                BrandErrorCodes.SourceFileCorrupt, "The file could not be read consistently. Upload it again.")),
        };
    }

    public async Task<OperationResult<BrandSourceDocumentServiceModel>> StoreReplacementAsync(
        string actorUserId, string? expectedConcurrencyToken, BrandSourcePreparedReplacement replacement, CancellationToken cancellationToken)
    {
        // The same three questions the pre-check asked, asked again of the row this is about to write to.
        // The pre-check's row was read before the file was scanned and may be stale by now, and a retrying
        // execution strategy re-runs this whole method — so the document here has to be the one just loaded,
        // not one carried in from earlier. This is the answer that counts; the first only saved the work.
        var (document, refusal) = await RefuseReplacementAsync(
            replacement.DocumentId, expectedConcurrencyToken, cancellationToken);

        if (refusal is not null)
        {
            return OperationResult<BrandSourceDocumentServiceModel>.Failure(refusal);
        }

        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;
        var previousVersionNumber = document!.CurrentVersionNumber;

        var version = new BrandSourceDocumentVersion
        {
            Id = replacement.VersionId,

            // From the resolved context, never the request, for the same reason as an upload: the row is
            // keyed on it, so it cannot be left for the ownership interceptor to stamp.
            WorkspaceId = workspace.WorkspaceId,
            BrandSourceDocumentId = document.Id,

            // The counter the document carries, not a number the client sent or the pre-check settled. The
            // unique index on (workspace, document, version number) is the second guard on two replacements
            // that both read the same N.
            VersionNumber = previousVersionNumber + 1,
            MediaType = replacement.MediaType,
            SizeBytes = replacement.SizeBytes,
            ContentChecksum = replacement.ContentChecksum,
            OriginalFileName = replacement.OriginalFileName,
            CreatedByMembershipId = membershipId,
            CreatedAt = now,
        };

        // The root's only changes. Title, type, purpose, channel, audience, tags and status are the creator's
        // description of the document and are not this operation's to touch; the earlier version row is
        // immutable and is not loaded, let alone written.
        document.CurrentVersionNumber = version.VersionNumber;
        document.UpdatedAt = now;
        document.UpdatedByMembershipId = membershipId;

        // The new version's own queued extraction, for the same reason the upload's is queued with its version.
        // The previous version keeps the extraction it already had: a guide version that cited that text still
        // cites it, because the citation pins a version rather than a document.
        var extraction = BrandSourceExtractionQueue.For(
            workspace.WorkspaceId, document.Id, version.Id, now);

        var result = await dataLayer.ReplaceAsync(
            document,
            version,
            extraction,
            Audit(actorUserId, document, previousVersionNumber),
            replacement.Content,
            cancellationToken);

        return result.Outcome switch
        {
            BrandSourceReplaceOutcome.Replaced => OperationResult<BrandSourceDocumentServiceModel>.Success(
                ToServiceModel(document, version, result.TagNames)),

            BrandSourceReplaceOutcome.Conflict => OperationResult<BrandSourceDocumentServiceModel>.Failure(ReplaceConflict()),

            BrandSourceReplaceOutcome.StorageUnavailable => OperationResult<BrandSourceDocumentServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SourceStorageUnavailable,
                "Files cannot be stored right now, so nothing was replaced. Try again shortly.",
                new Dictionary<string, string[]>())),

            _ => OperationResult<BrandSourceDocumentServiceModel>.Failure(FileError(
                BrandErrorCodes.SourceFileCorrupt, "The file could not be read consistently. Upload it again.")),
        };
    }

    public Task AbandonAsync(Guid documentId, Guid versionId) => dataLayer.AbandonAsync(documentId, versionId);

    public async Task<OperationResult<BrandSourceDocumentDetailServiceModel?>> TransitionAsync(
        string actorUserId,
        Guid documentId,
        BrandSourceDocumentLifecycleCommand command,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken)
    {
        var target = TargetOf(command);
        var document = await dataLayer.FindForUpdateAsync(documentId, cancellationToken);

        // A restore is the one command that reads through the tombstone; for the other three a removed
        // document is not found, exactly as it is for the read, the download and the replacement.
        if (document is null
            || (document.Status == BrandSourceDocumentStatus.Removed && command != BrandSourceDocumentLifecycleCommand.Restore))
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel?>.Failure(NotFound());
        }

        // Before anything is touched, and before the repeat answer below: a caller quoting a stale token has
        // not seen what the document looks like now, and telling them "already archived" would hide a
        // collaborator's work from them. The same ordering the recipe lifecycle uses.
        if (!BrandConcurrencyToken.Matches(expectedConcurrencyToken, document.RowVersion))
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel?>.Failure(ReplaceConflict());
        }

        if (document.Status == target)
        {
            // A repeat, not a move. The honest answer is the document, and writing an audit entry for a move
            // that did not happen would put a lie in the record this feature exists to keep.
            return await ReadBackAsync(documentId, target, cancellationToken);
        }

        var from = document.Status;
        var now = clock.UtcNow;

        document.Status = target;
        document.UpdatedAt = now;
        document.UpdatedByMembershipId = workspace.MembershipId;

        // Kept through an unarchive and through a restore: it records when the document was last shelved, not
        // whether it is shelved now.
        document.ArchivedAt = target == BrandSourceDocumentStatus.Archived ? now : document.ArchivedAt;

        // Forced by CK_BrandSourceDocuments_Removed_Consistent, which refuses a removal that does not name
        // when and who, and refuses the pair on anything that is not removed. So a restore cannot keep them,
        // and the two audit entries are the only lasting record that the document was ever removed.
        var removed = target == BrandSourceDocumentStatus.Removed;
        document.RemovedAt = removed ? now : null;
        document.RemovedByMembershipId = removed ? workspace.MembershipId : null;

        if (!await dataLayer.SaveTransitionAsync(document, Audit(actorUserId, command, from, document.Id), cancellationToken))
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel?>.Failure(ReplaceConflict());
        }

        return await ReadBackAsync(documentId, target, cancellationToken);
    }

    /// <summary>
    /// The document as the detail route would answer for it, or nothing when it has just been removed.
    /// </summary>
    /// <remarks>
    /// Read back rather than mapped from the tracked entity, so a caller gets the token the server just
    /// generated and exactly the shape the detail route returns. A removal reports no document on purpose:
    /// the thing it produced is invisible to every read, and answering with it here would make this the
    /// second place a tombstone is published.
    /// </remarks>
    private async Task<OperationResult<BrandSourceDocumentDetailServiceModel?>> ReadBackAsync(
        Guid documentId, BrandSourceDocumentStatus target, CancellationToken cancellationToken)
    {
        if (target == BrandSourceDocumentStatus.Removed)
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel?>.Success(null);
        }

        var read = await GetAsync(documentId, cancellationToken);

        return read.Succeeded
            ? OperationResult<BrandSourceDocumentDetailServiceModel?>.Success(read.Value)
            : OperationResult<BrandSourceDocumentDetailServiceModel?>.Failure(read.Error!);
    }

    public async Task<OperationResult<BrandSourceDocumentUsageServiceModel>> UsageAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        // The same readability rule as the detail: an archived document still answers, a removed one does not.
        if (await dataLayer.FindStatusAsync(documentId, cancellationToken)
            is not { } status || status == BrandSourceDocumentStatus.Removed)
        {
            return OperationResult<BrandSourceDocumentUsageServiceModel>.Failure(NotFound());
        }

        var usage = await dataLayer.UsageAsync(documentId, cancellationToken);

        return OperationResult<BrandSourceDocumentUsageServiceModel>.Success(new BrandSourceDocumentUsageServiceModel(
            documentId,
            usage.Size.VersionCount,
            usage.Size.StoredBytes,

            // An approved holder is what a retention job has to honour. A draft one is a link that may still
            // be superseded, so it is listed without making the document held.
            usage.Holds.Any(hold => hold.IsApproved),
            [.. usage.Holds.Select(hold => new BrandSourceHoldServiceModel(
                BrandSourceHoldKind.StyleGuideVersion,
                hold.GuideId,
                hold.GuideDisplayName,
                hold.GuideVersionNumber,
                hold.SourceVersionNumber,
                hold.IsApproved))]));
    }

    public async Task<OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>> ListRemovedAsync(
        RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken)
    {
        // Owner, because this is the only read that answers for removed documents and the only place a
        // tombstone's token is published — which is to say, the only way to reach the restore at all.
        if (workspace.Role < WorkspaceRole.Owner)
        {
            return OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>.Failure(new OperationError(
                BrandErrorCodes.SourceForbidden,
                "Only a workspace owner can see removed brand source documents.",
                new Dictionary<string, string[]>()));
        }

        var page = await dataLayer.ListRemovedAsync(criteria, cancellationToken);

        return OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>.Success(
            PageBuilder.Build(page.Rows, page.HasMore, criteria.Scope, row => new RemovedBrandSourceDocumentServiceModel(
                row.Id,
                row.Title,
                row.DocumentType,
                row.Purpose,
                row.CurrentVersionNumber,
                row.RemovedAt,
                row.RemovedByMembershipId,
                row.CreatedAt,
                BrandConcurrencyToken.From(row.RowVersion))));
    }


    public async Task<IReadOnlyList<BrandSourcePassageSelector>> ListGroundingCandidatesAsync(
        IReadOnlyCollection<BrandSourcePurpose> purposes,
        string? channelKey,
        string? audience,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purposes);

        if (purposes.Count == 0 || limit <= 0)
        {
            return [];
        }

        // Active documents only, and deliberately not filtered by channel: a document carrying no channel is
        // the brand's general writing and applies to every channel, so filtering on one would throw away the
        // documents most likely to be relevant. The channel is a ranking signal below instead.
        var criteria = new BrandSourceDocumentListCriteria(
            new BrandSourceDocumentListFilters(
                BrandSourceDocumentStatus.Active, DocumentType: null, ChannelKey: null, TagNames: [], Search: null),

            // A scope this read never issues a cursor for. It is required by the criteria and unused here,
            // because one page is all a grounding selection considers.
            Scope: GroundingScope,
            Position: null,
            RequestedLimit: CandidatePageSize);

        var page = await dataLayer.ListAsync(criteria, cancellationToken);

        return
        [
            .. page.Rows
                .Where(document => purposes.Contains(document.Purpose))
                .OrderByDescending(document => Specificity(document, channelKey, audience))
                .ThenByDescending(document => document.UpdatedAt)

                // A total order, so two documents updated in the same instant rank the same way twice and the
                // selection is reproducible.
                .ThenBy(document => document.Id)
                .Take(limit)
                .Select(document => new BrandSourcePassageSelector(document.Id, document.VersionNumber)),
        ];
    }

    public async Task<BrandSourceVisualReferenceListServiceModel> ListVisualReferencesAsync(
        int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return new BrandSourceVisualReferenceListServiceModel([], false);
        }

        var criteria = new BrandSourceDocumentListCriteria(
            new BrandSourceDocumentListFilters(
                BrandSourceDocumentStatus.Active, DocumentType: null, ChannelKey: null, TagNames: [], Search: null),
            Scope: VisualReferenceScope,
            Position: null,
            RequestedLimit: CandidatePageSize);

        var page = await dataLayer.ListAsync(criteria, cancellationToken);

        var matches = page.Rows
            .Where(document => document.Purpose == BrandSourcePurpose.VisualDirection
                || document.DocumentType == BrandSourceDocumentType.VisualReference)
            .OrderByDescending(document => document.UpdatedAt)
            .ThenBy(document => document.Id)
            .ToList();

        return new BrandSourceVisualReferenceListServiceModel(
            [.. matches.Take(limit).Select(document => new BrandSourceVisualReferenceServiceModel(
                document.Id,
                document.Title,
                document.VersionNumber,
                Extraction(document.ExtractionStatus, document.ExtractionOrigin, document.ExtractionAt).State))],
            page.HasMore || matches.Count > limit);
    }

    /// <summary>
    /// How well one document matches what is being written, as a rank rather than a filter.
    /// </summary>
    /// <remarks>
    /// Ranked so that a library whose documents carry no channel or audience tags still supplies evidence. A
    /// document tagged for this channel beats one tagged for none, which beats one tagged for another — and
    /// nothing is excluded, because a creator who wanted an exact document names it instead.
    /// </remarks>
    private static int Specificity(
        BrandSourceDocumentSummaryRecord document, string? channelKey, string? audience)
    {
        var score = 0;

        if (channelKey is not null)
        {
            score += string.Equals(document.ChannelKey, channelKey, StringComparison.Ordinal) ? 4
                : document.ChannelKey is null ? 2
                : 0;
        }

        if (audience is not null)
        {
            score += string.Equals(document.Audience, audience, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        return score;
    }

    /// <summary>
    /// How many of the library's recent documents a grounding selection considers.
    /// </summary>
    /// <remarks>
    /// One page, so the read stays bounded however large the library is: a workspace with hundreds of documents
    /// draws from its most recently updated ones, and a creator who wants a particular older document names it.
    /// Deliberately not "every document scored".
    /// </remarks>
    private const int CandidatePageSize = ReferencePolicy.DefaultPageSize;

    /// <summary>A scope string for a read that issues no cursor. Distinct, so it can never resolve one.</summary>
    private const string GroundingScope = "brand-source-documents:grounding";

    /// <summary>A scope this read never issues a cursor for, required by the criteria and unused.</summary>
    private const string VisualReferenceScope = "brand-source-documents:visual-references";

    public async Task<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>> ListAsync(
        BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken)
    {
        var page = await dataLayer.ListAsync(criteria, cancellationToken);

        return PageBuilder.Build(page.Rows, page.HasMore, criteria.Scope, row => new BrandSourceDocumentSummaryServiceModel(
            row.Id,
            row.Title,
            row.DocumentType,
            row.Purpose,
            row.ChannelKey,
            row.Audience,
            page.TagNames.TryGetValue(row.Id, out var tags) ? tags : [],
            row.Status,
            new BrandSourceDocumentVersionServiceModel(
                row.VersionId, row.VersionNumber, row.MediaType, row.SizeBytes, row.OriginalFileName, row.VersionCreatedAt),
            Extraction(row.ExtractionStatus, row.ExtractionOrigin, row.ExtractionAt),
            row.CreatedAt,
            row.UpdatedAt));
    }

    public async Task<OperationResult<BrandSourceDocumentDetailServiceModel>> GetAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindDetailAsync(documentId, cancellationToken) is not { } detail)
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel>.Failure(NotFound());
        }

        // Decided here rather than in the query, so that "removed" and "never existed" are one answer written
        // in one place — a status predicate in the repository would make the same decision invisibly, and the
        // next read would have to remember to repeat it.
        if (detail.Record.Status == BrandSourceDocumentStatus.Removed)
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel>.Failure(NotFound());
        }

        var record = detail.Record;

        return OperationResult<BrandSourceDocumentDetailServiceModel>.Success(new BrandSourceDocumentDetailServiceModel(
            record.Id,
            record.Title,
            record.DocumentType,
            record.Purpose,
            record.ChannelKey,
            record.Audience,
            detail.TagNames,
            record.Status,
            new BrandSourceDocumentVersionDetailServiceModel(
                record.VersionId,
                record.VersionNumber,
                record.MediaType,
                record.SizeBytes,
                record.OriginalFileName,
                record.ContentChecksum,
                record.VersionCreatedAt),
            Extraction(record.ExtractionStatus, record.ExtractionOrigin, record.ExtractionAt),
            record.ArchivedAt,
            record.CreatedAt,
            record.UpdatedAt,
            BrandConcurrencyToken.From(record.RowVersion)));
    }

    public async Task<OperationResult<BrandSourceDownload>> OpenVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken)
    {
        // A version number is a positive counter. Refused before storage is touched, and as "not found"
        // rather than as a validation error, because 0 and -1 are not versions any document has.
        if (versionNumber < 1)
        {
            return OperationResult<BrandSourceDownload>.Failure(NotFound());
        }

        // The document's own readability first, so a removed document refuses its bytes exactly as it refuses
        // its metadata. A status and nothing else: the version query below cannot see a status, and the full
        // detail read would project a version this does not want and then read tag names nobody will show.
        //
        // The rule lives here rather than in the version query on purpose — "removed is not readable" is a
        // decision about a document, and one place deciding it is what keeps the two routes agreeing. The
        // consequence is that a future caller reaching the DataLayer directly would not get the check, which
        // is why nothing below this layer is a supported entry point.
        if (await dataLayer.FindStatusAsync(documentId, cancellationToken)
            is not { } status || status == BrandSourceDocumentStatus.Removed)
        {
            return OperationResult<BrandSourceDownload>.Failure(NotFound());
        }

        var result = await dataLayer.OpenVersionAsync(documentId, versionNumber, cancellationToken);

        return result.Outcome switch
        {
            BrandSourceDownloadOutcome.Opened => OperationResult<BrandSourceDownload>.Success(result.Download!),

            BrandSourceDownloadOutcome.Missing => OperationResult<BrandSourceDownload>.Failure(NotFound()),

            // Both say the bytes cannot be had right now and both are worth retrying, so they are one answer
            // to a client. They are separate outcomes below because only one of them is a bug to chase.
            _ => OperationResult<BrandSourceDownload>.Failure(new OperationError(
                BrandErrorCodes.SourceStorageUnavailable,
                "That file could not be read right now. Nothing about the document has changed.",
                new Dictionary<string, string[]>())),
        };
    }

    /// <summary>
    /// How the latest extraction attempt ended, or that there has not been one.
    /// </summary>
    /// <remarks>
    /// Shared by the list and the detail so the two cannot describe the same version differently. A status
    /// this does not name is <c>Failed</c>, which is the honest reading of "an attempt that did not succeed".
    /// </remarks>
    private static BrandSourceExtractionSummaryServiceModel Extraction(
        BrandSourceExtractionStatus? status,
        BrandSourceExtractionOrigin? origin,
        DateTimeOffset? at) =>
        new(
            status switch
            {
                null => BrandSourceExtractionState.NotExtracted,
                BrandSourceExtractionStatus.Succeeded => BrandSourceExtractionState.Succeeded,
                BrandSourceExtractionStatus.Unsupported => BrandSourceExtractionState.Unsupported,
                _ => BrandSourceExtractionState.Failed,
            },
            origin,
            at);

    /// <summary>
    /// One refusal for every way a document or a version can fail to be readable. See
    /// <see cref="BrandErrorCodes.SourceNotFound"/> for why they are deliberately indistinguishable.
    /// </summary>
    private static OperationError NotFound() => new(
        BrandErrorCodes.SourceNotFound,
        "That source document could not be found.",
        new Dictionary<string, string[]>());

    /// <summary>
    /// The document moved on under a replacement. One message whether the pre-check caught it, the tracked
    /// re-read caught it, or the save's own row-version guard did.
    /// </summary>
    private static OperationError ReplaceConflict() => new(
        BrandErrorCodes.SourceConflict,
        "This document changed while you were replacing its file. Read it again and retry.",
        new Dictionary<string, string[]>());

    /// <summary>A file that passed inspection and the scan. What it is, not where it will go.</summary>
    private sealed record BrandSourceAcceptedFile(string MediaType, long SizeBytes, string ContentChecksum, string FileName);

    private static OperationError Refusal(BrandSourceInspectionOutcome outcome) => outcome switch
    {
        BrandSourceInspectionOutcome.Empty => OperationError.Validation(
            BrandErrorCodes.SourceInvalidRequest, CannotUpload, [(BrandSourceInputChecks.FileField, "Choose a file to upload.")]),

        BrandSourceInspectionOutcome.TooLarge => FileError(
            BrandErrorCodes.SourceFileTooLarge,
            $"A file can be at most {BrandPolicy.SourceUploadMaxBytes / (1024 * 1024)} MB, and a text file at most {BrandPolicy.SourceTextUploadMaxBytes / (1024 * 1024)} MB."),

        BrandSourceInspectionOutcome.ImageTooLarge => FileError(
            BrandErrorCodes.SourceFileTooLarge, "This image has more pixels than can be accepted. Upload a smaller version."),

        BrandSourceInspectionOutcome.NameMismatch => FileError(
            BrandErrorCodes.SourceFileUnsupported, "This file's contents do not match its name. Check the file and its extension."),

        BrandSourceInspectionOutcome.Corrupt => FileError(
            BrandErrorCodes.SourceFileCorrupt, "This file appears to be damaged or incomplete."),

        _ => FileError(
            BrandErrorCodes.SourceFileUnsupported,
            "Upload a PDF, a Word document (.docx, without macros), Markdown, plain text, HTML, or a PNG, JPEG or WebP image."),
    };

    private static OperationError FileError(string code, string message) =>
        OperationError.Validation(code, CannotUpload, [(BrandSourceInputChecks.FileField, message)]);

    private static OperationResult<BrandSourcePreparedUpload> Refused(OperationError error) =>
        OperationResult<BrandSourcePreparedUpload>.Failure(error);

    // State references only: never the title or the filename, which are the creator's words.
    private static AuditEntry Audit(string actorUserId, BrandSourceDocument document) => new(
        actorUserId,
        BrandAuditActions.SourceDocumentUploaded,
        BrandAuditActions.SourceDocumentResourceType,
        document.Id.ToString("D"),
        CorrelationId(),
        "Uploaded a brand source document.",
        BeforeReference: null,
        AfterReference: document.CurrentVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A replacement, as state references on both sides: which version the document was on, and which it is
    /// on now. Never the title or the filename, which are the creator's words — the same rule as the upload's.
    /// </summary>
    private static AuditEntry Audit(string actorUserId, BrandSourceDocument document, int previousVersionNumber) => new(
        actorUserId,
        BrandAuditActions.SourceDocumentReplaced,
        BrandAuditActions.SourceDocumentResourceType,
        document.Id.ToString("D"),
        CorrelationId(),
        "Replaced a brand source document's file.",
        BeforeReference: previousVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AfterReference: document.CurrentVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// One lifecycle move. State references on both sides and nothing else — never the title, which is the
    /// creator's words, and never a reason, which this seam takes no field for.
    /// </summary>
    private static AuditEntry Audit(
        string actorUserId, BrandSourceDocumentLifecycleCommand command, BrandSourceDocumentStatus from, Guid documentId) => new(
        actorUserId,
        command switch
        {
            BrandSourceDocumentLifecycleCommand.Archive => BrandAuditActions.SourceDocumentArchived,
            BrandSourceDocumentLifecycleCommand.Unarchive => BrandAuditActions.SourceDocumentUnarchived,
            BrandSourceDocumentLifecycleCommand.Remove => BrandAuditActions.SourceDocumentRemoved,
            _ => BrandAuditActions.SourceDocumentRestored,
        },
        BrandAuditActions.SourceDocumentResourceType,
        documentId.ToString("D"),
        CorrelationId(),
        "Moved a brand source document between states.",
        BeforeReference: from.ToString(),
        AfterReference: TargetOf(command).ToString());

    /// <summary>Where each command lands a document. The one place that mapping is written.</summary>
    private static BrandSourceDocumentStatus TargetOf(BrandSourceDocumentLifecycleCommand command) => command switch
    {
        BrandSourceDocumentLifecycleCommand.Unarchive => BrandSourceDocumentStatus.Active,
        BrandSourceDocumentLifecycleCommand.Remove => BrandSourceDocumentStatus.Removed,

        // Archive and Restore both land here, which is exactly why the command rather than the target is
        // what travels down from the route.
        _ => BrandSourceDocumentStatus.Archived,
    };

    private static Guid CorrelationId()
    {
        // The request's trace id, as on the brand profile's audit rows, so the two can be searched alike.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static BrandSourceDocumentServiceModel ToServiceModel(
        BrandSourceDocument document, BrandSourceDocumentVersion version, IReadOnlyList<string> tagNames) => new(
        document.Id,
        document.Title,
        document.DocumentType,
        document.Purpose,
        document.ChannelKey,
        document.Audience,
        tagNames,
        document.Status,
        new BrandSourceDocumentVersionServiceModel(
            version.Id, version.VersionNumber, version.MediaType, version.SizeBytes, version.OriginalFileName, version.CreatedAt),
        document.CreatedAt,
        document.UpdatedAt,
        BrandConcurrencyToken.From(document.RowVersion));
}
