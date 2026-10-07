using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
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

    /// <summary>Changes part of one asset's metadata and returns it as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset to change. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">
    /// The fields to change, plus the asset's concurrency token. Carries no workspace, no owner, no audit field,
    /// and nothing about the bytes.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same edit returns the original response with
    /// `Idempotent-Replayed: true` rather than being answered as a conflict.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// This is a JSON Merge Patch, not a replacement. A field the body does not mention is left exactly as it is;
    /// a field sent with a value is set to it; a field sent as `null` is cleared. Omitting a field never blanks
    /// it. `tags` is the one field that replaces rather than merges — a submitted list becomes the asset's
    /// complete set, and `[]` or `null` removes them all. `title` may be changed but not cleared.
    ///
    /// **Nothing about the bytes can be reached from here.** No object key, version number, media type,
    /// dimensions or checksum: those are facts the server established by reading the bytes, and a new file is a
    /// new version rather than an edit. `kind` is absent too — it records where the bytes came from, which is
    /// lineage rather than classification, and relabelling an AI-generated asset as an upload would erase the one
    /// column that remembers a model was involved. The editorial classification a creator may correct is
    /// `cuisineId`, `courseId`, `channelKey`, `platformKey`, `styleKey`, `day` and `tags`.
    ///
    /// `expectedConcurrencyToken` is required and is not a field of the asset: it is the `concurrencyToken` from
    /// the read this edit was composed against. An edit quoting a token the asset has moved past is refused with
    /// `409 media.asset.stale.conflict` rather than overwriting whoever saved first; a token this API could never
    /// have issued is a `400` naming the field, because a conflict would be a lie about a token that never
    /// existed. **An edit that would change nothing writes nothing** — no `updatedAt`, no new token — so the
    /// caller's token stays valid and there is no way to "touch" an asset.
    ///
    /// A soft-deleted asset answers `404`, as does an unknown or inaccessible one: a patch is not a way to edit
    /// or restore something deleted. The response is the whole asset in the same shape the detail read returns,
    /// so an editor can rebind from it, carrying the refreshed token the next edit must quote.
    /// </remarks>
    [HttpPatch("{assetId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<MediaAssetDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Patch(
        string workspaceSlug,
        Guid assetId,
        MediaAssetMetadataPatchViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await assets.PatchMetadataAsync(
            userId, assetId, model, idempotencyKey, cancellationToken);

        // No location to contribute and no mapping to do: the facade already returns the ServiceModel, and the
        // asset is where it always was.
        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Removes one asset from the library without deleting its bytes.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset to remove. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">The confirmation and the asset's concurrency token. Carries nothing else.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Removing is not erasing.** Every version, tag, utilization record, prompt record and link stays exactly
    /// where it was, and **no bytes are deleted** — here or by any sweep this route schedules. What changes is that
    /// the asset gains a tombstone: it drops out of the library listing and out of the detail read, which answers
    /// `404` unless asked with `?includeDeleted=true`, and the render and download routes refuse it.
    ///
    /// **Links are deliberately left standing.** Cutting a recipe's photograph silently is the one thing this must
    /// not do, so recipe, brand-profile and test-attachment links all survive. The response instead reports what is
    /// now pointing at a tombstone: `affected.recipes` names them, `affected.brandProfileCount` and
    /// `affected.testAttachmentCount` count them, and `affected.any` is the one flag a client needs to decide
    /// whether to warn. Read the asset first if you want to warn *before* removing it.
    ///
    /// Requires the **Editor** role, where adding and editing an asset require Contributor: removing one takes
    /// finished work out of every collaborator's library rather than contributing to it.
    ///
    /// `confirmed` must be `true` — a deletion should be a step a creator took, not somewhere a well-formed body
    /// arrives. `expectedConcurrencyToken` is required and is checked **before** the already-removed answer: a
    /// caller quoting a stale token has not seen what the asset looks like now, and telling them "already removed"
    /// would hide a collaborator's work. The consequence is that retrying after a lost response needs a fresh read.
    ///
    /// **Removing an already-removed asset succeeds and changes nothing** — the original timestamp and actor come
    /// back with `alreadyDeleted: true`, and no second audit entry is written. There is no restore route yet.
    /// An unknown, inaccessible or other-workspace asset answers `404 media.asset.not_found`.
    /// </remarks>
    [HttpDelete("{assetId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<MediaAssetDeletionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(
        string workspaceSlug,
        Guid assetId,
        DeleteMediaAssetViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await assets.SoftDeleteAsync(userId, assetId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Renders the asset's current version inline.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset to render. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may render: an asset a creator can see in the library is one they can look at. Suitable as an
    /// `&lt;img src&gt;` — the bytes come back `inline` with the media type recorded when they were stored, never
    /// one a client declared.
    ///
    /// **The bytes are proxied, never redirected.** There is no signed URL, no storage path and no object key in
    /// the response or its headers, so nothing here can be shared, bookmarked past a session, or replayed against
    /// storage directly (media.md).
    ///
    /// **Caching:** `private, no-cache` with a strong `ETag` built from the version's content checksum. A version's
    /// bytes are write-once so the tag identifies them exactly, and `If-None-Match` answers `304` with no body —
    /// cheap for a grid that renders the same asset repeatedly. It revalidates every time rather than carrying a
    /// `max-age` because **this route serves whichever version is current**: a stale window would mean a creator who
    /// just added a version still seeing the old image with no way to tell why. `private` keeps it out of any shared
    /// proxy (gateway.md).
    ///
    /// **No ranges.** `Accept-Ranges: none`, and a `Range` header is ignored rather than honoured: these are whole
    /// images a browser renders in one pass, and offering ranges would be a contract to keep for no benefit.
    ///
    /// An unknown asset, another workspace's, a soft-deleted one, and one whose current version row is missing all
    /// answer `404 media.asset.not_found` — a caller cannot tell which, so none of them discloses that an asset
    /// exists elsewhere. A version whose bytes cannot be reached answers `503 media.asset.unavailable` instead,
    /// because the asset is there and retrying is the remedy rather than going back to a list.
    /// </remarks>
    [HttpGet("{assetId:guid}/content")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Content(
        string workspaceSlug, Guid assetId, CancellationToken cancellationToken) =>
        Send(await assets.OpenCurrentVersionAsync(assetId, naming: false, cancellationToken));

    /// <summary>Downloads the asset's current version as a named file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset to download. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The same bytes `/content` renders, with two headers different: `attachment` rather than `inline`, and a
    /// filename. Any member may download — being able to look at an image and being able to save it are not
    /// meaningfully different privileges, and every brand source download takes the same view.
    ///
    /// **The filename is `{title-slug}-v{n}.{ext}`**, so "Soda bread hero" at version 2 stored as JPEG downloads as
    /// `soda-bread-hero-v2.jpg`. Deterministic: the same asset, version and media type always give the same name.
    /// Safe by construction: the title is reduced to lower-case ASCII letters, digits and single hyphens, so there is
    /// nothing a title could carry that becomes a path, a traversal or a second header. A title that folds away
    /// entirely falls back to `image`.
    ///
    /// **The extension comes from the stored media type, never from the uploaded filename** — that name is the
    /// creator's own text, is optional, and may claim `.jpg` over bytes that are a PNG. A media type with no known
    /// extension yields a name with none rather than a guessed one, because a missing extension is recoverable where
    /// a wrong one misleads whatever opens the file. **The `-v{n}` is what stops two downloads colliding:** saving
    /// version 1 and version 2 without it gives two identical names and the second silently becomes "… (1)".
    ///
    /// **Only the current version.** Choosing a historical one is 12.9h; this route serves what the asset points at.
    /// Caching, conditional requests, ranges and the failure shapes are all exactly as `/content` describes them —
    /// the two actions share one sender, so none of that can change for one and not the other. No storage path,
    /// signed URL or object key appears anywhere in the response.
    /// </remarks>
    [HttpGet("{assetId:guid}/download")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Download(
        string workspaceSlug, Guid assetId, CancellationToken cancellationToken) =>
        Send(await assets.OpenCurrentVersionAsync(assetId, naming: true, cancellationToken));

    /// <summary>Downloads one named version of the asset as a file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset the version must belong to. Constrained to a Guid.</param>
    /// <param name="versionNumber">
    /// The version to download, from 1. Must be a version of **this** asset; a number belonging to another asset is
    /// not found rather than served.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The history counterpart of `/download`, which serves whichever version the asset currently points at. Named
    /// by **version number**, which is what the detail read publishes — a version's own row id is deliberately never
    /// published, so the number is the only identifier a client has.
    ///
    /// **Both identifiers are checked together.** The lookup matches `(assetId, versionNumber)` as one predicate, so
    /// asking this asset's route for a number that belongs to a different asset finds nothing — it cannot serve the
    /// other asset's bytes. The workspace filter sits on top of that.
    ///
    /// **It never falls back.** A number this asset has no version for answers `404 media.asset.not_found`, not its
    /// current version: serving bytes a caller did not ask for would be worse than refusing, because they would have
    /// no way to tell. `0` and negative numbers answer the same `404` — the schema makes them unrepresentable, so
    /// there is nothing to disclose.
    ///
    /// The filename names the version it served, so `soda-bread-hero-v1.jpg` and `soda-bread-hero-v2.jpg` are
    /// distinct files. Everything else — the extension from the stored media type, the `attachment` disposition, the
    /// `private, no-cache` policy with its strong `ETag`, `Accept-Ranges: none`, `nosniff`, and the `404`/`503` split
    /// — is exactly what `/download` describes, because all three byte routes share one sender.
    ///
    /// A soft-deleted asset answers `404` here too: removing an asset takes its history out of reach, not just its
    /// latest bytes. No storage path, signed URL or object key appears anywhere in the response.
    /// </remarks>
    [HttpGet("{assetId:guid}/versions/{versionNumber:int}/download")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> DownloadVersion(
        string workspaceSlug, Guid assetId, int versionNumber, CancellationToken cancellationToken) =>
        Send(await assets.OpenVersionAsync(assetId, versionNumber, naming: true, cancellationToken));

    /// <summary>
    /// The one place that turns an asset's version into a response.
    /// </summary>
    /// <remarks>
    /// Rendering, downloading and downloading a named version differ by two headers, so they share everything else
    /// rather than drifting: a change to the caching, the entity tag, the range policy or the sniffing protection
    /// that reached only one of them would be a hole in the others. The same argument the staged-image pair makes
    /// (12.8).
    ///
    /// The disposition follows <see cref="MediaAssetRender.FileName"/> rather than a flag of its own, so a response
    /// cannot claim to be an attachment with no name or a render with one — the two could otherwise disagree.
    /// </remarks>
    private IActionResult Send(OperationResult<MediaAssetRender> result)
    {
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var render = result.Value!;

        // First, before anything that could throw. The facade hands back an *open* read, so from here to the end of
        // the request something must own releasing it — and registering it covers the 304 path too, so neither
        // branch has a dispose of its own to forget.
        Response.RegisterForDisposeAsync(render);

        // Strong, and legitimately so: a version's bytes are write-once, so the checksum identifies this
        // representation for as long as it exists. A new version changes it, which is what a route serving "the
        // current version" needs.
        var entityTag = new EntityTagHeaderValue($"\"{render.ContentChecksum}\"");

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.ETag = entityTag.ToString();

        // Stated rather than left to inference, so a client does not probe for range support.
        Response.Headers.AcceptRanges = "none";

        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } candidates
            && candidates.Any(candidate => candidate.Compare(entityTag, useStrongComparison: true)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Private creator material: nothing may be sniffed into another type. EdgeHardening already puts
        // `default-src 'none'` on every response from this host, which is stricter than anything this route would
        // add, so no Content-Security-Policy is set here.
        Response.Headers.XContentTypeOptions = "nosniff";

        // ASCII letters, digits, single hyphens and one dot by construction, so there is nothing to quote or encode.
        // Set here rather than through FileStreamResult's FileDownloadName so that one piece of code decides the name
        // and the header it lands in. A render carries no filename at all.
        Response.Headers.ContentDisposition = render.FileName is { } fileName
            ? new ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString()
            : "inline";

        // The store's own size, so a client can show progress. Safe to state because a version write refuses to
        // commit a row whose size storage disagrees with.
        Response.ContentLength = render.SizeBytes;

        // `enableRangeProcessing` is left off and no EntityTag is set on the result: this contract offers no ranges,
        // and the conditional request is decided above, so exactly one place compares a tag.
        return new FileStreamResult(render.Content, render.MediaType);
    }

    /// <summary>Records that the asset was used.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset that was used. Constrained to a Guid.</param>
    /// <param name="model">Where it went out, when, and anything worth recording about it.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same log returns the original response with
    /// `Idempotent-Replayed: true`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above — the same role that may add an asset, because recording where work went out is part of
    /// producing it. `GET` on this same path reads the history back, cursor-paged.
    ///
    /// **`utilizedOn` is required, and that is what makes the derived day unambiguous.** `utilizedDay` follows from
    /// the date alone — a calendar date has exactly one day of the week in every zone — so there is no instant to
    /// convert and no zone that could make it wrong. A client cannot send the day: one that could would be able to
    /// send one that disagrees with its own date. `createdAt` is when the log was written, which is a different fact
    /// from when the asset was used, and the row keeps both.
    ///
    /// A date more than a day past the server's own is refused as `400`: not "after today", so a creator east of UTC
    /// logging this afternoon is not refused for being on tomorrow's date — no inhabited offset exceeds +14 hours.
    /// **There is no lower bound**; recording where a photograph was used last year is a creator entering their own
    /// history. `platformKey` is required and opaque, validated for length and not against a catalogue.
    /// `campaignName` and `notes` are optional, and whitespace in either is stored as absent rather than as blanks.
    ///
    /// **Without an `Idempotency-Key`, two identical calls record two uses** — deliberately, because an asset can
    /// genuinely go out twice on one platform on one day, which is why no uniqueness is imposed over
    /// `(asset, platform, date)`. Send a key if a retry should be safe.
    ///
    /// An unknown asset, another workspace's, and a soft-deleted one all answer `404 media.asset.not_found`: there is
    /// nothing to record a use against, and the three are indistinguishable so none discloses that an asset exists
    /// elsewhere.
    /// </remarks>
    [HttpPost("{assetId:guid}/utilization")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<MediaAssetUtilizationServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> LogUtilization(
        string workspaceSlug,
        Guid assetId,
        LogMediaAssetUtilizationViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await assets.LogUtilizationAsync(
            userId, assetId, model, idempotencyKey, cancellationToken);

        // 201 with no Location: there is no single-utilization read to point at, and the collection this belongs to
        // is the history GET on this same path — which a client already knows the address of.
        return this.IdempotentResult(outcome, created => StatusCode(StatusCodes.Status201Created, created));
    }

    /// <summary>Adds a new version to the asset from an uploaded file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="assetId">The asset to add a version to. Constrained to a Guid.</param>
    /// <param name="form">The file. Carries nothing else.</param>
    /// <param name="idempotencyKey">
    /// Optional, and worth sending: an upload is the request most likely to be retried after a dropped connection, and
    /// without a key a retry adds a second version of the same bytes rather than returning the first.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Bytes only.** Changing what the asset *says* about itself is `PATCH /dam-assets/{assetId}`: a patch is JSON
    /// with a concurrency token and merge semantics, an upload is multipart, and form fields cannot express
    /// absent-versus-clear. Alt text in particular is left alone rather than cleared — it is the creator's own words,
    /// and deleting them because a file changed is not this route's decision to make.
    ///
    /// The file is accepted on **what its bytes are**, not what they are called: bounded, signature-inspected and
    /// scanned by the same acceptance a new asset goes through, so a file refused as an asset is refused as a version.
    /// The stored media type, dimensions, size and checksum all describe the bytes something measured.
    ///
    /// **Nothing is overwritten.** A version's object key embeds its number, so a new version is always a new object,
    /// and the store refuses to overwrite in any case. Every earlier version keeps its row, its object and its
    /// `/versions/{n}/download` — which is why this needs only Contributor where *removing* an asset needs Editor.
    /// The new version becomes current, so `/content` and `/download` serve it from here on.
    ///
    /// **Concurrent uploads cannot share a number.** Three things see to that: the store is create-only, the
    /// `(asset, version)` pair is unique, and the asset's row version guards the counter. A caller that loses the race
    /// gets `409 media.asset.version_taken.conflict` and should **retry unchanged** — the next attempt reads the next
    /// number. Its upload is discarded and, importantly, the winner's object is left exactly where it is.
    ///
    /// An unknown asset, another workspace's, and a soft-deleted one all answer `404 media.asset.not_found`: a removed
    /// asset does not gain versions. Storage failures answer `503`, and a commit that fails answers
    /// `409 media.asset.conflict` with the object already removed — nothing partial is ever left behind.
    /// </remarks>
    [HttpPost("{assetId:guid}/versions")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [RequestSizeLimit(MediaPolicy.AssetUploadRequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MediaPolicy.AssetUploadRequestMaxBytes)]
    [ProducesResponseType<MediaAssetVersionServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> AddVersion(
        string workspaceSlug,
        Guid assetId,
        [FromForm] AddDamAssetVersionForm form,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (form.File is null || form.File.Length == 0)
        {
            return this.ProblemFor(OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest,
                "That version could not be added.",
                [("file", "A file is required.")]));
        }

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        await using var content = form.File.OpenReadStream();

        var outcome = await assets.AddVersionAsync(
            assetId,
            new MediaAssetVersionUpload(content, form.File.FileName, userId),
            idempotencyKey,
            cancellationToken);

        // 201 with no Location: there is a route for the version's bytes, but the resource created here is a version
        // of an asset the caller already knows the address of, and the response carries it in full.
        return this.IdempotentResult(outcome, created => StatusCode(StatusCodes.Status201Created, created));
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
/// <summary>The multipart body of a new-version upload (DAM-010).</summary>
/// <remarks>
/// One field, deliberately. Metadata belongs to <c>PATCH /dam-assets/{assetId}</c>, which can express
/// absent-versus-clear in a way form fields cannot — see <c>MediaAssetVersionUpload</c>.
/// </remarks>
public sealed class AddDamAssetVersionForm
{
    [FromForm(Name = "file")]
    public IFormFile? File { get; set; }
}

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
