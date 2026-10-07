using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
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

    /// <summary>
    /// Applies a metadata patch to one asset and returns it as it now stands (DAM-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected failures: no live asset this workspace can see has that id; the merged metadata would be
    /// invalid; a tag is not this workspace's; or the quoted token is one the asset has moved past.
    /// </para>
    /// <para>
    /// A patch that asks for what is already there writes nothing and returns the asset unchanged, token included.
    /// </para>
    /// </remarks>
    Task<OperationResult<MediaAssetDetailServiceModel>> PatchMetadataAsync(
        Guid mediaAssetId,
        MediaAssetMetadataPatchViewModel patch,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes one asset and reports what still references it (DAM-005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected failures: no asset this workspace can see has that id, or the quoted token is one the asset has
    /// moved past. An asset that is already deleted is <em>not</em> a failure — the existing tombstone comes back
    /// and nothing is written.
    /// </para>
    /// <para>
    /// No link is touched, and no bytes are removed.
    /// </para>
    /// </remarks>
    Task<OperationResult<MediaAssetDeletionServiceModel>> SoftDeleteAsync(
        Guid mediaAssetId,
        string? expectedConcurrencyToken,
        string actorUserId,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a live asset's current version, for rendering (DAM-006) or for download (DAM-007).
    /// </summary>
    /// <remarks>
    /// Two expected failures and they say different things: nothing to serve, or the bytes could not be reached and
    /// retrying is the remedy. The caller owns disposing what comes back.
    /// </remarks>
    /// <param name="naming">True for a download, which names a file; false for a render, which does not.</param>
    Task<OperationResult<MediaAssetRender>> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one named version of a live asset for download (DAM-008).
    /// </summary>
    /// <remarks>
    /// The same two failures the current-version open has, and the same codes. A number this asset has no version
    /// for is the <em>not found</em> one — never a fallback to whatever version does exist.
    /// </remarks>
    Task<OperationResult<MediaAssetRender>> OpenVersionAsync(
        Guid mediaAssetId, int versionNumber, bool naming, CancellationToken cancellationToken);

    /// <summary>
    /// Records one use of a live asset (DAM-009).
    /// </summary>
    /// <remarks>
    /// One expected failure: no live asset this workspace can see has that id. Validation is the facade's, because it
    /// is shape rather than domain rule — what is here is the derivation and the write.
    /// </remarks>
    Task<OperationResult<MediaAssetUtilizationServiceModel>> LogUtilizationAsync(
        Guid mediaAssetId,
        LogMediaAssetUtilizationViewModel model,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds a new version to a live asset from a validated upload (DAM-010).
    /// </summary>
    /// <remarks>
    /// The bytes go through the same acceptance the asset create uses — bound, signature-inspected, scanned — so a
    /// file this library would refuse as a new asset is refused as a new version too.
    /// </remarks>
    Task<OperationResult<MediaAssetVersionServiceModel>> AddVersionFromUploadAsync(
        Guid mediaAssetId, MediaAssetVersionUpload upload, Guid actorMembershipId, CancellationToken cancellationToken);

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

    public async Task<OperationResult<MediaAssetDetailServiceModel>> PatchMetadataAsync(
        Guid mediaAssetId,
        MediaAssetMetadataPatchViewModel patch,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);

        // Tracked, with tags, tombstones excluded. An unknown id, another workspace's asset and a deleted one all
        // arrive as null and all answer the same 404 (tenancy.md, and the policy approved for 12.9c).
        var asset = await assets.FindLiveForUpdateAsync(mediaAssetId, cancellationToken);

        if (asset is null)
        {
            return OperationResult<MediaAssetDetailServiceModel>.Failure(NoSuchAsset());
        }

        // Before any validation work: a caller whose token is stale should be told that rather than being sent
        // round a loop fixing fields on a version of the asset they are no longer looking at.
        if (!MediaConcurrencyToken.Matches(patch.ExpectedConcurrencyToken, asset.RowVersion))
        {
            return OperationResult<MediaAssetDetailServiceModel>.Failure(StaleToken());
        }

        var merged = MediaAssetMetadataMerge.Apply(asset, patch);

        // The creation path's own checks, over the merged result rather than over the patch. A patch therefore
        // cannot leave an asset in a state a create would have refused, and clearing the title fails with "A
        // title is required" without that rule being written twice.
        var failures = MediaAssetInputChecks.Metadata(merged)
            .Concat(await ResolveAsync(merged, recipeLink: null, cancellationToken))
            .ToList();

        if (failures.Count > 0)
        {
            return OperationResult<MediaAssetDetailServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest, "That asset could not be changed as described.", failures));
        }

        // Nothing to write is not an error, and not a write either: the asset comes back as it stands with the
        // token the caller already holds still valid. See MediaAssetMetadataMerge.Changes.
        if (!MediaAssetMetadataMerge.Changes(asset, merged))
        {
            return await GetDetailAsync(mediaAssetId, includeDeleted: false, cancellationToken);
        }

        if (!await assets.UpdateMetadataAsync(asset, merged, actorMembershipId, cancellationToken))
        {
            return OperationResult<MediaAssetDetailServiceModel>.Failure(StaleToken());
        }

        // Re-read rather than composing the response from the entity just written: the detail carries counts,
        // versions and two modules' lineage that this patch never loaded, and a hand-built response would be a
        // second definition of the same shape waiting to drift from the read's.
        return await GetDetailAsync(mediaAssetId, includeDeleted: false, cancellationToken);
    }

    /// <summary>
    /// Somebody else saved first, or the caller quoted an older read. One answer for both.
    /// </summary>
    /// <remarks>
    /// The two are the same thing from the caller's side and the same thing to recover from — re-read, look at
    /// what changed, decide — so distinguishing them would publish a difference nobody could act on.
    /// </remarks>
    private static OperationError StaleToken() =>
        new(
            MediaErrorCodes.AssetStaleToken,
            "That asset changed since you last read it. Read it again before editing.",
            new Dictionary<string, string[]>());

    public async Task<OperationResult<MediaAssetDeletionServiceModel>> SoftDeleteAsync(
        Guid mediaAssetId,
        string? expectedConcurrencyToken,
        string actorUserId,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        // Tombstones included, so a repeat answers with the existing one rather than a 404: a client that cannot
        // tell "already done" from "never existed" cannot retry safely.
        var asset = await assets.FindForDeleteAsync(mediaAssetId, cancellationToken);

        if (asset is null)
        {
            return OperationResult<MediaAssetDeletionServiceModel>.Failure(NoSuchAsset());
        }

        // Before the already-deleted answer below, following RecipeBusiness.TransitionAsync and for the reason it
        // gives: a caller quoting a stale token has not seen what the asset looks like now, and telling them
        // "already deleted" would hide a collaborator's work from them.
        if (!MediaConcurrencyToken.Matches(expectedConcurrencyToken, asset.RowVersion))
        {
            return OperationResult<MediaAssetDeletionServiceModel>.Failure(StaleToken());
        }

        if (asset.DeletedAt is { } alreadyAt)
        {
            // A repeat, not a second deletion. Writing a new timestamp, a new actor or a second audit entry for
            // something that did not happen would put a lie in the records that have to be trustworthy.
            return OperationResult<MediaAssetDeletionServiceModel>.Success(
                await DeletionAsync(asset, alreadyAt, asset.DeletedByMembershipId!.Value, true, cancellationToken));
        }

        // Read before the write: afterwards is the same answer, but reading first means a failed write reports the
        // references it was about to leave stale rather than an empty impact.
        var references = await assets.FindReferencesAsync(mediaAssetId, cancellationToken);

        if (!await assets.SoftDeleteAsync(asset, actorUserId, actorMembershipId, cancellationToken))
        {
            return OperationResult<MediaAssetDeletionServiceModel>.Failure(StaleToken());
        }

        return OperationResult<MediaAssetDeletionServiceModel>.Success(
            await DeletionAsync(asset, asset.DeletedAt!.Value, actorMembershipId, false, cancellationToken,
                references));
    }

    /// <summary>Composes the deletion response, naming the recipes that still point at the asset.</summary>
    /// <remarks>
    /// The recipe titles come through <see cref="IRecipeFacade"/> because <c>Recipe</c> is that module's entity and
    /// no foreign key runs from an asset to a recipe — the same reason 12.9c resolves lineage that way. A recipe
    /// this workspace cannot name is counted and not listed.
    /// </remarks>
    private async Task<MediaAssetDeletionServiceModel> DeletionAsync(
        MediaAsset asset,
        DateTimeOffset deletedAt,
        Guid deletedBy,
        bool alreadyDeleted,
        CancellationToken cancellationToken,
        (IReadOnlyList<Guid> RecipeIds, int BrandProfileCount, int TestAttachmentCount)? known = null)
    {
        var references = known ?? await assets.FindReferencesAsync(asset.Id, cancellationToken);
        var titles = await recipes.ListTitlesAsync(references.RecipeIds, cancellationToken);

        return new MediaAssetDeletionServiceModel(
            asset.Id,
            asset.Title,
            deletedAt,
            deletedBy,
            alreadyDeleted,
            new MediaAssetAffectedContentServiceModel(
                references.RecipeIds.Count,
                titles,
                references.BrandProfileCount,
                references.TestAttachmentCount),
            MediaConcurrencyToken.From(asset.RowVersion));
    }

    public async Task<OperationResult<MediaAssetRender>> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, CancellationToken cancellationToken) =>
        Opened(await assets.OpenCurrentVersionAsync(mediaAssetId, naming, cancellationToken));

    public async Task<OperationResult<MediaAssetRender>> OpenVersionAsync(
        Guid mediaAssetId, int versionNumber, bool naming, CancellationToken cancellationToken) =>
        Opened(await assets.OpenVersionAsync(mediaAssetId, versionNumber, naming, cancellationToken));

    /// <summary>Turns an open outcome into the result a route answers from.</summary>
    /// <remarks>
    /// Shared so the two byte-serving seams cannot answer the same outcome differently — which would make one route
    /// a softer way in than the other.
    /// </remarks>
    private static OperationResult<MediaAssetRender> Opened(MediaAssetOpen opened)
    {
        return opened.Outcome switch
        {
            MediaAssetOpenOutcome.Opened => OperationResult<MediaAssetRender>.Success(opened.Render!),

            // The same 404 an unknown id gets, which is the point: an unknown asset, a neighbour's, a tombstone and
            // a missing version row are one answer (tenancy.md).
            MediaAssetOpenOutcome.NotFound =>
                OperationResult<MediaAssetRender>.Failure(NoSuchAsset()),

            // Not a 404: the asset is there, so telling a creator it is gone would be wrong, and retrying is the
            // remedy rather than going back to a list.
            MediaAssetOpenOutcome.StorageUnavailable => OperationResult<MediaAssetRender>.Failure(new OperationError(
                MediaErrorCodes.AssetStorageUnavailable,
                "That image could not be read just now. Try again.",
                new Dictionary<string, string[]>())),

            _ => throw new InvalidOperationException($"Unhandled open outcome '{opened.Outcome}'."),
        };
    }

    public async Task<OperationResult<MediaAssetUtilizationServiceModel>> LogUtilizationAsync(
        Guid mediaAssetId,
        LogMediaAssetUtilizationViewModel model,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var logged = await assets.LogUtilizationAsync(
            mediaAssetId,

            // Trimmed, so " instagram " and "instagram" are one platform rather than two in a creator's own history.
            // Non-null by the facade's validation, which runs before this.
            model.PlatformKey!.Trim(),
            model.UtilizedOn!.Value,
            Blank(model.CampaignName),
            Blank(model.Notes),
            actorMembershipId,
            cancellationToken);

        // Null covers an unknown id, another workspace's asset and a tombstone — one answer, so a caller cannot use
        // this route to learn that an asset exists where they cannot see it (tenancy.md).
        return logged is null
            ? OperationResult<MediaAssetUtilizationServiceModel>.Failure(NoSuchAsset())
            : OperationResult<MediaAssetUtilizationServiceModel>.Success(MapUse(logged));
    }

    /// <summary>An optional field that arrived as whitespace is absent, not a value.</summary>
    /// <remarks>
    /// Stored as null rather than as spaces so a history does not show a campaign whose name is three blanks, and so
    /// "has a campaign" is one question rather than two.
    /// </remarks>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<OperationResult<MediaAssetVersionServiceModel>> AddVersionFromUploadAsync(
        Guid mediaAssetId,
        MediaAssetVersionUpload upload,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        using var buffered = new MemoryStream();
        var accepted = await AcceptBytesAsync(upload.Content, buffered, cancellationToken);

        if (accepted.Error is { } byteError)
        {
            return OperationResult<MediaAssetVersionServiceModel>.Failure(byteError);
        }

        var inspection = accepted.Inspection!.Value;

        var added = await assets.AddVersionAsync(
            mediaAssetId,
            buffered,
            inspection.MediaType!,
            inspection.Width,
            inspection.Height,
            buffered.Length,
            upload.FileName,
            actorMembershipId,
            cancellationToken);

        return added.Outcome switch
        {
            MediaAssetVersionAddOutcome.Added =>
                OperationResult<MediaAssetVersionServiceModel>.Success(added.Version!),

            MediaAssetVersionAddOutcome.NotFound =>
                OperationResult<MediaAssetVersionServiceModel>.Failure(NoSuchAsset()),

            // Retry unchanged: the next read gives the next number. Distinct from the two 409s below because that is
            // the only thing a caller does differently.
            MediaAssetVersionAddOutcome.VersionTaken =>
                OperationResult<MediaAssetVersionServiceModel>.Failure(new OperationError(
                    MediaErrorCodes.AssetVersionTaken,
                    "Another upload took that version number. Try again.",
                    new Dictionary<string, string[]>())),

            MediaAssetVersionAddOutcome.StorageUnavailable =>
                OperationResult<MediaAssetVersionServiceModel>.Failure(new OperationError(
                    MediaErrorCodes.AssetStorageUnavailable,
                    "That version could not be stored just now. Try again.",
                    new Dictionary<string, string[]>())),

            MediaAssetVersionAddOutcome.NotCommitted =>
                OperationResult<MediaAssetVersionServiceModel>.Failure(new OperationError(
                    MediaErrorCodes.AssetNotCreated,
                    "That version could not be saved. Nothing was kept.",
                    new Dictionary<string, string[]>())),

            _ => throw new InvalidOperationException($"Unhandled version add outcome '{added.Outcome}'."),
        };
    }

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
        var accepted = await AcceptBytesAsync(upload.Content, buffered, cancellationToken);

        if (accepted.Error is { } byteError)
        {
            return OperationResult<MediaAssetServiceModel>.Failure(byteError);
        }

        var inspection = accepted.Inspection!.Value;
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

    /// <summary>
    /// Buffers an upload, bounds it, establishes what it actually is, and scans it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the asset create (DAM-001) and the new-version upload (DAM-010), so the two cannot disagree about
    /// what this library will accept — a format one took and the other refused would be a hole in whichever was
    /// stricter, and a limit enforced in only one of them would be no limit at all.
    /// </para>
    /// <para>
    /// <strong>Buffered first, because three things need the bytes:</strong> the size bound, the signature
    /// inspection, and the scanner. A stream read once could satisfy only the first of them.
    /// </para>
    /// <para>
    /// <strong>The type comes from the bytes.</strong> Neither the declared content type nor the filename is
    /// consulted — a file called <c>.jpg</c> carrying something else is refused on what it is, which is the whole
    /// point of inspecting a signature (media.md).
    /// </para>
    /// <para>
    /// On return the buffer is positioned at zero and is the caller's to write from.
    /// </para>
    /// </remarks>
    private async Task<(GeneratedImageInspection? Inspection, OperationError? Error)> AcceptBytesAsync(
        Stream content, MemoryStream buffered, CancellationToken cancellationToken)
    {
        await content.CopyToAsync(buffered, cancellationToken);

        if (buffered.Length > MediaPolicy.ImageMaxBytes)
        {
            return (null, Refusal("That file is larger than this library accepts."));
        }

        var inspection = GeneratedImageInspector.Inspect(buffered.ToArray());

        if (inspection.Outcome is not GeneratedImageInspectionOutcome.Accepted)
        {
            return (null, Refusal(
                "That file is not an image this library can store, whatever its name or type says."));
        }

        buffered.Position = 0;

        if (await scanner.ScanAsync(buffered, cancellationToken) is not MalwareScanVerdict.Clean)
        {
            // Without saying what was found: a scanner's verdict is not something to describe back to a caller who
            // may have sent the file deliberately.
            logger.LogWarning("A DAM upload was refused by the scanner.");

            return (null, Refusal("That file could not be accepted."));
        }

        buffered.Position = 0;

        return (inspection, null);
    }

    private static OperationError Refusal(string message) =>
        new(MediaErrorCodes.AssetUnsupported, message, new Dictionary<string, string[]>());

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

        // Both are client-supplied ids into shared reference vocabulary with Restrict foreign keys. Unchecked,
        // the write reaches SaveChanges and throws DbUpdateException, which nothing on this path catches — so a
        // creator naming a stale cuisine would get a 500 rather than a named field. Checked here, so the create
        // path and the patch path are both covered by one rule.
        if (metadata.CuisineId is { } cuisineId
            && !await assets.CuisineExistsAsync(cuisineId, cancellationToken))
        {
            failures.Add((nameof(metadata.CuisineId), "That cuisine could not be found."));
        }

        if (metadata.CourseId is { } courseId
            && !await assets.CourseExistsAsync(courseId, cancellationToken))
        {
            failures.Add((nameof(metadata.CourseId), "That course could not be found."));
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
