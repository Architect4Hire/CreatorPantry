using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// 11A.17: request and follow a brand-guide proposal — voice, tone, tenor, style, language, channel, blog,
/// social and visual guidance — drawn from one style guide's own answers and the source documents the creator
/// selected. A proposal the creator reads and judges; it never becomes their guide.
/// </summary>
/// <remarks>
/// Not nested under a recipe, unlike every other AI request route: this capability reads no recipe at all. Its
/// subject is a brand style guide, named in the body rather than in the path, because the proposal belongs to
/// the workspace's AI operations rather than to the guide — a guide may have many, and each one is followed
/// through this route by its own request id.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-guide-proposal-requests")]
public sealed class BrandGuideProposalRequestsController(
    IAiBrandGuideProposalRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for a brand-guide proposal. Returns 202 with the request to follow.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The guide, the dimensions, any channels, and the source documents to ground in.</param>
    /// <param name="idempotencyKey">
    /// Required. Generation is not free, so a retried request returns the first answer rather than buying a
    /// second; the same key with a different body is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, as for every other generation request. The work runs in the background: this
    /// answers `202` with a request id, and the `GET` below reports where it got to.
    ///
    /// `dimensions` defaults to every dimension except `channel`, which needs `channelKeys` to mean anything.
    /// A channel key the product does not know is `400`; channel keys sent without asking for channel guidance
    /// are also `400`, rather than ignored.
    ///
    /// `sourceDocumentIds` is optional — a proposal from the creator's own guide answers alone is legitimate,
    /// and says so in its findings. Each document's **current** version is pinned server-side, so the proposal
    /// records the exact text it was grounded in. A document that does not resolve, for any reason, answers
    /// `422` identically for every cause. A document that resolves but has no indexed passages yet is not an
    /// error: it travels, and the proposal reports that the evidence was thin.
    ///
    /// The guide's working version is pinned too, and the task refuses to run if the guide is edited in the
    /// meantime — a proposal explained by answers that have since been rewritten is unexplainable.
    ///
    /// **Nothing returned here becomes the guide.** Every claim carries a citation to a passage this request
    /// supplied, or says it rests on the creator's answers or on nothing; contradictions between sources are
    /// reported with both sides cited; and the result is a proposal the creator reads, edits and writes their
    /// own version from. There is no code path from a stored row to a guide.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestBrandGuideProposalViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/brand-guide-proposal-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Reports where one brand-guide proposal request got to, and its proposal once there is one.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="requestId">The request to follow, as the `POST` returned it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. An unknown id, another workspace's request, and a request that ran a different task
    /// all answer `404` identically.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetAsync(requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Accepts reviewed brand guidance into a new draft version of the guide, or records that it was rejected.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="requestId">The proposal request being decided. It is the operation's id.</param>
    /// <param name="model">
    /// The decision, the items being accepted, and any wording the creator rewrote. See
    /// <see cref="AcceptBrandGuideProposalViewModel"/> — the guide is taken from the proposal's own stored inputs,
    /// so a client cannot aim accepted guidance at a guide the proposal was never about.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// **Editor or above**, unlike the `POST` that asks for a proposal: asking produces something to read, and
    /// this writes a version of the workspace's own brand voice.
    /// </para>
    /// <para>
    /// **The only step that turns proposed guidance into a brand guide.** `acceptedChangeIds` names the
    /// `changeId` of each item's text row; the rows describing it — its dimension, channel, evidence basis and
    /// citations — are accepted with it, and naming one of those on its own is a `400`. `acceptAll` must name
    /// every item: a well-formed body is not by itself a confirmation that the guidance was reviewed.
    /// </para>
    /// <para>
    /// **The new version is a draft, laid over the guide's working version.** Accepted sections replace the same
    /// section key and are added where the guide had none; accepted rules are appended, and one the guide already
    /// held is reported in `rulesAlreadyPresent` rather than duplicated; citations are added to what the working
    /// version already cited. Everything the proposal did not speak to travels through untouched. **Nothing is
    /// approved and nothing is activated** — the workspace default still needs an approval and an Owner.
    /// </para>
    /// <para>
    /// **A guide edited since the proposal was composed is `409 brand.guide.workingVersion.conflict`**, naming
    /// both version numbers. Laying the guidance over a version nobody compared it with would be the one thing
    /// acceptance must not do, and there is no override: ask for the proposal again. An archived guide is `409
    /// brand.guide.archived.conflict`, and guidance that would take the guide past its rule, channel-variant or
    /// source caps is `400 brand.guide.version.limit.invalid_request` — refused rather than truncated.
    /// </para>
    /// <para>
    /// **A cited source document replaced since is not an error.** The citation is written at the version the
    /// proposal actually read and is never re-pointed at the newer text, and `written.staleSourceCount` reports
    /// how many are now behind — which is what decides whether the version can be activated as it stands.
    /// </para>
    /// <para>
    /// `written` is null in three cases, and the reply separates them: a rejection (see `status`), a retry of a
    /// decision already recorded (see `replayed`), and an acceptance whose guidance already matched the guide
    /// word for word, which writes no version for the same reason a creator's own no-op edit writes none.
    /// Accepted items that are reported conflicts or uncertainties produce no guidance at all and are counted in
    /// `droppedItemCount`.
    /// </para>
    /// <para>
    /// **No idempotency key, and retrying is safe without one.** A proposal that has already been decided is
    /// recognised inside the acceptance transaction and answered with `replayed: true`, never a second version.
    /// A retry asking for a *different* decision is a `409`. A retry that carries rewrites is also refused:
    /// nothing stores what the creator's words were, so "already done" would discard the words in this request
    /// while reporting success.
    /// </para>
    /// <para>
    /// The version, the per-item decisions, the feedback and this request's terminal status commit together or
    /// not at all — guidance the guide's own rules refuse leaves nothing behind, not even the decision. `200`
    /// rather than `201`: the reply is the decision, which names the version it wrote.
    /// </para>
    /// </remarks>
    [HttpPost("{requestId:guid}/acceptance")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<AiBrandGuideAcceptanceServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    // ValidationProblemDetails on the 409 because brand.guide.workingVersion.conflict carries both version
    // numbers in its extensions, which is the only way a client can tell how far behind it is. One schema per
    // status, and the wider one is the honest answer.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Accept(
        string workspaceSlug,
        Guid requestId,
        AcceptBrandGuideProposalViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var result = await requests.AcceptAsync(userId, requestId, model, cancellationToken);

        // No Location: the guide version is not a resource of this route, and the reply already names it.
        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
