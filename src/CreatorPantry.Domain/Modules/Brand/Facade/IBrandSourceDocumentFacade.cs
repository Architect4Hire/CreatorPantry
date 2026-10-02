using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>The application boundary for brand source documents: upload and the library list.</summary>
public interface IBrandSourceDocumentFacade
{
    /// <summary>
    /// Uploads a file as a new source document at version 1. Editor or above, and an idempotency key is
    /// required: without one a retry would store the file twice.
    /// </summary>
    /// <param name="file">Null when the request carried no file part, which is a validation failure.</param>
    Task<IdempotentOutcome<BrandSourceDocumentServiceModel>> UploadAsync(
        string userId,
        UploadBrandSourceDocumentViewModel model,
        BrandSourceUploadFile? file,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the resolved workspace's library, newest edit first. Any member may read. Not cached: the
    /// key would have to carry every filter and cursor, and no write could enumerate what to invalidate.
    /// </summary>
    /// <summary>
    /// One source document as a client reads it on its own: its description, its current version's metadata,
    /// its extraction state, and the token the next change must quote.
    /// </summary>
    /// <param name="documentId">The document to read, from the route.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The document, or <c>brand.source.not_found</c> for an unknown id, another workspace's document, or one
    /// this workspace has removed — deliberately one answer.
    /// </returns>
    /// <remarks>
    /// A read, so no idempotency key and nothing to replay, and no validation step: the id is a route-bound
    /// Guid, so there is no shape left to refuse. Every member may read, like the list.
    /// </remarks>
    Task<OperationResult<BrandSourceDocumentDetailServiceModel>> GetAsync(
        Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one numbered version's original file for download.
    /// </summary>
    /// <param name="documentId">The document the version belongs to, from the route.</param>
    /// <param name="versionNumber">The version to download, as the document's metadata numbers it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// An open <see cref="BrandSourceDownload"/> the caller must dispose, or
    /// <c>brand.source.not_found</c> / <c>brand.source.storage.unavailable</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The only operation on this boundary that returns an open stream</strong>, which makes disposal
    /// the caller's responsibility — the controller hands it to a <c>FileStreamResult</c>, which disposes it
    /// when the response ends. A caller that drops it without disposing leaks the store's lease.
    /// </para>
    /// <para>
    /// It returns no address of any kind: no object key, no container, no signed link. Issuing a URL a browser
    /// could follow would be a second authorization surface with its own lifetime, and this product has not
    /// decided to have one (media.md).
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandSourceDownload>> OpenVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the document's file with a new immutable version, keeping every earlier one. Editor or above,
    /// and both a concurrency token and an idempotency key are required.
    /// </summary>
    /// <param name="documentId">The document to replace, from the route.</param>
    /// <param name="model">Carries the token of the read this replacement was composed against, and nothing else.</param>
    /// <param name="file">Null when the request carried no file part, which is a validation failure.</param>
    /// <remarks>
    /// <para>
    /// <strong>Nothing is overwritten.</strong> The new version gets a new row and a new private object; the
    /// previous version's row is immutable, its object is never named by this operation, and both remain
    /// downloadable. A style-guide version that cited the old upload still cites it afterwards, because the
    /// citation pins a version rather than a document.
    /// </para>
    /// <para>
    /// The token guards the <em>document</em>, which is what the new version's number is read from, so a
    /// replacement composed against a picture someone else has already moved is refused rather than
    /// numbered against a stale count.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<BrandSourceDocumentServiceModel>> ReplaceAsync(
        string userId,
        Guid documentId,
        ReplaceBrandSourceDocumentViewModel model,
        BrandSourceUploadFile? file,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>>> ListAsync(
        BrandSourceDocumentListViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> of the workspace's documents worth grounding a generation in, as exact
    /// document versions, most relevant first.
    /// </summary>
    /// <param name="purposes">What the caller can use a document as evidence of. Empty selects nothing.</param>
    /// <param name="channelKey">The channel being written for, or null. A ranking signal, not a filter.</param>
    /// <param name="audience">Who is being written for, or null. A ranking signal, not a filter.</param>
    /// <param name="limit">The most to return. The caller's budget, not this module's.</param>
    /// <remarks>
    /// <para>
    /// <strong>Not exposed over HTTP, and it takes no view model.</strong> Its caller is 11A.19's brand-context
    /// assembler, which needs "a few documents worth reading for this job" and has no business building a
    /// library query — the facts a document is ranked on, its purpose, channel and audience, are this module's.
    /// A view model crossing the boundary would be a defect <c>ModuleBoundaryTests</c> refuses, and rightly: it
    /// is an HTTP shape, not a contract between modules.
    /// </para>
    /// <para>
    /// <strong>Active documents only, ranked rather than filtered, and bounded.</strong> A document carrying no
    /// channel is the brand's general writing and ranks above one tagged for a different channel; nothing is
    /// excluded on a tag, because a creator who wanted an exact document names it. Candidates come from one page
    /// of the library, so the read stays bounded however large it is.
    /// </para>
    /// <para>
    /// Role is deliberately not checked, as on the passage read: the material is the workspace's own documents,
    /// which any member may read, and the caller is a background task running under a resolved workspace.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<BrandSourcePassageSelector>> ListGroundingCandidatesAsync(
        IReadOnlyCollection<BrandSourcePurpose> purposes,
        string? channelKey,
        string? audience,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The workspace's active documents that could be named as visual references: those typed as visual references
    /// or given the visual-direction purpose, most recently updated first, at most <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// The facts a document is selected on are this module's, so the selection lives here rather than in a caller
    /// that would have to name a view model across the boundary. Only the most recent page of the library is
    /// considered; <c>Truncated</c> says when that, or the limit, left some out.
    /// </remarks>
    Task<BrandSourceVisualReferenceListServiceModel> ListVisualReferencesAsync(
        int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Shelves a document, brings it back, soft-deletes it, or restores a soft-deleted one.
    /// </summary>
    /// <param name="command">Which command was asked for. Set by the route, never by the caller's body.</param>
    /// <remarks>
    /// <para>
    /// Editor for the first three; <strong>Owner for a restore</strong>, which is the only command that can
    /// address a removed document and therefore the only way one comes back.
    /// </para>
    /// <para>
    /// No idempotency key: a document already in the target state is answered with itself and nothing is
    /// written, so a retry is safe without one. The concurrency token is still required — removing a document
    /// a collaborator is replacing should tell the remover it moved under them.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandSourceDocumentDetailServiceModel?>> TransitionAsync(
        string userId,
        Guid documentId,
        BrandSourceDocumentLifecycleCommand command,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentBusiness.UsageAsync"/>
    Task<OperationResult<BrandSourceDocumentUsageServiceModel>> UsageAsync(
        Guid documentId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentBusiness.ListRemovedAsync"/>
    Task<OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>> ListRemovedAsync(
        RemovedBrandSourceDocumentListViewModel model, CancellationToken cancellationToken);

}

internal sealed class BrandSourceDocumentFacade(
    IValidator<UploadBrandSourceDocumentViewModel> validator,
    IValidator<ReplaceBrandSourceDocumentViewModel> replaceValidator,
    IValidator<BrandSourceDocumentLifecycleViewModel> lifecycleValidator,
    IBrandSourceDocumentBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IBrandSourceDocumentFacade
{
    /// <summary>A stable operation name for the idempotency scope. Changing it orphans in-flight keys.</summary>
    private const string UploadOperation = "brand.source.upload";

    public async Task<IdempotentOutcome<BrandSourceDocumentServiceModel>> UploadAsync(
        string userId,
        UploadBrandSourceDocumentViewModel model,
        BrandSourceUploadFile? file,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, as for the brand profile: source documents are what the workspace's style guide is drawn from.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused(new OperationError(
                BrandErrorCodes.SourceForbidden,
                "You do not have permission to upload brand source documents in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);
        var failures = validation.Errors
            .Select(failure => (failure.PropertyName, failure.ErrorMessage))
            .Concat(BrandSourceInputChecks.File(file))
            .ToList();

        if (failures.Count > 0)
        {
            return Refused(OperationError.Validation(BrandErrorCodes.SourceInvalidRequest, "The file could not be uploaded.", failures));
        }

        // Two Business calls where one is the rule, because the second must run inside the idempotent unit and
        // the first cannot: the fingerprint that decides whether this is a replay includes the file's
        // checksum, which is only known once the file has been read. The first writes nothing.
        var prepared = await business.PrepareAsync(model, file!, cancellationToken);
        if (!prepared.Succeeded)
        {
            return Refused(prepared.Error!);
        }

        var upload = prepared.Value!;

        try
        {
            return await idempotency.ExecuteAsync(
                new IdempotentCommand(userId, workspace.WorkspaceId, UploadOperation, idempotencyKey, Fingerprint(model, upload)),
                token => business.StoreAsync(userId, model, upload, token),
                cancellationToken);
        }
        catch
        {
            // The store may have written its object before whatever failed here did — the commit, most
            // likely. A replayed request never reaches this: it returns without running the operation.
            await business.AbandonAsync(upload.DocumentId, upload.VersionId);
            throw;
        }
    }

    /// <summary>A stable operation name for the idempotency scope. Changing it orphans in-flight keys.</summary>
    private const string ReplaceOperation = "brand.source.replace";

    public async Task<IdempotentOutcome<BrandSourceDocumentServiceModel>> ReplaceAsync(
        string userId,
        Guid documentId,
        ReplaceBrandSourceDocumentViewModel model,
        BrandSourceUploadFile? file,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, as for the upload: a replacement writes the document's canonical file.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused(new OperationError(
                BrandErrorCodes.SourceForbidden,
                "You do not have permission to replace brand source documents in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await replaceValidator.ValidateAsync(model, cancellationToken);
        var failures = validation.Errors
            .Select(failure => (failure.PropertyName, failure.ErrorMessage))
            .Concat(BrandSourceInputChecks.File(file))
            .ToList();

        if (failures.Count > 0)
        {
            return Refused(OperationError.Validation(BrandErrorCodes.SourceInvalidRequest, "The file could not be uploaded.", failures));
        }

        // Two Business calls for the same reason as the upload's: the fingerprint that decides whether this is
        // a replay includes the file's checksum, which is only known once the file has been read. The first
        // writes nothing — and, unlike the upload's, it also refuses a document that cannot take a version at
        // all before any of those bytes are read.
        var prepared = await business.PrepareReplacementAsync(documentId, model.ExpectedConcurrencyToken, file!, cancellationToken);
        if (!prepared.Succeeded)
        {
            return Refused(prepared.Error!);
        }

        var replacement = prepared.Value!;

        try
        {
            return await idempotency.ExecuteAsync(
                new IdempotentCommand(userId, workspace.WorkspaceId, ReplaceOperation, idempotencyKey, ReplaceFingerprint(documentId, model, replacement)),
                token => business.StoreReplacementAsync(userId, model.ExpectedConcurrencyToken, replacement, token),
                cancellationToken);
        }
        catch
        {
            // The new object may have been written before whatever failed here did. The previous version's
            // object is not a candidate for removal on any path: this names only the version this request
            // created. A replayed request never reaches this — it returns without running the operation.
            await business.AbandonAsync(replacement.DocumentId, replacement.VersionId);
            throw;
        }
    }

    /// <remarks>
    /// Straight through, and deliberately: there is nothing to validate because the id is a route-bound Guid,
    /// nothing to cache because a document changes whenever anybody retitles or replaces it, and nothing to
    /// enrich because this route resolves no name. A facade that adds nothing is still the boundary — workers
    /// and AI plugins reach this seam here, not through Business.
    /// </remarks>
    public Task<OperationResult<BrandSourceDocumentDetailServiceModel>> GetAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        business.GetAsync(documentId, cancellationToken);

    /// <inheritdoc />
    public Task<OperationResult<BrandSourceDownload>> OpenVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken) =>
        business.OpenVersionAsync(documentId, versionNumber, cancellationToken);

    public async Task<OperationResult<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>>> ListAsync(
        BrandSourceDocumentListViewModel model, CancellationToken cancellationToken)
    {
        // The workspace comes from the resolved context, never the model, which has no field for it.
        if (!BrandSourceDocumentListQueryFactory.TryCreate(model, workspace.WorkspaceId, out var criteria, out var error))
        {
            return OperationResult<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>>.Failure(error!);
        }

        return OperationResult<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>>.Success(
            await business.ListAsync(criteria!, cancellationToken));
    }

    public Task<IReadOnlyList<BrandSourcePassageSelector>> ListGroundingCandidatesAsync(
        IReadOnlyCollection<BrandSourcePurpose> purposes,
        string? channelKey,
        string? audience,
        int limit,
        CancellationToken cancellationToken) =>
        business.ListGroundingCandidatesAsync(purposes, channelKey, audience, limit, cancellationToken);

    public Task<BrandSourceVisualReferenceListServiceModel> ListVisualReferencesAsync(
        int limit, CancellationToken cancellationToken) =>
        business.ListVisualReferencesAsync(limit, cancellationToken);

    // What makes two uploads the same request: the same description of the same bytes under the same name.
    // The generated identifiers are deliberately absent; they differ on every attempt.
    private static object Fingerprint(UploadBrandSourceDocumentViewModel model, BrandSourcePreparedUpload upload) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = BrandProfileInputChecks.Normalize(model.Title),
            ["documentType"] = model.DocumentType,
            ["purpose"] = model.Purpose,
            ["channelKey"] = BrandProfileInputChecks.Normalize(model.ChannelKey),
            ["audience"] = BrandProfileInputChecks.Normalize(model.Audience),
            ["tags"] = (model.Tags ?? []).Select(tag => NameNormalization.NormalizeName(tag!)).ToArray(),
            ["fileName"] = upload.OriginalFileName,
            ["sizeBytes"] = upload.SizeBytes,
            ["contentChecksum"] = upload.ContentChecksum,
        };


    public async Task<OperationResult<BrandSourceDocumentDetailServiceModel?>> TransitionAsync(
        string userId,
        Guid documentId,
        BrandSourceDocumentLifecycleCommand command,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken)
    {
        // A restore is an Owner's command; the other three are an Editor's. Checked here as well as at the
        // route, because workers and AI plugins reach this boundary without passing an [Authorize].
        if (workspace.Role < RequiredRole(command))
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel?>.Failure(new OperationError(
                BrandErrorCodes.SourceForbidden,
                "You do not have permission to change this brand source document in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await lifecycleValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<BrandSourceDocumentDetailServiceModel?>.Failure(OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest,
                "That document could not be changed as described.",
                [.. validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))]));
        }

        return await business.TransitionAsync(userId, documentId, command, model.ExpectedConcurrencyToken, cancellationToken);
    }

    public Task<OperationResult<BrandSourceDocumentUsageServiceModel>> UsageAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        business.UsageAsync(documentId, cancellationToken);

    public async Task<OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>> ListRemovedAsync(
        RemovedBrandSourceDocumentListViewModel model, CancellationToken cancellationToken)
    {
        // The workspace comes from the resolved context, never the model, which has no field for it.
        if (!RemovedBrandSourceDocumentListQueryFactory.TryCreate(model, workspace.WorkspaceId, out var criteria, out var error))
        {
            return OperationResult<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>.Failure(error!);
        }

        return await business.ListRemovedAsync(criteria!, cancellationToken);
    }

    /// <summary>
    /// The lowest role that may issue each command: Owner for a restore, Editor for the other three.
    /// </summary>
    /// <remarks>
    /// Asked of the command and not of the state it targets, because an archive and a restore target the
    /// same state — so a role decided from the target would let an Editor reach a restore through the state
    /// the two share.
    /// </remarks>
    private static WorkspaceRole RequiredRole(BrandSourceDocumentLifecycleCommand command) =>
        command == BrandSourceDocumentLifecycleCommand.Restore ? WorkspaceRole.Owner : WorkspaceRole.Editor;

    // What makes two replacements the same request: the same bytes, under the same name, offered for the
    // same document against the same read of it. The token is part of it deliberately — the same file sent
    // again after someone else's edit is a different request about a different state, and replaying the
    // earlier answer for it would be a lie about which version the document is on.
    private static object ReplaceFingerprint(
        Guid documentId, ReplaceBrandSourceDocumentViewModel model, BrandSourcePreparedReplacement replacement) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["documentId"] = documentId,
            ["expectedConcurrencyToken"] = model.ExpectedConcurrencyToken,
            ["fileName"] = replacement.OriginalFileName,
            ["sizeBytes"] = replacement.SizeBytes,
            ["contentChecksum"] = replacement.ContentChecksum,
        };

    private static IdempotentOutcome<BrandSourceDocumentServiceModel> Refused(OperationError error) =>
        new(OperationResult<BrandSourceDocumentServiceModel>.Failure(error), Replayed: false);
}
