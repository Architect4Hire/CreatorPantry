using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The staged images one generation produced: looking at them, keeping a copy, and declining one
/// (IMG-005, IMG-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An image is named by its id and never by its location.</strong> No route here takes a path, a
/// key or a filename, so there is nothing a traversal sequence could be put in — the storage key is
/// generated from the resolved workspace, the operation and the variant, and never leaves the domain
/// (media.md). No URL to a staging object is ever issued.
/// </para>
/// <para>
/// An unknown image, another workspace's, and one whose bytes retention has already removed all answer
/// `404 media.staged_image.not_found`, so none of them tells a caller about the others.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/generated-images")]
public sealed class GeneratedImagesController(
    IStagedImageFacade images, IGeneratedImageGenerationFacade generation) : ControllerBase
{

    /// <summary>Asks for one to four images to be generated from a prompt.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The prompt and how many variants to ask for. Carries no workspace or owner.</param>
    /// <param name="idempotencyKey">
    /// <strong>Required on this route, unlike most.</strong> Image generation is the most expensive call
    /// this product makes, and a client whose request committed but whose response was lost would
    /// otherwise retry and buy a second set of images. There is no natural key to fall back on — the same
    /// prompt twice is a legitimate thing to want — so the header is the only thing standing in the way.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. `202 Accepted`, because nothing is generated in the request: the operation
    /// is queued and a worker claims it, which is what `api-contract.md` asks of long-running work. The
    /// body is the operation — its id and status — and a repeated key returns the first one rather than a
    /// conflict, because the caller asked for images and images are coming.
    ///
    /// The guarantee is the unique index on `(WorkspaceId, IdempotencyKey)` rather than a stored response:
    /// a second row for one key is unrepresentable, so a lost answer cannot become a second charge however
    /// the retry arrives.
    ///
    /// **A key names one request.** The same key sent again with a different prompt, avoid list, variant
    /// count, quality class, proposal, recipe or piece of work — or by a different member — answers
    /// `422 idempotency.key_reused` and queues nothing, rather than returning the first request's operation
    /// for something else that was asked.
    ///
    /// `aiProposalId`, when sent, must name a prompt proposal of this workspace. One that names nothing and
    /// another workspace's both answer `422 media.generation.proposal.unprocessable` naming `aiProposalId`,
    /// in the same words.
    ///
    /// **A run can say what it is for.** `recipeId`, `recipeVersionId` and `creativeContextId` are each
    /// optional and are recorded on the run and returned with it, so a run can be traced to the recipe and
    /// the piece of work that asked for it. They ground nothing — the prompt is sent as written. Each must be
    /// this workspace's: a recipe or version that is not answers `422 media.generation.recipe.unprocessable`
    /// and a piece of work that is not answers `422 media.generation.context.unprocessable`, each naming its
    /// field and each in the same words for an id nobody has and for another workspace's. A version sent
    /// without its recipe answers `400 media.generation.invalid_request`.
    ///
    /// A missing header answers `400 idempotency.key_required`. Asking for fewer than one or more than
    /// four variants answers `400 media.generation.invalid_request`; a Viewer answers
    /// `403 media.generation.forbidden`.
    ///
    /// **A request names how good a picture, never which model** (AP.3.1). `qualityClass` is one of `Draft`,
    /// `Standard`, `High`, `High2K` or `High4K`, and is optional: a request that names none is made at the
    /// host's default class, which `GET .../generated-images/quality-classes` names. A class this host does not
    /// offer answers `422 media.generation.quality_class.unprocessable` naming `qualityClass`. The class is part
    /// of what a key names: the same key naming a different class answers `422 idempotency.key_reused`. A retry
    /// that names none is the request it retries, at whatever class that was made — so a lost answer still finds
    /// its operation if the default has changed — and a replayed key is answered even if its class has since
    /// been taken off this host.
    ///
    /// **A plan says which qualities an account may ask for** (PRICE-007, AP.4.3). A class this host offers but
    /// the account's plan does not allow answers `403 media.generation.quality_class.forbidden`, naming
    /// `qualityClass` and carrying `requiresPlan` and `requiresPlanName` — the cheapest plan that would allow it,
    /// or null when no plan does. `retry` is `none`: the same request cannot succeed until the plan changes.
    /// Nothing is held or queued. A request that names no class is never refused this way, because every plan
    /// allows the host's default class: `AddBillingModule`'s startup validation refuses a catalogue where one does
    /// not, which is what makes the class-less path safe rather than merely untested. A replayed key is never
    /// refused, so a downgrade does not strand pictures already paid for, and the worker does not check again.
    ///
    /// Asked before the allowance and the suspension, deliberately: it is the cheapest refusal and the most
    /// specific, and it writes nothing. A suspended account naming a locked class is told about the class.
    ///
    /// **This narrows what the route accepts, on purpose** (PRICE-007). Before plans, every class this host
    /// offered could be asked for by any member; now an account's plan decides. No API version was added: the gate
    /// ships with the picker that respects it, and no deployment configures plans yet, so no shipped client is
    /// refused anything it was previously granted.
    ///
    /// **The prompt has to fit the class's model, and is never cut to fit** (AP.3.2). What is sent is the prompt,
    /// then the avoid list under a heading of its own, and each class's model takes so many characters of it. A
    /// request over that answers `422 media.generation.prompt.unprocessable` naming `promptText` when a shorter
    /// prompt would fit — `POST .../image-prompt-requests/compactions` asks for one — or
    /// `422 media.generation.avoid.unprocessable` naming `avoidText` when the avoid list alone leaves too little
    /// room. Both carry `characterLimit`, `characterCount` and `promptBudget`, the most the prompt may be with
    /// the avoid list kept whole. Nothing is held or queued, and a replayed key is never refused.
    ///
    /// **Pictures are charged to the requesting account's AI allowance** (AP.2.5), and the whole request is
    /// held against it before anything is queued: `variantCount` × the configured rate for the model, tier
    /// and quality the request's class sends. An account whose period will not cover all of it answers
    /// `429 ai.quota.exhausted` with `unit`, `remaining`, `required` and `resetsAt` plus a `Retry-After`, and
    /// one whose AI access is switched off answers `403 ai.quota.suspended` — the same codes a text request is
    /// refused with, because it is the same allowance. Nothing is queued either way. A replayed key is never
    /// refused: its answer is the operation it already bought, and the hold that bought it.
    ///
    /// **A trial is gated twice** (AP.7.1). Its pictures are capped in all, across every workspace and day: a
    /// request that would pass the trial plan's `trialImageCap` answers `403 ai.quota.trial_images.forbidden` with
    /// `trialImageCap` and `trialEndsAt`, and writing goes on. Once the trial's days are over and no plan has been
    /// chosen, every request answers `403 ai.quota.trial_ended.forbidden` with `trialEndedAt`, pack credits or not.
    /// Neither carries a balance or a reset, and nothing is queued.
    ///
    /// A host with no credit rate for the picture it would make answers `503 media.generation.unavailable`
    /// and carries no figures: that is this deployment's misconfiguration, not the caller's balance.
    ///
    /// The action is named `RequestImages` rather than `Request`, which other controllers use and get
    /// away with: this one reads `ControllerBase.Request` for the conditional download below, and an
    /// action of that name hides it.
    /// </remarks>
    [OperationLimit(LimitedOperation.ImageGeneration)]
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<GeneratedImageOperationServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]

    // This host unable to price the picture it would make (AP.2.5) — a misconfiguration rather than anything
    // about the caller. The allowance refusals need no attribute: 403 is declared above, and 429 is described
    // by [OperationLimit]'s own transformer, which an attribute here would overwrite with "Too Many Requests".
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> RequestImages(
        string workspaceSlug,
        [FromBody] RequestGeneratedImagesViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // The same code and message every other route's executor produces for a missing required key,
            // so a client handles one case rather than two. This route enforces it itself because its
            // replay guard is the operation's own unique index rather than a stored idempotency record.
            return this.ProblemFor(new OperationError(
                IdempotencyPolicy.KeyRequiredCode,
                $"This request requires an {IdempotencyPolicy.KeyHeader} header.",
                new Dictionary<string, string[]>()));
        }

        var result = await generation.RequestAsync(
            new GeneratedImageRequest(
                model.PromptText ?? string.Empty,
                model.AvoidText,
                model.AiProposalId,
                model.VariantCount ?? 0,
                idempotencyKey,
                new GeneratedImageSubject(model.RecipeId, model.RecipeVersionId, model.CreativeContextId),
                model.QualityClass),
            cancellationToken);

        return result.Succeeded
            ? Accepted(result.Value)
            : this.ProblemFor(result.Error!);
    }

    /// <summary>
    /// What asking for this many pictures would cost the caller, and what it would leave them (PRICE-004).
    /// </summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The pictures to price: how many, and optionally at which quality class.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only: nothing is held, reserved or written, so it is a GET and asking it twice is free. Priced by
    /// the same code that admits the request, for the model, tier and quality the asked-about `qualityClass`
    /// sends — the host's default when none is named — and the class priced comes back as `qualityClass`. A
    /// snapshot: the request is still the decision, and can be refused if the balance moved in between.
    ///
    /// Refused as the request it prices would be: `400 media.generation.invalid_request` for a count outside one
    /// to four, `422 media.generation.quality_class.unprocessable` for a class this host does not offer,
    /// `403 media.generation.quality_class.forbidden` for one the account's plan does not allow (AP.4.3), and
    /// `403 media.generation.forbidden` for a member below Contributor.
    ///
    /// **A host that cannot price the picture answers `200` with `outcome: Unavailable`, not `503`.** The request
    /// answers `503 media.generation.unavailable` because it cannot do what was asked; a quote can — it says there
    /// is no price to show — and a page asking on every change of count would otherwise log an error per step for
    /// one misconfiguration.
    /// </remarks>
    [HttpGet("quote")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<GeneratedImageQuoteServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Quote(
        string workspaceSlug,
        [FromQuery] GeneratedImageQuoteViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var result = await generation.QuoteAsync(model.Variants, model.QualityClass, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>
    /// The picture qualities a creator may ask for here, each with what one picture of it costs (PRICE-006, PRICE-013).
    /// </summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only: a price list, not the caller's balance — what a particular request would cost and leave is the
    /// quote's. Contributor, like the quote and the request it is for.
    ///
    /// `classes` is in ascending order, `Draft` to `High4K`, and holds only the classes this host offers. Each
    /// `perPicture` is in `unit` (credits) and is the rate the request is charged at for what that class sends
    /// — read from the same table admission holds from, so it is exact, not typical (B-55). It names no model:
    /// a request names a class. `defaultClass` is what a request that names none is made at. A host that offers
    /// no classes answers an empty list and a null `defaultClass`; every picture there is the deployment's own
    /// default.
    ///
    /// **The prices are the same for every member; which classes are allowed is not** (PRICE-007, AP.4.3). Each
    /// class carries `allowed` — whether the asking account's plan allows a picture of it — and, when it does
    /// not, `requiresPlan` and `requiresPlanName`: the cheapest plan that would, or null when no plan does. A
    /// locked class stays in the list with its price, so a picker can show it and say what unlocks it; asking
    /// for one answers `403 media.generation.quality_class.forbidden`, and hiding it in the browser is
    /// presentation only. A plan belongs to the account rather than the workspace (B-47), so two members of one
    /// workspace can read different flags from this list and nothing may cache it by workspace alone. On a host
    /// with no plans configured every class is `allowed` with no plan named.
    /// </remarks>
    [HttpGet("quality-classes")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<GeneratedImageQualityClassesServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> QualityClasses(string workspaceSlug, CancellationToken cancellationToken)
    {
        var result = await generation.ListQualityClassesAsync(cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads one image request and the images it has produced so far.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="operationId">The request to read. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read, matching the preview and download below: a member who may look at an image may
    /// certainly learn that it exists.
    ///
    /// **This is what makes the 202 above usable.** That response carries a status and two counts but no image
    /// identities, so a client had an operation and no way to learn a single thing in it — progress, a contact
    /// sheet, a download and a decline were all unreachable without this read.
    ///
    /// The body carries the operation plus one entry per image, ordered by variant, with each image's status,
    /// media type, dimensions, size and retention deadline. A row exists only once there are bytes to describe,
    /// so entries appear as variants land and a client can fill a grid in rather than waiting for all of them.
    ///
    /// **No prompt text, no object key, no checksum, no address of any kind.** The repository projects rather
    /// than reading entities, so none of them is even loaded. `Cache-Control: no-store`, because an operation in
    /// flight changes and because its images are private creator content.
    ///
    /// An unknown operation and another workspace's answer `404 media.generation_operation.not_found` alike, so
    /// neither discloses the other.
    /// </remarks>
    [HttpGet("operations/{operationId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<GeneratedImageOperationDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetOperation(
        string workspaceSlug,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        // Set before the branch, so a refusal is no more cacheable than an answer: an operation in flight is
        // different on the next read, what it names is private creator content, and a cached 404 would be a
        // small signal about which ids exist.
        Response.Headers.CacheControl = "no-store";

        var result = await generation.GetOperationAsync(operationId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Renders one staged image for viewing in the browser.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="generatedImageId">The image to render. Constrained to a Guid.</param>
    /// <param name="rendition">
    /// Which encoding to send: `web` (the default), `thumbnail` or `original`. Anything else answers
    /// `400 media.staged_image.invalid_request`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Identical to the download below but for the disposition: this is `inline`, so a
    /// browser shows it where a page asked for it, and that is the only difference.
    ///
    /// **The smaller picture is the default.** With no `rendition` this sends the image's web rendition — a
    /// JPEG fitted inside 1600 pixels — when it has one, and the image as it was staged when it does not:
    /// not made yet, or a picture that could not be made smaller. `rendition=thumbnail` asks for the 480
    /// pixel one and `rendition=original` always sends the staged bytes. **`X-Rendition` states which was
    /// sent** — `web`, `thumbnail` or `original` — so a caller that asked for one and got another can tell.
    /// A rendition that does not exist is never a 404: the original is sent instead.
    ///
    /// `Content-Type`, `Content-Length` and the `ETag` describe the bytes actually sent. For the original,
    /// `Content-Type` is the media type **established from the returned bytes at staging**, never one a
    /// provider declared; for a rendition it is `image/jpeg`.
    /// `X-Content-Type-Options: nosniff` and `Cache-Control: no-store` are set, and the response carries a
    /// strong `ETag` — the checksum of the bytes sent — so `If-None-Match` answers 304. Staged bytes and
    /// their renditions are immutable, so that tag identifies this representation for as long as it exists;
    /// the same address answers with a different tag once a rendition has been made.
    /// Metadata that exists whose bytes cannot be read answers `503 media.staged_image.unavailable`
    /// rather than 404: the image is there, and retrying is the remedy. No range requests.
    ///
    /// **A declined image, and one that expired unchosen, is not served** — it answers the same 404 an
    /// unknown image does, from the moment it is declined rather than from whenever its bytes are collected,
    /// and whatever `rendition` asks for. A kept image is still served. The download route below follows the
    /// same rule.
    /// </remarks>
    [HttpGet("{generatedImageId:guid}/preview")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public Task<IActionResult> Preview(
        string workspaceSlug,
        Guid generatedImageId,
        [FromQuery(Name = MediaRenditionSelector.QueryName)] string? rendition,
        CancellationToken cancellationToken) =>
        SendAsync(generatedImageId, inline: true, rendition, cancellationToken);

    /// <summary>Downloads one staged image: its web rendition by default, or the bytes the provider returned.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="generatedImageId">The image to download. Constrained to a Guid.</param>
    /// <param name="rendition">
    /// Which encoding to send: `web` (the default), `thumbnail` or `original`. Anything else answers
    /// `400 media.staged_image.invalid_request`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. **`rendition=original` is the image byte for byte as the provider returned it.**
    /// With no `rendition` the download is the web rendition when there is one, exactly as the preview above
    /// chooses, and `X-Rendition` states which was sent.
    ///
    /// `Content-Disposition: attachment` names it `generated-{n}.{ext}`, where `n` is the variant counted
    /// from one and the extension comes from the media type of the bytes sent — `jpg` for a rendition, and
    /// for the original whatever was established from the bytes at staging — so the name describes what is
    /// actually there. The name is ASCII `a-z0-9`, one hyphen and one dot by construction and carries no id,
    /// workspace, prompt or storage location. Everything else matches the preview above, including the
    /// entity tag and the 503 for unreadable bytes.
    /// </remarks>
    [HttpGet("{generatedImageId:guid}/content")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public Task<IActionResult> Download(
        string workspaceSlug,
        Guid generatedImageId,
        [FromQuery(Name = MediaRenditionSelector.QueryName)] string? rendition,
        CancellationToken cancellationToken) =>
        SendAsync(generatedImageId, inline: false, rendition, cancellationToken);

    /// <summary>Declines one staged image, so retention may remove it.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="generatedImageId">The image to decline. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above — the same role that may ask for images in the first place. The row moves to
    /// `Rejected` and the bytes stay until the retention sweep collects them, which is what makes this
    /// answer fast and makes a half-done delete impossible: there is no second system to fail.
    /// `204` whether this call declined the image or a previous one already had, so a client that retries
    /// gets the answer it wanted rather than a conflict. A kept or expired image answers
    /// `409 media.staged_image.conflict`; an unknown or another workspace's answers
    /// `404 media.staged_image.not_found`.
    /// **A declined image is not a DAM asset and never becomes one** (IMG-006).
    /// </remarks>
    [HttpDelete("{generatedImageId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Reject(
        string workspaceSlug, Guid generatedImageId, CancellationToken cancellationToken)
    {
        var result = await images.RejectAsync(generatedImageId, cancellationToken);

        return result.Succeeded ? NoContent() : this.ProblemFor(result.Error!);
    }

    /// <summary>
    /// The one place that turns an opened staged image into a response.
    /// </summary>
    /// <remarks>
    /// Preview and download differ by one header, so they share everything else rather than drifting: a
    /// change to the caching, the entity tag or the sniffing protection that reached only one of them
    /// would be a hole in the other.
    /// </remarks>
    private async Task<IActionResult> SendAsync(
        Guid generatedImageId, bool inline, string? rendition, CancellationToken cancellationToken)
    {
        // Both staged routes default to the web rendition. Decided before anything is opened, so a value
        // that is not one of the three reads nothing.
        if (!MediaRenditionSelector.TryParse(rendition, MediaRenditionPurpose.Web, out var wanted))
        {
            return this.ProblemFor(new OperationError(
                MediaErrorCodes.StagedImageInvalidRequest,
                "The rendition must be web, thumbnail or original.",
                new Dictionary<string, string[]>
                {
                    [MediaRenditionSelector.QueryName] = ["Use web, thumbnail or original."],
                }));
        }

        var result = await images.OpenAsync(generatedImageId, wanted, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var download = result.Value!;

        // First, before anything that could throw. The facade hands back an *open* object, so from here to
        // the end of the request something must own releasing it. The lease rather than the stream:
        // disposing the lease is what the store's contract says releases a read, and it is registered on
        // the 304 path too so neither branch has a dispose of its own to forget.
        Response.RegisterForDisposeAsync(download);

        // Strong, and legitimately so: staged bytes are never rewritten — the store is create-only and a
        // variant has one row — and neither is a rendition, so the checksum of whichever is being sent
        // identifies this representation for as long as it exists.
        var entityTag = new EntityTagHeaderValue($"\"{download.ContentChecksum}\"");

        Response.Headers.CacheControl = "no-store";
        Response.Headers.ETag = entityTag.ToString();

        // What was sent, which is not always what was asked for: a rendition that does not exist is
        // answered with the original. On the 304 as well, since it describes the same representation.
        Response.Headers[MediaRenditionSelector.HeaderName] = MediaRenditionSelector.NameOf(download.Rendition);

        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } candidates
            && candidates.Any(candidate => candidate.Compare(entityTag, useStrongComparison: true)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Private creator material: nothing may be sniffed into another type. EdgeHardening already puts
        // `default-src 'none'` on every response from this host, which is stricter than anything this
        // route would add, so no Content-Security-Policy is set here.
        Response.Headers.XContentTypeOptions = "nosniff";

        // ASCII a-z, 0-9, one hyphen and one dot by construction, so there is nothing to quote or encode.
        // Set here rather than through FileStreamResult's FileDownloadName so that one piece of code
        // decides the name and the header it lands in.
        Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue(inline ? "inline" : "attachment")
            {
                FileName = download.FileName,
            }.ToString();

        // The stored size, so a client can show progress. Safe to state because staging refuses to commit
        // a row whose size storage disagrees with: the row's size is the object's size.
        Response.ContentLength = download.SizeBytes;

        // `enableRangeProcessing` is left off and no EntityTag is set on the result: this contract offers
        // no ranges, and the conditional request is decided above, so exactly one place compares a tag.
        return new FileStreamResult(download.Content, download.MediaType);
    }
}

/// <summary>The question a quote answers: how many pictures (AP.2.6).</summary>
/// <remarks>
/// Nullable for the same reason the request's own count is: a missing one is this feature's validation error,
/// which can say which field was wrong, rather than the model binder's.
/// </remarks>
public sealed record GeneratedImageQuoteViewModel
{
    /// <summary>How many pictures to price, as a request would ask for them.</summary>
    [FromQuery(Name = "variants")]
    public int? Variants { get; init; }

    /// <summary>The picture quality to price (AP.3.1). Omitted means the host's default class.</summary>
    [FromQuery(Name = "qualityClass")]
    public GeneratedImageQualityClass? QualityClass { get; init; }
}

/// <summary>The body of an image-generation request (IMG-003).</summary>
/// <remarks>
/// <para>
/// <strong>No workspace, no owner, no idempotency key.</strong> The first two come from the resolved
/// context; the key is a header, because it describes the request rather than what is being asked for.
/// </para>
/// <para>
/// Every field is nullable so that a missing one is this feature's own validation error rather than the
/// model binder's, which cannot say which of a creator's fields was the problem.
/// </para>
/// </remarks>
public sealed record RequestGeneratedImagesViewModel
{
    /// <summary>The prompt as it will be sent, after any edit the creator made to a composed one.</summary>
    public string? PromptText { get; init; }

    /// <summary>What the provider should avoid, when the creator said. Untrusted text, like the prompt.</summary>
    public string? AvoidText { get; init; }

    /// <summary>The IMG-002 proposal the prompt was composed from, when it was composed rather than written.</summary>
    public Guid? AiProposalId { get; init; }

    /// <summary>How many images to ask for. One to four — every one of them is a separate charge.</summary>
    public int? VariantCount { get; init; }

    /// <summary>
    /// The recipe these pictures are for, when they are for one. Recorded on the run; it grounds nothing,
    /// because the prompt is sent as written.
    /// </summary>
    public Guid? RecipeId { get; init; }

    /// <summary>The exact version of that recipe. Never valid without <see cref="RecipeId"/>.</summary>
    public Guid? RecipeVersionId { get; init; }

    /// <summary>The piece of work these pictures are for, when they are for one. Recorded on the run.</summary>
    public Guid? CreativeContextId { get; init; }

    /// <summary>
    /// How good a picture to make (AP.3.1). Omitted means the host's default class. A class, never a model.
    /// </summary>
    public GeneratedImageQualityClass? QualityClass { get; init; }
}
