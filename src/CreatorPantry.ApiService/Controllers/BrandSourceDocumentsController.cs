using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Net.Http.Headers;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-source-documents")]
public sealed class BrandSourceDocumentsController(
    IBrandSourceDocumentFacade documents, IBrandSourceExtractionReviewFacade extractions) : ControllerBase
{
    /// <summary>Lists the workspace's brand source documents, filtered and paged.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The filters, cursor and page size. Carries no workspace.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Newest edit first, and that is the only ordering. Cursor-paged: follow `nextCursor`
    /// until it is null. A cursor is bound to the workspace and filters it was issued for, so changing a
    /// filter means starting again without one. `limit` is clamped rather than refused. `status` is `Active`
    /// unless `Archived` is asked for; removed documents are never listed. Filters combine with AND, except
    /// that repeated `tag` values match a document carrying any one of them. A filter naming a channel or tag
    /// nothing carries is an empty page, not an error; a malformed filter or a cursor from another filter set
    /// answers `400 brand.source.invalid_request`.
    ///
    /// Each item carries the document's description, its current version's metadata and `extraction.state`:
    /// `NotExtracted` until a first attempt, then how the latest attempt ended. It never carries the file,
    /// extracted text, a checksum or a storage location. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        string workspaceSlug,
        [FromQuery] BrandSourceDocumentListViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await documents.ListAsync(query, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Uploads a file as a new brand source document.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="form">
    /// The file and the creator's description of it. `title`, `documentType`, `purpose` and `file` are
    /// required. Carries no workspace, owner, media type or storage field.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. A repeat of the same key with the same file and description returns the original response with
    /// `Idempotent-Replayed: true` and stores nothing new; the same key with anything different answers
    /// `422 idempotency.key_reused`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. Accepts PDF, Word (`.docx`, without macros), Markdown, plain text, HTML, PNG, JPEG and
    /// WebP, up to 20 MB (5 MB for the three text formats). What the file is comes from its bytes: the
    /// part's declared content type is ignored, and a file whose contents are not what its extension claims
    /// answers `422 brand.source.file.unsupported.unprocessable`. A file that starts like an accepted format
    /// but is damaged answers `422 brand.source.file.corrupt.unprocessable`, one over its format's limit
    /// `413 brand.source.file.payload_too_large`, and one the malware scan refuses
    /// `422 brand.source.file.rejected.unprocessable`. When the scan or private storage cannot be reached the
    /// answer is `503` (`brand.source.scan.unavailable`, `brand.source.storage.unavailable`) and nothing is
    /// recorded; the same request with the same key may be sent again.
    ///
    /// The response is the document's metadata at version 1, with `mediaType` as established from the bytes.
    /// It never carries a storage location, and no text has been extracted from the file yet.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(BrandPolicy.SourceUploadRequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = BrandPolicy.SourceUploadRequestMaxBytes)]
    [ProducesResponseType<BrandSourceDocumentServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Upload(
        string workspaceSlug,
        [FromForm] UploadBrandSourceDocumentForm form,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        // The form reader has already buffered the part, so this stream is seekable: it is read to inspect,
        // again to scan and again to store.
        await using var content = form.File?.OpenReadStream();

        var outcome = await documents.UploadAsync(
            userId,
            new UploadBrandSourceDocumentViewModel
            {
                Title = form.Title,
                DocumentType = form.DocumentType,
                Purpose = form.Purpose,
                ChannelKey = form.ChannelKey,
                Audience = form.Audience,
                Tags = form.Tags,
            },
            content is null ? null : new BrandSourceUploadFile(content, form.File!.FileName),
            idempotencyKey,
            cancellationToken);

        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/brand-source-documents/{created.Id}", created));
    }

    /// <summary>Reads one brand source document's metadata.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">
    /// The document to read. Constrained to a Guid, so a malformed id answers 404 at routing — the same status
    /// as an unknown document and as another workspace's.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Carries the document's description, its tags, its current version's metadata,
    /// `extraction.state` and the `concurrencyToken` a replacement or an archive has to quote. It adds three
    /// things a list row does not: that token, the current version's `contentChecksum`, and `archivedAt`.
    /// It **never** carries the file, its extracted text, an object key, a container or a URL — the bytes are
    /// reached through the download route below, which streams them. An **archived** document reads normally;
    /// archiving is a shelf, not a deletion. An unknown id, another workspace's document and one this
    /// workspace has removed all answer `404 brand.source.not_found`, deliberately indistinguishable. The
    /// response is `no-store`.
    /// </remarks>
    [HttpGet("{documentId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandSourceDocumentDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var result = await documents.GetAsync(documentId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Downloads one version of a brand source document, as it was uploaded.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document the version belongs to. Constrained to a Guid.</param>
    /// <param name="versionNumber">
    /// The version to download, as the document's metadata numbers it. Constrained to an int, so a
    /// non-numeric segment answers 404 at routing.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. The body is the original file, byte for byte, streamed rather than buffered.
    /// `Content-Type` is the media type **established from the file's bytes at upload**, never the one the
    /// client declared, and `Content-Disposition: attachment` names it `{title-slug}-v{n}.{ext}` where the
    /// extension also comes from that media type — so a file uploaded as `hero.jpeg` downloads as `.jpg`,
    /// because one version has one name. The name is ASCII `a-z0-9` and hyphens by construction and carries no
    /// id, workspace or storage location; the creator's own filename is in the metadata above, for display,
    /// and is never put in a header.
    /// `X-Content-Type-Options: nosniff` and `Cache-Control: no-store` are set. A version is immutable, so the
    /// response carries a strong `ETag` — the stored content checksum — and `If-None-Match` answers 304.
    /// An unknown document, another workspace's, a removed one, and a version number this document does not
    /// have all answer `404 brand.source.not_found`, so a version number cannot be used to count a document's
    /// history. Metadata that exists whose bytes cannot be read answers `503
    /// brand.source.storage.unavailable` rather than 404: the document is there, and retrying is the remedy.
    /// No range requests, and no URL is ever issued for the object.
    /// </remarks>
    [HttpGet("{documentId:guid}/versions/{versionNumber:int}/content")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetVersionContent(
        string workspaceSlug,
        Guid documentId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        var result = await documents.OpenVersionAsync(documentId, versionNumber, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var download = result.Value!;

        // First, before anything that could throw. The facade hands back an *open* object, so from here to
        // the end of the request something must own releasing it — and every line below, however unlikely,
        // is a line that could leave it open forever if the response never got built.
        //
        // The lease, not the stream. FileStreamResult disposes the stream it is given, which for today's
        // store is the same object — but the store's contract is that disposing the lease is what releases a
        // read, and a provider that held anything else open would otherwise leak it once per download. It is
        // registered on both the 200 and the 304 path for the same reason, so neither has a dispose of its
        // own to forget.
        Response.RegisterForDisposeAsync(download);

        // Strong, and legitimately so: a version is immutable, so the checksum of its bytes identifies this
        // representation for good. Built here rather than in the domain because an entity tag is an HTTP
        // concern and the checksum is the fact underneath it.
        var entityTag = new EntityTagHeaderValue($"\"{download.ContentChecksum}\"");

        Response.Headers.CacheControl = "no-store";
        Response.Headers.ETag = entityTag.ToString();

        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } candidates
            && candidates.Any(candidate => candidate.Compare(entityTag, useStrongComparison: true)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Private creator material: nothing may be sniffed into another type.
        Response.Headers.XContentTypeOptions = "nosniff";

        // No Content-Security-Policy is set here: `EdgeHardening.ApiContentSecurityPolicy` already puts
        // `default-src 'none'` on every response from this host, which is stricter than the `sandbox` this
        // route would otherwise add for an uploaded HTML file. Setting one here would replace the stronger
        // policy with a weaker one.

        // ASCII a-z, 0-9, hyphens and one dot by construction, so there is nothing to quote or encode — the
        // same guarantee the recipe exports rely on. Set here rather than through FileStreamResult's
        // FileDownloadName so that one piece of code decides the name and the header it lands in.
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = download.FileName,
        }.ToString();

        // The stored size, so a client can show progress on a 20 MB file rather than watch an unbounded
        // stream. Safe to state because the upload refuses to commit a row whose size storage disagrees with
        // (BrandSourceStoreOutcome.ContentChanged), so the row's size is the object's size.
        Response.ContentLength = download.SizeBytes;

        // `enableRangeProcessing` is left off and no EntityTag is set on the result: this contract does not
        // offer ranges, and the conditional request is decided above, so there is exactly one place that
        // compares an entity tag.
        return new FileStreamResult(download.Content, download.MediaType);
    }

    /// <summary>Replaces a brand source document's file with a new version.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">
    /// The document to replace. Constrained to a Guid, so a malformed id answers 404 at routing — the same
    /// status as an unknown document and as another workspace's.
    /// </param>
    /// <param name="form">
    /// The new file and the `concurrencyToken` from the read this replacement was composed against. There are
    /// no metadata fields: a replacement replaces the file, never the creator's description of it.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. A repeat of the same key for the same document, token and bytes returns the original response
    /// with `Idempotent-Replayed: true` and stores nothing new; the same key with anything different answers
    /// `422 idempotency.key_reused`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. The file is accepted on exactly the terms an upload is — the same formats, the same
    /// limits, established from the bytes rather than the declared type, and the same four refusals
    /// (`unsupported`, `corrupt`, `payload_too_large`, `rejected`) plus the same two `503`s when the scanner
    /// or private storage cannot be reached, after which nothing is recorded and the same key may be resent.
    ///
    /// **Nothing is overwritten.** The new version is numbered one past the document's current version, gets
    /// its own private object, and leaves every earlier version's row, bytes and download untouched —
    /// `/versions/1/content` still serves version 1 afterwards. Text extracted from the old version stays
    /// attached to it; the new version reads `extraction.state: NotExtracted` until something extracts it.
    /// A style-guide version that cited the old upload still cites that exact version, because the citation
    /// pins a version rather than a document.
    ///
    /// `expectedConcurrencyToken` is required and guards the document, which is what the new version's number
    /// is read from: a replacement composed against a picture that has since moved answers
    /// `409 brand.source.conflict` and writes nothing. An **archived** document answers
    /// `409 brand.source.archived.conflict` — restore it first. An unknown id, another workspace's document
    /// and one this workspace has removed all answer `404 brand.source.not_found`, deliberately
    /// indistinguishable.
    ///
    /// The response is the document's metadata at its new version. It never carries a storage location.
    /// </remarks>
    [HttpPost("{documentId:guid}/versions")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(BrandPolicy.SourceUploadRequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = BrandPolicy.SourceUploadRequestMaxBytes)]
    [ProducesResponseType<BrandSourceDocumentServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Replace(
        string workspaceSlug,
        Guid documentId,
        [FromForm] ReplaceBrandSourceDocumentForm form,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        // Seekable, like the upload's: the form reader has already buffered the part, and it is read to
        // inspect, again to scan and again to store.
        await using var content = form.File?.OpenReadStream();

        var outcome = await documents.ReplaceAsync(
            userId,
            documentId,
            new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = form.ExpectedConcurrencyToken },
            content is null ? null : new BrandSourceUploadFile(content, form.File!.FileName),
            idempotencyKey,
            cancellationToken);

        // The document, not the version: the body is the document at its new version, and the detail route is
        // where a client reads it back. There is no metadata route for a single version — the bytes are at
        // `/versions/{n}/content`, which is a representation rather than the thing that was created.
        return this.IdempotentResult(outcome, replaced =>
            Created($"/api/v1/workspaces/{workspaceSlug}/brand-source-documents/{replaced.Id}", replaced));
    }

    /// <summary>Lists this workspace's removed brand source documents, newest removal first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The cursor and page size. Carries no workspace and no filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Owner only, and the only route that answers for a removed document.** Everywhere else a tombstone is
    /// a `404` — the read, the download, the replacement and the three Editor lifecycle commands alike — so
    /// this bin is how a removal is undone at all, and it is the one place a removed document's
    /// `concurrencyToken` is published. Cursor-paged like the library: follow `nextCursor` until it is null.
    ///
    /// A row carries who removed it and when, which a library row has no field for, and drops what a bin does
    /// not need: tags, channel, audience and extraction state. It never carries the file, a checksum or a
    /// storage location. The response is `no-store`.
    /// </remarks>
    [HttpGet("removed")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceOwner)]
    [ProducesResponseType<CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> ListRemoved(
        string workspaceSlug,
        [FromQuery] RemovedBrandSourceDocumentListViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await documents.ListRemovedAsync(query, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Reports what still points at one brand source document.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document to report on. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. The confirmation step for a removal: it says how many versions the document has,
    /// how many bytes they hold, and what still points at them — so a creator sees what a removal does
    /// **not** take with it.
    ///
    /// Nothing here is a warning about data loss, because a removal causes none. Every version row and every
    /// stored object survives it, and a style-guide version that cited this document still resolves
    /// afterwards, because the citation pins an exact version and that version is never deleted.
    ///
    /// `isHeld` is the fact a future retention job has to honour: true when at least one **approved** guide
    /// version cites one of this document's versions. A draft guide version is listed in `holds` but does not
    /// make the document held, because a draft may still be superseded. An **archived** document reports
    /// normally; a removed one answers `404 brand.source.not_found`, like every other read of a tombstone.
    /// The response is `no-store`.
    /// </remarks>
    [HttpGet("{documentId:guid}/usage")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandSourceDocumentUsageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Usage(
        string workspaceSlug,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var result = await documents.UsageAsync(documentId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Shelves one brand source document.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document to shelve. Constrained to a Guid.</param>
    /// <param name="model">The document's concurrency token. Carries nothing else.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Archiving is not deleting.** Every version, tag and stored object stays exactly where it was, and the
    /// document is still readable by id and still downloadable. What changes is that it drops out of the
    /// default library listing (`GET .../brand-source-documents` returns it only for `?status=Archived`) and
    /// stops accepting a replacement: `POST .../versions` answers `409 brand.source.archived.conflict` until
    /// it is brought back.
    ///
    /// Editor or above, because archiving takes a document out of every collaborator's library rather than
    /// contributing to one. `expectedConcurrencyToken` is required: archiving a document someone else is
    /// replacing should tell the archiver it moved under them. Archiving an already-archived document
    /// succeeds and changes nothing — no audit entry, no new token — so the command is safe to retry and
    /// takes no idempotency key.
    /// </remarks>
    [HttpPost("{documentId:guid}/archive")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandSourceDocumentDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<IActionResult> Archive(
        string workspaceSlug,
        Guid documentId,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken) =>
        TransitionAsync(documentId, BrandSourceDocumentLifecycleCommand.Archive, model, cancellationToken);

    /// <summary>Brings one brand source document back off the shelf.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document to bring back. Constrained to a Guid.</param>
    /// <param name="model">The document's concurrency token. Carries nothing else.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The mirror of the archive command, at the same Editor bar and with the same shape. The document
    /// returns to `Active` and accepts replacements again. `archivedAt` is **kept**: it records when the
    /// document was last shelved, not whether it is shelved now. Unarchiving a document that is not archived
    /// succeeds and changes nothing.
    ///
    /// Named `unarchive` rather than `restore` deliberately — `restore` already means bringing a *removed*
    /// document back, which is a different command at a different role, and one word for two operations
    /// would be a trap in a client and in this codebase alike.
    /// </remarks>
    [HttpPost("{documentId:guid}/unarchive")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandSourceDocumentDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<IActionResult> Unarchive(
        string workspaceSlug,
        Guid documentId,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken) =>
        TransitionAsync(documentId, BrandSourceDocumentLifecycleCommand.Unarchive, model, cancellationToken);

    /// <summary>Soft-deletes one brand source document.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document to remove. Constrained to a Guid.</param>
    /// <param name="model">The document's concurrency token. Carries nothing else.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Nothing is deleted.** Not the document row, not one version row, not one stored object, not one
    /// extraction. A style-guide version that cited this document still resolves its citation afterwards,
    /// because the citation pins an exact version and that version survives — which is why a removal is
    /// allowed even when an approved guide cites it. Read `GET .../usage` first to see what stays behind;
    /// physical purge is a retention job's decision and no retention period has been set.
    ///
    /// Editor or above, from `Active` or from `Archived`. `expectedConcurrencyToken` is required.
    ///
    /// The answer is `204 No Content` on purpose: the document this command produced is invisible to every
    /// read, and answering with its metadata would make this a second place a tombstone is published. The
    /// one place that happens is `GET .../brand-source-documents/removed`, which is Owner-only — so after
    /// this call an Editor can no longer see the document at all, and **repeating the removal answers
    /// `404`** rather than succeeding a second time. An Owner brings it back with
    /// `POST .../{id}/restore`.
    /// </remarks>
    [HttpPost("{documentId:guid}/remove")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<IActionResult> Remove(
        string workspaceSlug,
        Guid documentId,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken) =>
        TransitionAsync(documentId, BrandSourceDocumentLifecycleCommand.Remove, model, cancellationToken);

    /// <summary>Brings one removed brand source document back.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The removed document to bring back. Constrained to a Guid.</param>
    /// <param name="model">
    /// The document's concurrency token, which for a removed document comes from
    /// `GET .../brand-source-documents/removed` — the only route that publishes one.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Owner only**, and the only command that can address a removed document: every other route answers
    /// `404` for one, so this is the single way back.
    ///
    /// The document returns to **`Archived`, not `Active`** — a document somebody deliberately removed should
    /// not reappear in every collaborator's picker and in brand context on its way back, so an Editor
    /// unarchives it afterwards as a separate, deliberate step. `removedAt` and `removedBy` are cleared,
    /// because `CK_BrandSourceDocuments_Removed_Consistent` refuses them on a row that is not removed; the
    /// audit log is therefore the only lasting record that the document was ever removed.
    ///
    /// Restoring a document that is not removed succeeds and changes nothing, which is what makes this
    /// command safe to retry.
    /// </remarks>
    [HttpPost("{documentId:guid}/restore")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceOwner)]
    [ProducesResponseType<BrandSourceDocumentDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<IActionResult> Restore(
        string workspaceSlug,
        Guid documentId,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken) =>
        TransitionAsync(documentId, BrandSourceDocumentLifecycleCommand.Restore, model, cancellationToken);

    /// <summary>
    /// The one body the four lifecycle commands share: call the facade, map the one outcome that differs.
    /// </summary>
    /// <remarks>
    /// A null value means the document was removed and there is nothing to show — see the remove action for
    /// why a tombstone is not published here.
    /// </remarks>
    private async Task<IActionResult> TransitionAsync(
        Guid documentId,
        BrandSourceDocumentLifecycleCommand command,
        BrandSourceDocumentLifecycleViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await documents.TransitionAsync(userId, documentId, command, model, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        return result.Value is { } document ? Ok(document) : NoContent();
    }

    /// <summary>Reads the text extracted from one version of a brand source document.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document the version belongs to. Constrained to a Guid.</param>
    /// <param name="versionNumber">
    /// The version whose text to read, as the document's metadata numbers it. Any version reads, so a corrected
    /// or superseded version's text stays available.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. The body is the version's **current** extracted text — its latest attempt or
    /// correction — as JSON, not a download: this is the text a creator reviews and edits, and the file it came
    /// from is reached through `/content` above.
    ///
    /// `text` is present exactly when `state` is `Succeeded`. `Unsupported` carries `reason` instead and no text,
    /// which is what a scanned PDF or an uploaded image answers — this API performs no text recognition, and says
    /// so rather than returning an empty success. `Failed` likewise. A version nothing has read yet answers
    /// `200` with `state: NotExtracted` rather than a 404: the version exists and downloads, and a creator
    /// watching a queued extraction needs to see "not yet".
    ///
    /// `id` identifies the exact artifact and is what a correction sends back as `expectedExtractionId`;
    /// `ordinal` counts how many times this version's text has been written, and `origin` says whether a parser
    /// or a person wrote it. The response carries no object key, no container, no URL and no membership id.
    ///
    /// An unknown id, another workspace's document, one this workspace has removed, and a version number this
    /// document does not have all answer `404 brand.source.not_found`, deliberately indistinguishable. An
    /// **archived** document reads normally. An artifact whose stored text cannot be read answers
    /// `503 brand.source.storage.unavailable` rather than 404, because the document is there and retrying is the
    /// remedy. The response is `no-store`.
    ///
    /// Whatever this returns is untrusted content, however it was produced: a creator's correction is no more an
    /// instruction than a parser's output is.
    /// </remarks>
    [HttpGet("{documentId:guid}/versions/{versionNumber:int}/extraction")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandSourceExtractionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetExtraction(
        string workspaceSlug,
        Guid documentId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        var result = await extractions.GetAsync(documentId, versionNumber, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // Private creator material, and large: nothing may cache it anywhere between here and the browser.
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Corrects the text extracted from a brand source document's current version.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="documentId">The document the version belongs to. Constrained to a Guid.</param>
    /// <param name="versionNumber">
    /// The version whose text to correct. Must be the document's current version — see below.
    /// </param>
    /// <param name="model">The corrected text in full, the creator's reason, and `expectedExtractionId`.</param>
    /// <param name="idempotencyKey">
    /// Required. A repeat of the same key for the same version, artifact, text and reason returns the original
    /// response with `Idempotent-Replayed: true` and stores nothing new; the same key with anything different
    /// answers `422 idempotency.key_reused`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. **Nothing is overwritten.** The correction becomes a new artifact at the next `ordinal`
    /// with `origin: Corrected`, recording who made it and why; the uploaded file, the parser's artifact and
    /// every earlier correction all stay exactly where they are and stay readable. The document's own
    /// `extraction.state` then reads `Succeeded` with `origin: Corrected`.
    ///
    /// **Correcting an `Unsupported` or `Failed` extraction is the point of this route**, not an edge case: a
    /// scanned PDF or an image has no text to read, and typing it in is how a creator makes that document usable.
    ///
    /// `text` is stored as the creator wrote it. Line endings become `\n`, a leading byte-order mark is dropped
    /// and the text ends on one newline, because that is the stored form; nothing else is reshaped, and text
    /// carrying control or invisible formatting characters is **refused** rather than quietly cleaned —
    /// `400 brand.source.invalid_request`, as for text longer than four megabytes or a missing `reason`.
    ///
    /// `expectedExtractionId` is required and names the artifact this correction was composed against. One that
    /// has since been superseded answers `409 brand.source.extraction.conflict` and writes nothing, leaving the
    /// creator's attempted text in their own client rather than merging it into something they have not seen.
    /// A version whose text has not been read yet answers `409 brand.source.extraction.pending.conflict` — wait
    /// for the extraction, then correct it. A version that is not the document's current one answers
    /// `409 brand.source.extraction.superseded.conflict`: only the current version's text has consumers, so
    /// correcting an older one would be a write with no visible effect. Reading an older version's text stays
    /// allowed. An **archived** document answers `409 brand.source.archived.conflict` — restore it first.
    ///
    /// Text byte-identical to what is already stored is answered with the current artifact and writes nothing:
    /// an ordinal that changes nothing is provenance a later reader has to explain for no gain.
    ///
    /// An unknown id, another workspace's document, a removed one and a version this document does not have all
    /// answer `404 brand.source.not_found`. Private storage that cannot be reached answers
    /// `503 brand.source.storage.unavailable` with nothing recorded, after which the same key may be resent.
    ///
    /// The corrected text stays private and stays untrusted: it is delimited source material wherever it later
    /// reaches a prompt, never instruction.
    /// </remarks>
    [HttpPost("{documentId:guid}/versions/{versionNumber:int}/extraction/corrections")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]

    // Raised above the 4 MB default so that a four-megabyte text, once escaped into JSON, still reaches the
    // validator — which refuses it with a stable code and a field error rather than letting the transport answer
    // 413 with nothing a client can act on. The gateway's route for this path carries the same number.
    [RequestSizeLimit(BrandPolicy.ExtractionCorrectionRequestMaxBytes)]
    [ProducesResponseType<BrandSourceExtractionServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> CorrectExtraction(
        string workspaceSlug,
        Guid documentId,
        int versionNumber,
        [FromBody] CorrectBrandSourceExtractionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var outcome = await extractions.CorrectAsync(
            userId, documentId, versionNumber, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, corrected =>
            Created(
                $"/api/v1/workspaces/{workspaceSlug}/brand-source-documents/{documentId}"
                    + $"/versions/{versionNumber}/extraction",
                corrected));
    }
}

/// <summary>
/// The multipart form an upload arrives as. HTTP-shaped, so it lives beside the controller: the file part
/// becomes a stream and the rest an <see cref="UploadBrandSourceDocumentViewModel"/> before the facade sees it.
/// </summary>
public sealed class UploadBrandSourceDocumentForm
{
    /// <summary>The file. Its declared content type is ignored; its name is kept for display only.</summary>
    [FromForm(Name = "file")]
    public IFormFile? File { get; set; }

    [FromForm(Name = "title")]
    public string? Title { get; set; }

    [FromForm(Name = "documentType")]
    public BrandSourceDocumentType? DocumentType { get; set; }

    [FromForm(Name = "purpose")]
    public BrandSourcePurpose? Purpose { get; set; }

    [FromForm(Name = "channelKey")]
    public string? ChannelKey { get; set; }

    [FromForm(Name = "audience")]
    public string? Audience { get; set; }

    /// <summary>Repeat the field once per tag.</summary>
    [FromForm(Name = "tags")]
    public List<string?>? Tags { get; set; }
}

/// <summary>
/// The multipart form a replacement arrives as. HTTP-shaped, so it lives beside the controller.
/// </summary>
/// <remarks>
/// Deliberately two fields. There is no title, type, purpose, channel, audience or tag here: a replacement
/// replaces the creator's file, not their description of it, so a creator re-uploading a file cannot
/// reclassify the document at the same time without meaning to.
/// </remarks>
public sealed class ReplaceBrandSourceDocumentForm
{
    /// <summary>The new file. Its declared content type is ignored; its name is kept for display only.</summary>
    [FromForm(Name = "file")]
    public IFormFile? File { get; set; }

    /// <summary>The <c>concurrencyToken</c> from the document read this replacement was composed against.</summary>
    [FromForm(Name = "expectedConcurrencyToken")]
    public string? ExpectedConcurrencyToken { get; set; }
}

