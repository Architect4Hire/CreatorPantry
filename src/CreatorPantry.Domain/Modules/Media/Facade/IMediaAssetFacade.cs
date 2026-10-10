using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary for creating a DAM asset (DAM-001).
/// </summary>
/// <remarks>
/// <para>
/// Two entry points because there are two sources of bytes and they are validated differently, not because
/// there are two kinds of asset. They meet at one Business call and one transaction.
/// </para>
/// <para>
/// <strong>Idempotency has three layers, and only one of them is the header.</strong> Keeping a staged
/// image is naturally idempotent — the image is kept once, and a repeat returns the asset the first call
/// made. An upload has no natural key, because a creator may legitimately upload the same photograph twice
/// as two assets, so there the <c>Idempotency-Key</c> is the only thing standing between a lost response
/// and a duplicate asset. The unique object key is the third, and it is what stops a replay writing two
/// objects for one version.
/// </para>
/// </remarks>
public interface IMediaAssetFacade
{
    /// <summary>
    /// One page of the resolved workspace's library (DAM-002). Any member may read.
    /// </summary>
    /// <remarks>
    /// <strong>Not cached.</strong> <c>IRecipeFacade.SearchAsync</c> settles this for every search in the
    /// product: a key would have to include every filter, ordering and cursor, so one asset edit would
    /// invalidate an unbounded family of keys no write seam can enumerate — and <c>CachedPageReader</c> keys
    /// through <c>CacheKeys.Global</c>, which would put one workspace's asset titles where another reads.
    /// </remarks>
    Task<OperationResult<MediaAssetSearchPageServiceModel>> SearchAsync(
        MediaAssetSearchViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one asset of the resolved workspace in full (DAM-003).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not cached</strong>, for the reason <c>SearchAsync</c> gives: this read composes four Media
    /// statements with two other modules' reads, so its invalidation scope is "anything about this asset, its
    /// versions, its tags, its links or its prompts changed" — a key that would be wrong more often than useful.
    /// </para>
    /// <para>
    /// An unknown asset, another workspace's asset, and a soft-deleted one the caller did not ask for are one
    /// answer (tenancy.md).
    /// </para>
    /// </remarks>
    Task<OperationResult<MediaAssetDetailServiceModel>> GetDetailAsync(
        Guid mediaAssetId, MediaAssetDetailViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Changes part of one asset's metadata and returns it as it now stands (DAM-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Submitted-field semantics: a field the body does not mention is left alone, a value sets it, and
    /// <c>null</c> clears it. Tags replace. Nothing about the bytes can be reached from here — see
    /// <see cref="MediaAssetMetadataPatchViewModel"/> for what is deliberately absent and why.
    /// </para>
    /// <para>
    /// Takes an idempotency key so a retried request is replayed rather than answered as a conflict. Without one a
    /// client whose connection dropped after the server committed would re-send the same edit, find the token
    /// stale, and be told somebody else saved first — when the somebody else was itself.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<MediaAssetDetailServiceModel>> PatchMetadataAsync(
        string userId,
        Guid mediaAssetId,
        MediaAssetMetadataPatchViewModel patch,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes one asset and reports what still references it (DAM-005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires the <strong>Editor</strong> role, where creating and patching require Contributor. Removing a shared
    /// asset takes finished work out of every collaborator's library and can leave another creator's recipe pointing
    /// at a tombstone — the same asymmetry <c>RecipesController.Archive</c> names when it asks for Editor to archive
    /// a recipe.
    /// </para>
    /// <para>
    /// <strong>No idempotency key.</strong> The command is already idempotent — a repeat with a current token
    /// returns the existing tombstone and writes nothing — so a key would only add a store to the path. The
    /// consequence of checking the token first is that a retry whose response was lost needs a fresh read.
    /// </para>
    /// </remarks>
    Task<OperationResult<MediaAssetDeletionServiceModel>> SoftDeleteAsync(
        string userId,
        Guid mediaAssetId,
        DeleteMediaAssetViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a live asset's current version, for rendering (DAM-006) or for download (DAM-007).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any member may render, matching the reads: an asset a creator can see in the library is one they can look at.
    /// </para>
    /// <para>
    /// <strong>The caller owns disposing the render</strong>, and must register that before anything else that could
    /// throw — the bytes are an open read, and the controller is what holds it to the end of the response.
    /// </para>
    /// <para>
    /// Not cached, and could not usefully be: the value is a stream.
    /// </para>
    /// </remarks>
    /// <param name="naming">True for a download, which names a file; false for a render, which does not.</param>
    Task<OperationResult<MediaAssetRender>> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, CancellationToken cancellationToken);

    /// <summary>
    /// As the overload without <paramref name="rendition"/>, serving the named rendition in place of the stored
    /// bytes when the version has one (AF.5.6).
    /// </summary>
    /// <param name="rendition">The rendition wanted, or null for the version as it was stored.</param>
    /// <remarks>
    /// Which assets and versions can be opened, and by whom, is exactly as for the original. A rendition the
    /// version does not have is answered with the stored bytes, and the result says which it is.
    /// </remarks>
    Task<OperationResult<MediaAssetRender>> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, MediaRenditionPurpose? rendition, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one named version of a live asset for download (DAM-008).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any member may download, as for the current version. The caller owns disposing the render.
    /// </para>
    /// <para>
    /// <strong>Both identifiers are verified together</strong>, so one asset's route cannot serve another's version,
    /// and a number this asset has no version for is refused rather than quietly answered with the current one.
    /// </para>
    /// </remarks>
    Task<OperationResult<MediaAssetRender>> OpenVersionAsync(
        Guid mediaAssetId, int versionNumber, bool naming, CancellationToken cancellationToken);

    /// <summary>
    /// As the overload without <paramref name="rendition"/>, serving the named rendition in place of the stored
    /// bytes when the version has one (AF.5.6).
    /// </summary>
    /// <param name="rendition">The rendition wanted, or null for the version as it was stored.</param>
    /// <remarks>
    /// Which assets and versions can be opened, and by whom, is exactly as for the original. A rendition the
    /// version does not have is answered with the stored bytes, and the result says which it is.
    /// </remarks>
    Task<OperationResult<MediaAssetRender>> OpenVersionAsync(
        Guid mediaAssetId,
        int versionNumber,
        bool naming,
        MediaRenditionPurpose? rendition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records one use of a live asset (DAM-009).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contributor, the same role that may add an asset: recording where work went out is part of producing it.
    /// </para>
    /// <para>
    /// Takes an optional idempotency key, as the other DAM creates do. <strong>Without one, two identical calls write
    /// two rows</strong> — and that is deliberate rather than a gap: an asset genuinely can go out twice on one
    /// platform on one day, which is why the table carries no uniqueness over
    /// <c>(asset, platform, date)</c>. A client that wants a retry to be safe sends a key.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<MediaAssetUtilizationServiceModel>> LogUtilizationAsync(
        string userId,
        Guid mediaAssetId,
        LogMediaAssetUtilizationViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds a new version to a live asset from an upload (DAM-010).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contributor, the same role that may add an asset: replacing the bytes of your own work is part of producing it.
    /// Note this is Contributor where <em>removing</em> an asset needs Editor — adding a version takes nothing away,
    /// because every earlier version keeps its row, its object and its download route.
    /// </para>
    /// <para>
    /// Takes an optional idempotency key, as the create does. Worth sending: an upload is the request most likely to be
    /// retried after a dropped connection, and without a key a retry adds a second version of the same bytes rather
    /// than returning the first.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<MediaAssetVersionServiceModel>> AddVersionAsync(
        Guid mediaAssetId,
        MediaAssetVersionUpload upload,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Reads one page of one asset's utilization history, newest first (DAM-003).</summary>
    /// <remarks>
    /// Separate from the detail because this history grows without limit — a row per use, for as long as the
    /// creator keeps using the asset — where versions are a handful and travel with the asset itself.
    /// </remarks>
    Task<OperationResult<MediaAssetUtilizationPageServiceModel>> GetUtilizationAsync(
        Guid mediaAssetId, MediaAssetUtilizationViewModel model, CancellationToken cancellationToken);


    /// <summary>Creates an asset from an upload. Contributor or above.</summary>
    Task<IdempotentOutcome<MediaAssetServiceModel>> CreateFromUploadAsync(
        MediaAssetUpload upload, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Creates an asset from a staged generated image, keeping it. Contributor or above.</summary>
    Task<IdempotentOutcome<MediaAssetServiceModel>> CreateFromGeneratedImageAsync(
        MediaAssetFromGeneratedImage request, string? idempotencyKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetFacade"/>
internal sealed class MediaAssetFacade(
    IMediaAssetBusiness business,
    IValidator<MediaAssetSearchViewModel> searchValidator,
    IValidator<MediaAssetUtilizationViewModel> utilizationValidator,
    IClock clock,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IMediaAssetFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string UploadOperation = "media.asset.upload";

    private const string KeepOperation = "media.asset.keep";

    /// <inheritdoc cref="UploadOperation"/>
    private const string PatchOperation = "media.asset.patch";

    /// <inheritdoc cref="UploadOperation"/>
    private const string UtilizationOperation = "media.asset.utilization";

    /// <inheritdoc cref="UploadOperation"/>
    private const string AddVersionOperation = "media.asset.version";

    public async Task<OperationResult<MediaAssetSearchPageServiceModel>> SearchAsync(
        MediaAssetSearchViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await searchValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<MediaAssetSearchPageServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.AssetSearchInvalidRequest,
                "That search cannot be run as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The workspace comes from the resolved context and goes into the cursor's scope, so a cursor issued
        // for one workspace cannot resume in another even if a client sends it there (tenancy.md).
        if (!MediaAssetSearchQueryFactory.TryCreate(
            model, workspace.WorkspaceId, out var criteria, out var error))
        {
            return OperationResult<MediaAssetSearchPageServiceModel>.Failure(error!);
        }

        return OperationResult<MediaAssetSearchPageServiceModel>.Success(
            await business.SearchAsync(criteria!, cancellationToken));
    }

    public Task<OperationResult<MediaAssetDetailServiceModel>> GetDetailAsync(
        Guid mediaAssetId, MediaAssetDetailViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        return business.GetDetailAsync(
            mediaAssetId, model.IncludeDeleted ?? false, cancellationToken);
    }

    public Task<IdempotentOutcome<MediaAssetDetailServiceModel>> PatchMetadataAsync(
        string userId,
        Guid mediaAssetId,
        MediaAssetMetadataPatchViewModel patch,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);

        // Contributor, the role that may add an asset: correcting what was said about one is part of producing it
        // and changes no bytes.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Task.FromResult(RefusedPatch(new OperationError(
                MediaErrorCodes.AssetForbidden,
                "You do not have permission to change this workspace's library.",
                new Dictionary<string, string[]>())));
        }

        // A token that is not one this API could have issued is a bad request naming the field, not a 409: a
        // conflict says "somebody saved first", which would be a lie about a token that never existed. This is
        // what MediaConcurrencyToken.IsWellFormed is separate from Matches for.
        if (!MediaConcurrencyToken.IsWellFormed(patch.ExpectedConcurrencyToken))
        {
            return Task.FromResult(RefusedPatch(OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest,
                "That asset could not be changed as described.",
                [("expectedConcurrencyToken",
                    "Send the concurrencyToken from the read this edit was composed against.")])));
        }

        return idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                PatchOperation,
                idempotencyKey,
                // The asset and everything the patch asked for. Fingerprinting only the id and the token would
                // make one key serve two different edits: the second would be replayed as the first and silently
                // dropped, which is the opposite of what an idempotency key is for. The view model cannot be
                // serialized directly — see MediaAssetMetadataPatchViewModel.Fingerprint.
                Fingerprint: new { mediaAssetId, Patch = patch.Fingerprint() },
                KeyRequired: false),
            token => business.PatchMetadataAsync(
                mediaAssetId, patch, workspace.MembershipId, token),
            cancellationToken);
    }

    private static IdempotentOutcome<MediaAssetDetailServiceModel> RefusedPatch(OperationError error) =>
        new(OperationResult<MediaAssetDetailServiceModel>.Failure(error), Replayed: false);

    public Task<OperationResult<MediaAssetDeletionServiceModel>> SoftDeleteAsync(
        string userId,
        Guid mediaAssetId,
        DeleteMediaAssetViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Task.FromResult(OperationResult<MediaAssetDeletionServiceModel>.Failure(new OperationError(
                MediaErrorCodes.AssetForbidden,
                "You do not have permission to remove assets from this workspace's library.",
                new Dictionary<string, string[]>())));
        }

        var failures = new List<(string, string)>();

        // A person decided this, rather than a well-formed body reaching a deletion. Checked before the token so a
        // client that forgot the flag is told about the flag rather than about concurrency.
        if (model.Confirmed is not true)
        {
            failures.Add(("confirmed", "Send confirmed: true to remove this asset."));
        }

        if (!MediaConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            failures.Add((
                "expectedConcurrencyToken",
                "Send the concurrencyToken from the read this deletion was decided against."));
        }

        if (failures.Count > 0)
        {
            return Task.FromResult(OperationResult<MediaAssetDeletionServiceModel>.Failure(
                OperationError.Validation(
                    MediaErrorCodes.AssetInvalidRequest, "That asset could not be removed.", failures)));
        }

        return business.SoftDeleteAsync(
            mediaAssetId,
            model.ExpectedConcurrencyToken,
            userId,
            workspace.MembershipId,
            cancellationToken);
    }

    public Task<OperationResult<MediaAssetRender>> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, CancellationToken cancellationToken) =>
        business.OpenCurrentVersionAsync(mediaAssetId, naming, rendition: null, cancellationToken);

    public Task<OperationResult<MediaAssetRender>> OpenVersionAsync(
        Guid mediaAssetId, int versionNumber, bool naming, CancellationToken cancellationToken) =>
        business.OpenVersionAsync(mediaAssetId, versionNumber, naming, rendition: null, cancellationToken);

    public Task<OperationResult<MediaAssetRender>> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, MediaRenditionPurpose? rendition, CancellationToken cancellationToken) =>
        business.OpenCurrentVersionAsync(mediaAssetId, naming, rendition, cancellationToken);

    public Task<OperationResult<MediaAssetRender>> OpenVersionAsync(
        Guid mediaAssetId,
        int versionNumber,
        bool naming,
        MediaRenditionPurpose? rendition,
        CancellationToken cancellationToken) =>
        business.OpenVersionAsync(mediaAssetId, versionNumber, naming, rendition, cancellationToken);

    public Task<IdempotentOutcome<MediaAssetUtilizationServiceModel>> LogUtilizationAsync(
        string userId,
        Guid mediaAssetId,
        LogMediaAssetUtilizationViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Task.FromResult(RefusedUtilization(new OperationError(
                MediaErrorCodes.AssetForbidden,
                "You do not have permission to record use of this workspace's assets.",
                new Dictionary<string, string[]>())));
        }

        // The server's own date for the future bound, passed in so the rule stays a pure function it can be tested at
        // the boundary of.
        var failures = MediaAssetInputChecks
            .Utilization(model, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime))
            .ToList();

        if (failures.Count > 0)
        {
            return Task.FromResult(RefusedUtilization(OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest, "That use could not be recorded.", failures)));
        }

        return idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                UtilizationOperation,
                idempotencyKey,

                // The asset and everything recorded about the use, so one key cannot serve two different logs — the
                // mistake the patch path had to be corrected for.
                Fingerprint: new { mediaAssetId, model.PlatformKey, model.UtilizedOn, model.CampaignName, model.Notes },
                KeyRequired: false),
            token => business.LogUtilizationAsync(
                mediaAssetId, model, workspace.MembershipId, token),
            cancellationToken);
    }

    private static IdempotentOutcome<MediaAssetUtilizationServiceModel> RefusedUtilization(OperationError error) =>
        new(OperationResult<MediaAssetUtilizationServiceModel>.Failure(error), Replayed: false);

    public Task<IdempotentOutcome<MediaAssetVersionServiceModel>> AddVersionAsync(
        Guid mediaAssetId,
        MediaAssetVersionUpload upload,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Task.FromResult(RefusedVersion(new OperationError(
                MediaErrorCodes.AssetForbidden,
                "You do not have permission to add versions to this workspace's library.",
                new Dictionary<string, string[]>())));
        }

        return idempotency.ExecuteAsync(
            new IdempotentCommand(
                upload.UserId,
                workspace.WorkspaceId,
                AddVersionOperation,
                idempotencyKey,

                // The asset and the filename, not the bytes: a fingerprint has to be cheap, and hashing the file would
                // mean reading it twice before anything is stored. A creator who sends one key with two different
                // files has made a mistake the executor should catch rather than silently serve the first answer for —
                // which it does, because the filename almost always differs, and when it does not the two uploads were
                // indistinguishable to begin with.
                Fingerprint: new { mediaAssetId, upload.FileName },
                KeyRequired: false),
            token => business.AddVersionFromUploadAsync(
                mediaAssetId, upload, workspace.MembershipId, token),
            cancellationToken);
    }

    private static IdempotentOutcome<MediaAssetVersionServiceModel> RefusedVersion(OperationError error) =>
        new(OperationResult<MediaAssetVersionServiceModel>.Failure(error), Replayed: false);

    public async Task<OperationResult<MediaAssetUtilizationPageServiceModel>> GetUtilizationAsync(
        Guid mediaAssetId, MediaAssetUtilizationViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await utilizationValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<MediaAssetUtilizationPageServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.AssetSearchInvalidRequest,
                "That history cannot be read as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The workspace and the asset both go into the scope, so a cursor cannot be moved between assets or
        // between workspaces even if a client sends it somewhere else (tenancy.md).
        var scope = MediaAssetUtilizationScope.Build(workspace.WorkspaceId, mediaAssetId);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            return OperationResult<MediaAssetUtilizationPageServiceModel>.Failure(CursorRefused());
        }

        MediaAssetUtilizationPosition? position = null;

        if (cursor is not null && !MediaAssetUtilizationPosition.TryCreate(cursor, out position))
        {
            // Bound to this exact scope and still not a position — a tie-breaker that is not an id, or a sort
            // value that is not a date. Only reachable by editing one, and answered the same way.
            return OperationResult<MediaAssetUtilizationPageServiceModel>.Failure(CursorRefused());
        }

        var criteria = new MediaAssetUtilizationCriteria(
            mediaAssetId, scope, position, model.Limit, model.IncludeTotal ?? true);

        // A deleted asset's history is readable only the way its detail is, and this route has no flag of its
        // own: a creator looking at a tombstone is looking at the detail, not paging its usage log.
        return await business.GetUtilizationAsync(criteria, includeDeleted: false, cancellationToken);
    }

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            MediaErrorCodes.AssetCursorInvalidRequest,
            "This cursor was issued for a different workspace or asset. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);


    public Task<IdempotentOutcome<MediaAssetServiceModel>> CreateFromUploadAsync(
        MediaAssetUpload upload, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        if (Refusal(upload.Metadata, upload.RecipeLink) is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        return idempotency.ExecuteAsync(
            new IdempotentCommand(
                upload.UserId,
                workspace.WorkspaceId,
                UploadOperation,
                idempotencyKey,

                // The metadata and the lineage, not the bytes: a fingerprint has to be cheap, and a
                // creator who sends one key with two different files has made a mistake the executor
                // should catch rather than silently serve the first answer for.
                Fingerprint: new { upload.FileName, upload.Metadata, upload.RecipeLink },
                KeyRequired: false),
            token => business.CreateFromUploadAsync(upload, token),
            cancellationToken);
    }

    public Task<IdempotentOutcome<MediaAssetServiceModel>> CreateFromGeneratedImageAsync(
        MediaAssetFromGeneratedImage request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Refusal(request.Metadata, request.RecipeLink) is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        if (request.GeneratedImageId == Guid.Empty)
        {
            return Task.FromResult(Refused(OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest,
                "The asset could not be created.",
                [(nameof(request.GeneratedImageId), "A generated image id is required.")])));
        }

        return idempotency.ExecuteAsync(
            new IdempotentCommand(
                request.UserId,
                workspace.WorkspaceId,
                KeepOperation,
                idempotencyKey,
                Fingerprint: new { request.GeneratedImageId, request.Metadata, request.RecipeLink },
                KeyRequired: false),
            token => business.CreateFromGeneratedImageAsync(request, token),
            cancellationToken);
    }

    /// <summary>The role gate and the shape checks, which are the same whichever way the bytes arrived.</summary>
    private IdempotentOutcome<MediaAssetServiceModel>? Refusal(
        MediaAssetMetadataInput metadata, MediaAssetRecipeLinkInput? recipeLink)
    {
        // Contributor, the same role that may generate an image: adding finished work to the library is
        // part of producing it, and it spends storage rather than changing anything already published.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(new OperationError(
                MediaErrorCodes.AssetForbidden,
                "You do not have permission to add assets to this workspace's library.",
                new Dictionary<string, string[]>()));
        }

        var failures = MediaAssetInputChecks.Metadata(metadata)
            .Concat(MediaAssetInputChecks.RecipeLink(recipeLink))
            .ToList();

        return failures.Count == 0
            ? null
            : Refused(OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest, "The asset could not be created.", failures));
    }

    private static IdempotentOutcome<MediaAssetServiceModel> Refused(OperationError error) =>
        new(OperationResult<MediaAssetServiceModel>.Failure(error), Replayed: false);
}
