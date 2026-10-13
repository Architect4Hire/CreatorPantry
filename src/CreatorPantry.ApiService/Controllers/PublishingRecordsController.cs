using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/publishing/records")]
public sealed class PublishingRecordsController(IPublishingRecordFacade records) : ControllerBase
{
    /// <summary>Confirms that a piece of content should be published to one profile at one time.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The card, the profile, the text, the time, and the creator's confirmation.</param>
    /// <param name="idempotencyKey">**Required.** See the remarks: without one, a retry could publish twice.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor and above.** Publishing puts the workspace's content in public under its own name, so a
    /// Contributor may prepare the card and the copy but not confirm the delivery.
    ///
    /// **`Idempotency-Key` is required, and it is the only thing standing between a retry and a second post.**
    /// The provider offers no key of its own, so CreatorPantry's is the guard: the same key, with the same body,
    /// from the same member always answers with the record the first confirmation made, carrying
    /// `Idempotent-Replayed: true`, and the provider is asked exactly once. A missing key answers `400
    /// idempotency.key_required` and a malformed one `400 idempotency.key_invalid`; a key reused for a
    /// different body, or by a different member, answers `422 idempotency.key_reused`. A key is durable here —
    /// it keeps working long after the shared idempotency window would have expired.
    ///
    /// **A new key does not buy a second post either.** Confirming for a card, profile and time that already
    /// has a live publication answers `409 publishing.duplicate.conflict`, with the existing record's id in
    /// `publishingRecordId` — enforced by a unique index, so two confirmations racing cannot both get through.
    /// A failed or cancelled publication does not block a fresh confirmation.
    ///
    /// Other refusals: `422 publishing.profile.not_ready.unprocessable` when the provider will not post to
    /// that profile, and `409 publishing.schedule.conflict` when two confirmations shared a key and this one
    /// lost the race — repeating it returns the winner's record.
    ///
    /// **`201` does not mean published.** It means the confirmation is recorded; `status` says what became of
    /// it, and every state is a normal answer rather than an error:
    ///
    /// - `Scheduled` — the provider accepted it and will publish it at `scheduledAt`.
    /// - `Pending` — the provider was busy, unreachable, or not asked, and nothing was published. The
    ///   reconciliation worker picks it up and sends it; there is nothing for the creator to do.
    /// - `Submitting` with **`outcomeUncertain: true`** — the provider was asked and did not confirm, so a post
    ///   may or may not exist. This is never rounded to success or failure, and a client must not present it as
    ///   either; it is settled by asking the provider, not by guessing.
    /// - `Failed` — the provider refused it. `failureCategory` is CreatorPantry's own stable word for why.
    /// - `NeedsAttention` — it has been tried as often as it will be, and a person has to look.
    /// - `Published`, `Cancelling`, `Cancelled` — reached by later phases, not by this route. Treat the status
    ///   as an open set: a client should have a branch for a value it does not recognise.
    ///
    /// **Times.** `scheduledAt` is an instant and must be at least five minutes ahead and within a year.
    /// It is stored in UTC together with the workspace's own scheduling zone, read from its brand profile —
    /// never from the request — and `scheduledLocal` is the wall-clock time that zone makes of it, with no
    /// offset of its own. A workspace with no zone set answers `422
    /// publishing.time_zone.unset.unprocessable` rather than having one guessed for it.
    ///
    /// **A picture cannot be published yet** and answers `422
    /// publishing.media.not_publishable.unprocessable`. The provider fetches images by public address and
    /// CreatorPantry's storage deliberately issues none; the text is scheduled on its own, and sending it
    /// silently without the picture would be worse than saying so.
    ///
    /// **No provider identifier, message or credential is ever returned here**, in a record or in an error.
    /// `failureCategory` is one of CreatorPantry's own words and `failureSummary` is a sentence composed from
    /// it on this server; nothing a provider wrote reaches either. `publishingProfileId` is CreatorPantry's own
    /// id. `expectedPlatform` is an optional guard: send what your copy of the profile list said and a mismatch
    /// answers `409 publishing.platform.mismatch.conflict` instead of publishing to a network you did not mean.
    /// A workspace with no provider answers `422 publishing.provider.not_configured.unprocessable` — the same
    /// answer a deployment with no provider gives, so this cannot be used to learn that a neighbour has one.
    /// A confirmation that was committed but could not be submitted answers `503
    /// publishing.schedule.unavailable`, and retrying it with the same key is safe. The response is `no-store`.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<PublishingRecordServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Schedule(
        string workspaceSlug,
        SchedulePublishingViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await records.ScheduleAsync(userId, model, idempotencyKey, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var outcome = result.Value!;

        // The same header the shared idempotent commands set, for the same reason: a client that retried needs
        // to know it is looking at the first answer rather than a second publication.
        if (outcome.Replayed)
        {
            Response.Headers[IdempotencyPolicy.ReplayedHeader] = "true";
        }

        // The location is the only thing this layer contributes, because only it knows the route shape.
        return Created(
            $"/api/v1/workspaces/{Uri.EscapeDataString(workspaceSlug)}/publishing/records/{outcome.Record.Id}",
            outcome.Record);
    }

    /// <summary>Reads the publications confirmed for one board card.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the confirm route.</param>
    /// <param name="contentCardId">The card whose publications to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Newest scheduled first, and **capped rather than paged**:
    /// `complete` is `false` when a card has more publications than one read returns, so a client is never
    /// shown part of a list believing it is all of it.
    ///
    /// A card with no publications, a card this workspace does not have, and another workspace's card are all
    /// an empty list — so asking about an id cannot reveal that a neighbour owns it. Each item is exactly what
    /// the confirm route returns, with the same meanings and the same silences: no provider identifier,
    /// message or credential.
    ///
    /// **`contentCardId` is required.** Omitting it answers `400`, rather than an empty list that claims to
    /// be complete — "this card has no publications" is exactly the answer that must never be given by
    /// accident. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<PublishingRecordListServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> ListForCard(
        string workspaceSlug, [FromQuery] Guid contentCardId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        // An absent parameter binds to the empty id, which would otherwise read as a card with nothing
        // scheduled — and be reported as the complete list for it.
        if (contentCardId == Guid.Empty)
        {
            return this.ProblemFor(OperationError.Validation(
                PublishingErrorCodes.ScheduleInvalid,
                "That card's publications could not be read as described.",
                [(nameof(contentCardId), "Say which card's publications to read.")]));
        }

        return Ok(await records.ListForCardAsync(contentCardId, cancellationToken));
    }

    /// <summary>Stops one publication going out.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the confirm route.</param>
    /// <param name="recordId">The publication to stop.</param>
    /// <param name="model">The `concurrencyToken` of the record as you last read it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor and above**, as confirming is: stopping a publication is the same decision in reverse.
    ///
    /// **Repeating it is safe, and there is no `Idempotency-Key`.** The record's id is what makes this
    /// converge: asking twice cannot remove two posts, and a record that is already `Cancelled` answers `200`
    /// with itself rather than an error. A cancellation whose outcome is unknown is *meant* to be asked again
    /// — that is how it settles.
    ///
    /// **`expectedConcurrencyToken` is required.** Quote the `concurrencyToken` of the record you read; a
    /// stale one answers `409 publishing.record.conflict`, and one this API never issued answers `400
    /// publishing.cancel.invalid`. The race it catches is the post going out between the screen being drawn
    /// and Cancel being pressed. **A record that is already `Cancelled` answers `200` with itself whatever
    /// token you quote**, so the ordinary retry after a lost answer converges rather than conflicting.
    ///
    /// **What `200` means is in `status`:**
    ///
    /// - `Cancelled` — it will not go out. Either the provider removed its post, or the provider never had
    ///   one, or it was never sent at all.
    /// - `Cancelling` — the provider was asked and has not confirmed. With **`outcomeUncertain: true`**,
    ///   whether the post is gone is not known; send the cancellation again to settle it.
    /// - `Scheduled` — the removal did not happen and the post still stands: the provider would not remove it,
    ///   or refused the credential. `failureCategory` says which.
    /// - `NeedsAttention` — it has been asked as often as it will be, and a person has to look.
    ///
    /// **A publication whose send outcome is unknown cannot be cancelled** and answers `409
    /// publishing.outcome_unknown.conflict`: without the provider's id for a post there is nothing to ask it
    /// to remove, and recording a cancellation would be a claim about a post that may be going out.
    ///
    /// One that has already gone out answers `409 publishing.already_published.conflict` — taking something
    /// down again is a separate capability — and one that failed answers `409
    /// publishing.not_cancellable.conflict`, because nothing is going out to stop. An id this workspace does
    /// not have answers `404`, as does another workspace's. The response is `no-store`.
    /// </remarks>
    [HttpPost("{recordId:guid}/cancellation")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<PublishingRecordServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Cancel(
        string workspaceSlug,
        Guid recordId,
        CancelPublishingViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await records.CancelAsync(userId, recordId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads one publishing record of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the confirm route.</param>
    /// <param name="recordId">The record to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. The fields mean exactly what they mean on the confirm route,
    /// **`outcomeUncertain` included**: a record whose outcome is unknown says so here for as long as it is
    /// unknown.
    ///
    /// An id no record in this workspace has answers `404 publishing.record.not_found`, and so does another
    /// workspace's, so asking cannot reveal that a record exists elsewhere. The response is `no-store`.
    /// </remarks>
    [HttpGet("{recordId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<PublishingRecordServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid recordId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await records.GetAsync(recordId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
