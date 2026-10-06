using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

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
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IMediaAssetFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string UploadOperation = "media.asset.upload";

    private const string KeepOperation = "media.asset.keep";

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
