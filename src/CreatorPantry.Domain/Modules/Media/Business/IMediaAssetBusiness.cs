using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>
/// Creating a DAM asset from a validated upload or from a staged image the creator kept (DAM-001).
/// </summary>
public interface IMediaAssetBusiness
{
    /// <summary>Creates an asset from bytes a creator uploaded.</summary>
    Task<OperationResult<MediaAssetServiceModel>> CreateFromUploadAsync(
        MediaAssetUpload upload, CancellationToken cancellationToken);

    /// <summary>Creates an asset from a staged generated image, keeping it.</summary>
    Task<OperationResult<MediaAssetServiceModel>> CreateFromGeneratedImageAsync(
        MediaAssetFromGeneratedImage request, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetBusiness"/>
/// <remarks>
/// <para>
/// <strong>Both paths meet at one data layer call.</strong> What differs is where the bytes come from and
/// what has to be true about them before anything is written: an upload is a stranger's file and is
/// inspected and scanned here; a staged image was inspected and scanned when the worker staged it (12.7),
/// so its recorded media type and dimensions are facts this layer may rely on rather than re-derive.
/// </para>
/// <para>
/// <strong>The checksum is re-verified either way.</strong> For an upload it is computed by the store as
/// it writes; for a staged image the copy is checked against what the staged row recorded. A row that
/// described bytes nobody measured would be the thing "preserve actual media metadata" exists to prevent.
/// </para>
/// </remarks>
internal sealed class MediaAssetBusiness(
    IMediaAssetDataLayer assets,
    IPromptRecordFacade prompts,
    IGeneratedImageDataLayer stagedImages,
    IMalwareScanGateway scanner,
    ILogger<MediaAssetBusiness> logger) : IMediaAssetBusiness
{
    public async Task<OperationResult<MediaAssetServiceModel>> CreateFromUploadAsync(
        MediaAssetUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        using var buffered = new MemoryStream();
        await upload.Content.CopyToAsync(buffered, cancellationToken);

        if (buffered.Length > MediaPolicy.ImageMaxBytes)
        {
            return Error(MediaErrorCodes.AssetUnsupported, "That file is larger than this library accepts.");
        }

        var bytes = buffered.ToArray();
        var inspection = GeneratedImageInspector.Inspect(bytes);

        if (inspection.Outcome is not GeneratedImageInspectionOutcome.Accepted)
        {
            // Established from the bytes, never from the declared content type or the filename.
            return Error(
                MediaErrorCodes.AssetUnsupported,
                "That file is not an image this library can store, whatever its name or type says.");
        }

        buffered.Position = 0;

        if (await scanner.ScanAsync(buffered, cancellationToken) is not MalwareScanVerdict.Clean)
        {
            logger.LogWarning("A DAM upload was refused by the scanner.");

            return Error(MediaErrorCodes.AssetUnsupported, "That file could not be accepted.");
        }

        var failures = await ResolveAsync(upload.Metadata, upload.RecipeLink, cancellationToken);

        if (failures.Count > 0)
        {
            return Invalid(failures);
        }

        buffered.Position = 0;

        return await CreateAsync(
            new MediaAssetCreation(
                MediaAssetKind.Original,
                MediaAssetVersionSource.Upload,
                SourceGeneratedImageId: null,
                buffered,
                inspection.MediaType!,
                inspection.Width,
                inspection.Height,
                buffered.Length,

                // Null rather than empty: there is nothing to verify the copy against yet, because the
                // store is what computes a checksum as it writes. A staged image passes the one its own
                // staging recorded, and the data layer compares that.
                ContentChecksum: null,
                upload.FileName,
                upload.Metadata,
                upload.Metadata.WorkspaceTagIds ?? [],
                upload.RecipeLink,
                upload.Prompt,
                upload.UserId),
            cancellationToken);
    }

    public async Task<OperationResult<MediaAssetServiceModel>> CreateFromGeneratedImageAsync(
        MediaAssetFromGeneratedImage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The natural replay guard, and it needs no idempotency header: a staged image is kept exactly
        // once, so a second attempt hands back the asset the first one made.
        if (await assets.FindByGeneratedImageAsync(request.GeneratedImageId, cancellationToken) is { } existing)
        {
            return OperationResult<MediaAssetServiceModel>.Success(Map(existing, null, request.RecipeLink?.RecipeId));
        }

        var staged = await stagedImages.OpenForKeepAsync(request.GeneratedImageId, cancellationToken);

        if (staged.Outcome is StagedImageOpenOutcome.NotFound)
        {
            // One answer for an unknown id, a neighbour's, an image already rejected and one whose bytes
            // retention has removed, so none of them discloses the others (tenancy.md).
            return Error(MediaErrorCodes.AssetSourceNotFound, "That generated image could not be found.");
        }

        if (staged.Outcome is StagedImageOpenOutcome.StorageUnavailable)
        {
            return Error(
                MediaErrorCodes.AssetStorageUnavailable,
                "The image could not be read right now. Try again shortly.");
        }

        await using var download = staged.Download!;

        var failures = await ResolveAsync(request.Metadata, request.RecipeLink, cancellationToken);

        if (failures.Count > 0)
        {
            return Invalid(failures);
        }

        return await CreateAsync(
            new MediaAssetCreation(
                MediaAssetKind.AiGenerated,
                MediaAssetVersionSource.GeneratedImage,
                request.GeneratedImageId,
                download.Content,

                // Established when the worker staged it (12.7) from the bytes the provider returned, and
                // carried forward rather than re-derived: the copy is verified against the checksum below.
                download.MediaType,
                staged.Width,
                staged.Height,
                download.SizeBytes,
                download.ContentChecksum,
                OriginalFileName: null,
                request.Metadata,
                request.Metadata.WorkspaceTagIds ?? [],
                request.RecipeLink,
                request.Prompt,
                request.UserId),
            cancellationToken);
    }

    /// <summary>Resolves the request's references before anything is written, and collects every failure.</summary>
    private async Task<List<(string Field, string Error)>> ResolveAsync(
        MediaAssetMetadataInput metadata,
        MediaAssetRecipeLinkInput? recipeLink,
        CancellationToken cancellationToken)
    {
        var failures = new List<(string, string)>();
        var tags = metadata.WorkspaceTagIds ?? [];

        // Unknown ids are refused, never created here: an asset should not be able to invent vocabulary a
        // creator never agreed to, and the tag tables belong to whoever owns that decision.
        if (tags.Count > 0 && (await assets.UnknownTagsAsync(tags, cancellationToken)).Count > 0)
        {
            failures.Add((nameof(metadata.WorkspaceTagIds), "One of those tags is not in this workspace."));
        }

        if (recipeLink is { } link && !await assets.RecipeExistsAsync(link.RecipeId, cancellationToken))
        {
            failures.Add((nameof(link.RecipeId), "That recipe could not be found in this workspace."));
        }

        return failures;
    }

    private async Task<OperationResult<MediaAssetServiceModel>> CreateAsync(
        MediaAssetCreation creation, CancellationToken cancellationToken)
    {
        // The cross-module write lives here, where the decision does, and goes down as a delegate so the
        // data layer can run it inside its transaction without knowing another module's facade exists.
        // The same shape IAiOperationDataLayer.AcceptDraftAsync uses for the recipe module's create.
        var created = await assets.CreateAsync(
            creation,
            creation.Prompt is { } prompt
                ? (assetId, token) => prompts.SaveForAssetAsync(creation.UserId, prompt, assetId, token)
                : null,
            cancellationToken);

        return created.Outcome switch
        {
            MediaAssetCreateOutcome.Created or MediaAssetCreateOutcome.AlreadyCreated =>
                OperationResult<MediaAssetServiceModel>.Success(
                    Map(created.Asset!, created.PromptRecordId, creation.RecipeLink?.RecipeId)),

            MediaAssetCreateOutcome.PromptRefused =>
                OperationResult<MediaAssetServiceModel>.Failure(created.PromptError!),

            MediaAssetCreateOutcome.StorageUnavailable => Error(
                MediaErrorCodes.AssetStorageUnavailable,
                "The asset could not be stored right now. Try again shortly."),

            _ => Error(
                MediaErrorCodes.AssetNotCreated,
                "The asset could not be created. Nothing was saved."),
        };
    }

    private static MediaAssetServiceModel Map(
        Data.Entities.MediaAsset asset, Guid? promptRecordId, Guid? recipeId)
    {
        var version = asset.Versions.OrderByDescending(candidate => candidate.VersionNumber).First();

        return new MediaAssetServiceModel(
            asset.Id,
            asset.Title,
            asset.Kind,
            asset.CurrentVersionNumber,
            version.MediaType,
            version.Width,
            version.Height,
            version.SizeBytes,
            version.SourceGeneratedImageId,
            promptRecordId,
            recipeId,
            asset.CreatedAt);
    }

    private static OperationResult<MediaAssetServiceModel> Error(string code, string message) =>
        OperationResult<MediaAssetServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<MediaAssetServiceModel> Invalid(
        IEnumerable<(string Field, string Error)> failures) =>
        OperationResult<MediaAssetServiceModel>.Failure(
            OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest, "The asset could not be created.", failures));
}
