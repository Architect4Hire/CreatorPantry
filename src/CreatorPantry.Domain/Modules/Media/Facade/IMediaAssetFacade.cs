using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
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
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IMediaAssetFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string UploadOperation = "media.asset.upload";

    private const string KeepOperation = "media.asset.keep";

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
