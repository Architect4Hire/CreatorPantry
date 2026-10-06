using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
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

    /// <summary>
    /// Reads one asset of the resolved workspace in full, or reports that it has none with that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One expected failure, and only one: no asset this workspace can see has that id. There is no separate
    /// "belongs to another workspace" branch, because the query filter means this layer never sees a neighbour's
    /// row — which is what makes the two answers identical rather than merely matched (tenancy.md).
    /// </para>
    /// <para>
    /// A soft-deleted asset is that same failure unless <paramref name="includeDeleted"/> says otherwise.
    /// </para>
    /// </remarks>
    Task<OperationResult<MediaAssetDetailServiceModel>> GetDetailAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken);

    /// <summary>Reads one page of one asset's utilization history, newest first.</summary>
    /// <remarks>
    /// Checks the asset is visible first, so a history read cannot become a way to learn that a neighbour's asset
    /// exists by comparing an empty page against a 404. A soft-deleted asset's history is readable only the way
    /// its detail is.
    /// </remarks>
    Task<OperationResult<MediaAssetUtilizationPageServiceModel>> GetUtilizationAsync(
        MediaAssetUtilizationCriteria criteria, bool includeDeleted, CancellationToken cancellationToken);

    /// <summary>One page of the filtered library (DAM-002).</summary>
    Task<MediaAssetSearchPageServiceModel> SearchAsync(
        MediaAssetSearchCriteria criteria, CancellationToken cancellationToken);

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
    IRecipeFacade recipes,
    IGeneratedImageDataLayer stagedImages,
    IMalwareScanGateway scanner,
    ILogger<MediaAssetBusiness> logger) : IMediaAssetBusiness
{

    public async Task<OperationResult<MediaAssetDetailServiceModel>> GetDetailAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var bundle = await assets.FindDetailAsync(mediaAssetId, includeDeleted, cancellationToken);

        if (bundle is null)
        {
            return OperationResult<MediaAssetDetailServiceModel>.Failure(NoSuchAsset());
        }

        // The two lineage reads run together: neither depends on the other, and a detail panel waits for both.
        // Each is its own module's facade because neither row is one this module may query — PromptRecord is
        // Content's and carries the foreign key, and Recipe has none pointing here at all (backend.md).
        var recipeIds = bundle.RecipeLinks.Select(link => link.RecipeId).Distinct().ToList();

        var lineage = prompts.ListForAssetAsync(mediaAssetId, cancellationToken);
        var titles = recipes.ListTitlesAsync(recipeIds, cancellationToken);

        await Task.WhenAll(lineage, titles);

        var titleById = (await titles).ToDictionary(recipe => recipe.Id, recipe => recipe.Title);

        return OperationResult<MediaAssetDetailServiceModel>.Success(
            Map(bundle, await lineage, titleById));
    }

    public async Task<OperationResult<MediaAssetUtilizationPageServiceModel>> GetUtilizationAsync(
        MediaAssetUtilizationCriteria criteria, bool includeDeleted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // Visibility first. Without it an unknown asset and a neighbour's would both answer an empty page, which
        // reads as "never used" — and a caller could then tell a real asset from a fictional one by nothing at
        // all. 404 for both is the answer tenancy.md asks for.
        //
        // An EXISTS rather than the detail read: a history page has no use for the asset's versions, tags and
        // links, and paying for three extra statements per page to learn one boolean is the kind of cost that
        // only shows up once a creator has a long history.
        if (!await assets.IsAssetVisibleAsync(criteria.MediaAssetId, includeDeleted, cancellationToken))
        {
            return OperationResult<MediaAssetUtilizationPageServiceModel>.Failure(NoSuchAsset());
        }

        var (rows, hasMore, total) = await assets.ListUtilizationAsync(criteria, cancellationToken);
        var page = PageBuilder.Build(rows, hasMore, criteria.Scope, MapUse);

        return OperationResult<MediaAssetUtilizationPageServiceModel>.Success(
            new MediaAssetUtilizationPageServiceModel(page.Items, page.NextCursor, total));
    }

    /// <summary>
    /// An unknown asset, a neighbour's asset, and a tombstone the caller did not ask for: one answer.
    /// </summary>
    /// <remarks>
    /// Written once so the three cannot drift apart. A distinguishable message for any of them would disclose
    /// that the other workspace's asset exists (tenancy.md).
    /// </remarks>
    private static OperationError NoSuchAsset() =>
        new(MediaErrorCodes.AssetNotFound, "No such asset.", new Dictionary<string, string[]>());

    public async Task<MediaAssetSearchPageServiceModel> SearchAsync(
        MediaAssetSearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore, total) = await assets.SearchAsync(criteria, cancellationToken);
        var page = PageBuilder.Build(rows, hasMore, criteria.Scope, Map);

        return new MediaAssetSearchPageServiceModel(page.Items, page.NextCursor, total);
    }

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
            return Error(MediaErrorCodes.AssetNotFound, "That generated image could not be found.");
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

    /// <summary>One search row as a page reports it. No bytes, no key, no address.</summary>

    /// <summary>
    /// Composes one detail from what Media found and what the other two modules could name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A link whose recipe cannot be named is dropped, not named with a placeholder.</strong> Inventing
    /// "Untitled" would be this layer asserting something about a row it was refused. Unreachable as the schema
    /// stands — the composite foreign key to <c>Recipe</c> makes a link to an unnameable recipe impossible — so
    /// this is a guard rather than a path, and the agreement between the list and the count is asserted by
    /// <c>MediaAssetDetailEndpointTests</c> rather than assumed here.
    /// </para>
    /// <para>
    /// <see cref="MediaAssetDetailServiceModel.CurrentVersion"/> is the version matching
    /// <c>CurrentVersionNumber</c>, picked from the list already read rather than queried again. Null is reachable
    /// only for an asset whose current version is missing, which is modelled honestly instead of defaulted —
    /// listing the asset with no media facts is the more useful failure than hiding it.
    /// </para>
    /// </remarks>
    private static MediaAssetDetailServiceModel Map(
        MediaAssetDetailBundle bundle,
        IReadOnlyList<AssetPromptServiceModel> prompts,
        IReadOnlyDictionary<Guid, string> recipeTitles)
    {
        var asset = bundle.Asset;
        var versions = bundle.Versions.Select(MapVersion).ToList();

        return new MediaAssetDetailServiceModel(
            asset.Id,
            asset.Title,
            asset.Description,
            asset.AltText,
            asset.Kind,
            asset.ChannelKey,
            asset.PlatformKey,
            asset.Day,
            asset.StyleKey,
            asset.CuisineId,
            asset.CourseId,
            asset.RightsHolder,
            asset.AttributionText,
            [.. bundle.Tags.Select(tag => new MediaAssetTagServiceModel(tag.Id, tag.Name))],
            versions.FirstOrDefault(version => version.VersionNumber == asset.CurrentVersionNumber),
            versions,
            asset.VersionCount,
            asset.UtilizationCount,
            asset.RecipeLinkCount,
            [.. bundle.RecipeLinks
                .Where(link => recipeTitles.ContainsKey(link.RecipeId))
                .Select(link => new MediaAssetRecipeLinkServiceModel(
                    link.RecipeId, recipeTitles[link.RecipeId], link.Role, link.Caption))],
            prompts,
            asset.DeletedAt,
            asset.DeletedByMembershipId,
            asset.CreatedAt,
            asset.UpdatedAt,
            MediaConcurrencyToken.From(asset.RowVersion));
    }

    private static MediaAssetVersionServiceModel MapVersion(MediaAssetVersionRecord version) =>
        new(
            version.VersionNumber,
            version.MediaType,
            version.Width,
            version.Height,
            version.SizeBytes,
            version.ContentChecksum,
            version.OriginalFileName,
            version.Source,
            version.SourceGeneratedImageId,
            version.CreatedAt);

    private static MediaAssetUtilizationServiceModel MapUse(MediaAssetUtilizationRecord use) =>
        new(
            use.Id,
            use.PlatformKey,
            use.UtilizedOn,
            use.UtilizedDay,
            use.CampaignName,
            use.Notes,
            use.CreatedAt);

    private static MediaAssetSummaryServiceModel Map(MediaAssetSearchRecord row) =>
        new(
            row.Id,
            row.Title,
            row.Description,
            row.Kind,
            row.AltText,
            row.ChannelKey,
            row.PlatformKey,
            row.Day,
            row.StyleKey,
            row.CuisineId,
            row.CourseId,
            row.CurrentVersionNumber,
            row.MediaType,
            row.Width,
            row.Height,
            row.SizeBytes,
            row.CreatedAt,
            row.UpdatedAt);

    private static OperationResult<MediaAssetServiceModel> Error(string code, string message) =>
        OperationResult<MediaAssetServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<MediaAssetServiceModel> Invalid(
        IEnumerable<(string Field, string Error)> failures) =>
        OperationResult<MediaAssetServiceModel>.Failure(
            OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest, "The asset could not be created.", failures));
}
