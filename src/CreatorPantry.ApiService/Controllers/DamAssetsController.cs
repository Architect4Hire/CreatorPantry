using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// Adding finished creative to the workspace's library (DAM-001).
/// </summary>
/// <remarks>
/// <para>
/// Two routes because there are two sources of bytes and they are validated differently: an upload is a
/// stranger's file and is inspected and scanned, while a staged image was both when the worker staged it.
/// They are not two kinds of asset — both commit through one transaction.
/// </para>
/// <para>
/// <strong>Neither response carries an address.</strong> An asset's bytes are read by id through 12.9f
/// and 12.9g; no URL for a DAM object is issued to anyone (media.md).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/dam-assets")]
public sealed class DamAssetsController(IMediaAssetFacade assets) : ControllerBase
{

    /// <summary>Lists the workspace's library, filtered and paged.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The filters, cursor and page size. Carries no workspace.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Newest first unless `sort=Title`. Cursor-paged: follow `nextCursor` until it is
    /// null. A cursor is bound to the workspace, ordering and filters it was issued for, so changing a
    /// filter means starting again without one — that answers `400 media.asset_search.cursor_invalid_request`
    /// rather than quietly resuming in a different set.
    ///
    /// `limit` is clamped rather than refused, so a client cannot fail a read by asking for too much.
    /// Filters combine with AND, except that repeated `tag`, `cuisine` and `course` values match an asset
    /// carrying any one of them. `createdFrom` is inclusive and `createdBefore` exclusive, so adjacent
    /// ranges neither overlap nor gap. Any filter that nothing matches is an empty page, not an error; a
    /// malformed one answers `400 media.asset_search.invalid_request` naming every field it could not read.
    ///
    /// **Soft-deleted assets are never listed**, whatever the filters say. Each item carries the current
    /// version's media type and dimensions so a grid can lay out without fetching every asset — and never
    /// the bytes, a checksum, an object key or any address. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<MediaAssetSearchPageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Search(
        string workspaceSlug,
        [FromQuery] MediaAssetSearchViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await assets.SearchAsync(query, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // A library page is a creator's private index of their own work: it must not sit in a shared proxy
        // or a browser cache after they sign out (gateway.md).
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Reads one asset in full: its metadata, its versions, its lineage and its counts.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset to read, resolved inside the caller's own workspace.</param>
    /// <param name="query">Whether a soft-deleted asset counts as found. Carries no workspace.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. The response carries the creator's own metadata, the current version's facts, the
    /// whole version history newest first, the tags named rather than as ids, and the recipes and prompts the
    /// asset is linked to. **Utilization is counted here and paged separately**, because it gains a row every
    /// time the asset is used and grows without limit; versions travel with the asset because there are a handful.
    ///
    /// `recipeLinkCount` agrees with `recipeLinks`: a link pointing at a recipe this workspace cannot name is
    /// refused by the database, so there is none to drop. Prompt lineage names a prompt and never quotes one —
    /// the words are read through the prompt's own route.
    ///
    /// **A soft-deleted asset answers `404` unless `includeDeleted=true`.** Asked for explicitly, it returns with
    /// `deletedAt` and `deletedByMembershipId` set, which is how a creator sees what they deleted and when. The
    /// image and download routes refuse a deleted asset either way.
    ///
    /// An unknown id, another workspace's asset and an unasked-for tombstone are one answer —
    /// `404 media.asset.not_found` — so the reply never discloses that an asset exists elsewhere. Nothing here is
    /// or becomes an address: no object key, no URL, no bytes. `concurrencyToken` is opaque and is what a metadata
    /// patch must quote. The response is `no-store`.
    /// </remarks>
    [HttpGet("{assetId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<MediaAssetDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Detail(
        string workspaceSlug,
        Guid assetId,
        [FromQuery] MediaAssetDetailViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await assets.GetDetailAsync(assetId, query, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // A creator's own asset, with its lineage: never in a shared proxy, and never in a browser cache after
        // they sign out (gateway.md).
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Reads one page of an asset's utilization history, newest first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset whose history to read, resolved inside the caller's own workspace.</param>
    /// <param name="query">The cursor and page size. Carries no workspace and no filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Cursor-paged: follow `nextCursor` until it is null. A cursor is bound to the workspace
    /// and the asset it was issued for, so sending one to a different asset answers
    /// `400 media.asset_search.cursor_invalid_request` rather than quietly paging the wrong history.
    ///
    /// Ordered by the date the asset was used, newest first, with the row id breaking the tie three uses on one
    /// day would otherwise leave undefined. `utilizedOn` is a calendar date in the workspace's own zone rather
    /// than an instant, and `utilizedDay` is the weekday derived when the use was logged — not re-derived here,
    /// which would use this server's calendar instead of the workspace's.
    ///
    /// `limit` is clamped rather than refused. An asset that has never been used is an empty page, not an error.
    /// A soft-deleted asset answers `404`, as does an unknown or inaccessible one — this route has no
    /// `includeDeleted` of its own, because a creator looking at a tombstone is reading its detail rather than
    /// paging its usage log. The response is `no-store`.
    /// </remarks>
    [HttpGet("{assetId:guid}/utilization")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<MediaAssetUtilizationPageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Utilization(
        string workspaceSlug,
        Guid assetId,
        [FromQuery] MediaAssetUtilizationViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await assets.GetUtilizationAsync(assetId, query, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Adds an uploaded image to the library as a new asset.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="form">The file and its metadata. Carries no workspace, owner or object identity.</param>
    /// <param name="idempotencyKey">
    /// Optional, and worth sending. Without it a client whose upload committed but whose response was lost
    /// retries and creates a <em>second</em> asset — there is no natural key to prevent that, because a
    /// creator may legitimately upload the same photograph twice as two assets.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. The media type is established from the file's bytes, never from the declared
    /// content type or the filename, and the file is scanned before anything stores it — a file whose bytes
    /// are not a supported image answers `422 media.asset.unprocessable` whatever it was called. The
    /// creator's filename is kept for display only and never becomes part of a key or a header.
    /// `201 Created` carries the asset; storage that could not be reached answers
    /// `503 media.asset.unavailable` with nothing written.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MediaPolicy.AssetUploadRequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MediaPolicy.AssetUploadRequestMaxBytes)]
    [ProducesResponseType<MediaAssetServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Upload(
        string workspaceSlug,
        [FromForm] CreateDamAssetForm form,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.File is null)
        {
            return this.ProblemFor(Domain.Managers.Results.OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest,
                "The asset could not be created.",
                [("file", "A file is required.")]));
        }

        // The form reader has already buffered the part, so this stream is seekable: it is read to
        // inspect, again to scan and again to store.
        await using var content = form.File.OpenReadStream();

        var outcome = await assets.CreateFromUploadAsync(
            new MediaAssetUpload(
                content,
                form.File.FileName,
                form.ToMetadata(),
                form.ToRecipeLink(),
                Prompt: null,
                User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value),
            idempotencyKey,
            cancellationToken);

        return Respond(outcome);
    }

    /// <summary>Keeps a staged generated image as a library asset.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The image to keep, its metadata, and the prompt that produced it.</param>
    /// <param name="idempotencyKey">Optional, and largely belt-and-braces here — see the remarks.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. The bytes are copied into the library and the staged image becomes `Kept`,
    /// which is what lets retention collect the staging copy: nothing is removed until a committed asset
    /// owns one.
    ///
    /// **Naturally idempotent, with or without a key.** An image is kept exactly once, so a repeat returns
    /// the asset the first call made rather than creating a second or failing. An unknown image, another
    /// workspace's, and one already rejected or expired all answer `404 media.asset.not_found`.
    ///
    /// A prompt sent with the request is written **inside the same transaction** as the asset, so the two
    /// commit together or not at all — a prompt record cannot be deleted afterwards, so it must never
    /// commit beside an asset that did not. A refused prompt takes the asset down with it and answers with
    /// the prompt's own error.
    /// </remarks>
    [HttpPost("from-generated-image")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<MediaAssetServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> KeepGeneratedImage(
        string workspaceSlug,
        [FromBody] KeepGeneratedImageViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var outcome = await assets.CreateFromGeneratedImageAsync(
            new MediaAssetFromGeneratedImage(
                model.GeneratedImageId ?? Guid.Empty,
                model.Metadata ?? new MediaAssetMetadataInput(),
                model.RecipeLink,
                model.Prompt,
                User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value),
            idempotencyKey,
            cancellationToken);

        return Respond(outcome);
    }

    /// <summary>
    /// One shape for both routes: the created asset, or the refusal that stopped it.
    /// </summary>
    /// <remarks>
    /// A replay answers 201 with the first call's asset rather than 200, because the client's request did
    /// result in exactly this asset existing — which is what the key promised it would mean.
    /// </remarks>
    private IActionResult Respond(IdempotentOutcome<MediaAssetServiceModel> outcome) =>
        outcome.Result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, outcome.Result.Value)
            : this.ProblemFor(outcome.Result.Error!);
}

/// <summary>The multipart body of an upload. Every field is optional here and validated in the domain.</summary>
/// <remarks>
/// Bound loosely on purpose, exactly as <c>UploadBrandSourceDocumentForm</c> is: a missing or malformed
/// form field should answer with this feature's own validation error rather than with the model binder's,
/// which cannot say which of a creator's fields was the problem.
/// </remarks>
public sealed class CreateDamAssetForm
{
    [FromForm(Name = "file")]
    public IFormFile? File { get; set; }

    [FromForm(Name = "title")]
    public string? Title { get; set; }

    [FromForm(Name = "description")]
    public string? Description { get; set; }

    [FromForm(Name = "altText")]
    public string? AltText { get; set; }

    [FromForm(Name = "channelKey")]
    public string? ChannelKey { get; set; }

    [FromForm(Name = "platformKey")]
    public string? PlatformKey { get; set; }

    [FromForm(Name = "day")]
    public DayOfWeek? Day { get; set; }

    [FromForm(Name = "styleKey")]
    public string? StyleKey { get; set; }

    [FromForm(Name = "cuisineId")]
    public Guid? CuisineId { get; set; }

    [FromForm(Name = "courseId")]
    public Guid? CourseId { get; set; }

    [FromForm(Name = "rightsHolder")]
    public string? RightsHolder { get; set; }

    [FromForm(Name = "attributionText")]
    public string? AttributionText { get; set; }

    [FromForm(Name = "tagIds")]
    public List<Guid>? TagIds { get; set; }

    [FromForm(Name = "recipeId")]
    public Guid? RecipeId { get; set; }

    [FromForm(Name = "caption")]
    public string? Caption { get; set; }

    internal MediaAssetMetadataInput ToMetadata() => new()
    {
        Title = Title,
        Description = Description,
        AltText = AltText,
        ChannelKey = ChannelKey,
        PlatformKey = PlatformKey,
        Day = Day,
        StyleKey = StyleKey,
        CuisineId = CuisineId,
        CourseId = CourseId,
        RightsHolder = RightsHolder,
        AttributionText = AttributionText,
        WorkspaceTagIds = TagIds,
    };

    internal MediaAssetRecipeLinkInput? ToRecipeLink() =>
        RecipeId is { } recipeId ? new MediaAssetRecipeLinkInput(recipeId, Caption) : null;
}

/// <summary>The body of a keep request.</summary>
/// <remarks>
/// <strong>No asset id, no object key and no media metadata.</strong> The asset is being created, the key
/// is the server's to generate, and the media facts come from the staged image's own row — a request that
/// could state them could state them wrongly.
/// </remarks>
public sealed record KeepGeneratedImageViewModel
{
    public Guid? GeneratedImageId { get; init; }

    public MediaAssetMetadataInput? Metadata { get; init; }

    public MediaAssetRecipeLinkInput? RecipeLink { get; init; }

    /// <summary>
    /// The prompt that produced the image, saved inside the same transaction as the asset.
    /// </summary>
    /// <remarks>
    /// It carries no asset id of its own: the asset does not exist when the request is written, and the
    /// server fills it in from inside the transaction that commits both.
    /// </remarks>
    public PromptRecordSaveInput? Prompt { get; init; }
}
