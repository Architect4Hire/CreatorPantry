using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// AF.6.4: the posts written for one piece of creative work — reading the package, editing one channel, and
/// deciding about one channel.
/// </summary>
/// <remarks>
/// <para>
/// Under the creative context rather than under a request, because the package belongs to the work and
/// outlives every request made for it: a second request for another channel writes into this same package,
/// and a creator returning tomorrow has no request id to quote.
/// </para>
/// <para>
/// <strong>Two facades, and one of the four decisions is why.</strong> Accepting, rejecting and reaffirming
/// finish before they return and are the content module's; regenerating spends the account's allowance and
/// produces an operation to poll, so it is the AI module's request seam — the same seam
/// <see cref="ChannelPostRequestsController"/> exposes, called with the one channel being replaced.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/creative-contexts/{contextId:guid}/posts")]
public sealed class CreativeContextPostsController(
    ISocialPackageFacade posts, IAiChannelPostsRequestFacade requests) : ControllerBase
{
    /// <summary>The posts written for this piece of work, one entry per channel.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="contextId">The piece of work.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. A piece of work with nothing written for it yet is a `204`,
    /// not a `404`: there is no package, and reporting the work missing would be untrue. A context this
    /// workspace does not have answers `404 content.social.not_found`, the same answer another workspace's
    /// gets.
    ///
    /// Each channel carries its newest revision, the one last accepted if any, and whether what was accepted
    /// is still current — decided from the recipe version the accepted words were pinned to rather than from
    /// the status alone. The response is `no-store`: it is workspace-private creator content.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<SocialPackageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid contextId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await posts.GetAsync(contextId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // Said rather than inferred. A null value through Ok() is a 204 by the framework's own rules, and a
        // contract this route documents should not rest on that staying true.
        return result.Value is { } package ? Ok(package) : NoContent();
    }

    /// <summary>Replaces one channel's words with the creator's own, as a new revision.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed, as above.</param>
    /// <param name="contextId">The piece of work.</param>
    /// <param name="channelKey">The channel being edited.</param>
    /// <param name="model">
    /// The words, and the revision they were composed against. It carries no count, no limit and no status:
    /// the server measures the body against the channel's writing profile.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. `200` rather than `202`: nothing is generated, and the revision commits before
    /// the reply is written. The whole package comes back, because an edit can change what the channel's
    /// staleness and decision state are.
    ///
    /// **A new revision, never an overwrite.** The generated body it replaces stays in the history, and
    /// anything already accepted stays accepted until a decision says otherwise.
    ///
    /// `expectedLatestRevisionId` is the version check: an edit composed against anything but the channel's
    /// newest revision answers `409 content.social.stale.conflict` with both the server's state and the
    /// creator's words intact — reload and send them again. Omit it only for the first words on a channel.
    ///
    /// A channel with no writing profile answers `422 content.social.channel.unprocessable`; a channel this
    /// package does not have and the catalogue does not know answers the same. An empty or over-long body is a
    /// `400 content.social.invalid`. A pin the workspace cannot see is
    /// `422 content.social.source.unprocessable` — one sentence for every such refusal, so a write cannot be
    /// used to ask what a neighbour owns.
    /// </remarks>
    [HttpPatch("{channelKey}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<SocialPackageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Edit(
        string workspaceSlug,
        Guid contextId,
        string channelKey,
        EditChannelPostViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await posts.EditAsync(contextId, channelKey, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Accepts, rejects, regenerates or reaffirms one channel's post.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed, as above.</param>
    /// <param name="contextId">The piece of work.</param>
    /// <param name="channelKey">The channel being decided about. No decision can reach another channel's.</param>
    /// <param name="model">The decision, and the revision it is about.</param>
    /// <param name="idempotencyKey">
    /// Required for `regenerate` and unused by the other three: writing a channel again spends the account's
    /// allowance, and accepting twice is already recognised as one decision made twice.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor at the edge, because `regenerate` is a drafting move. Accepting, rejecting and reaffirming
    /// need **Editor** and the domain enforces it: `403 content.social.forbidden` for a Contributor, which is
    /// the deliberate cost of one policy per route — the role that may draft a post is not the role that
    /// decides a workspace's content.
    ///
    /// `accept`, `reject` and `reaffirm` answer `200` with the package. `regenerate` answers `202` with an
    /// operation to poll and a `Location` pointing at it, exactly as a first request does.
    ///
    /// **Accepting twice does not accept twice.** A repeat of the same acceptance writes nothing, creates no
    /// second accepted revision and records no second audit entry; the package comes back unchanged. The same
    /// holds for a repeated rejection.
    ///
    /// **Regenerating one channel leaves the others exactly as they are** — it is a request naming that one
    /// channel, and nothing in this route can reach a neighbouring slot.
    ///
    /// `revisionId` must name the channel's newest revision: anything else is
    /// `409 content.social.stale.conflict`, so a decision composed while looking at an older body cannot land
    /// on words its author has not read. A channel with nothing awaiting that decision is
    /// `409 content.social.decision.conflict`. Accepting words pinned to a recipe version that is no longer
    /// the latest is `409 content.social.source_stale.conflict` — edit, regenerate, or reaffirm.
    /// </remarks>
    [HttpPost("{channelKey}/disposition")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<SocialPackageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Disposition(
        string workspaceSlug,
        Guid contextId,
        string channelKey,
        ChannelPostDispositionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (model?.Decision is ChannelPostDecision.Regenerate)
        {
            var outcome = await requests.RequestAsync(
                new RequestChannelPostsViewModel
                {
                    CreativeContextId = contextId,

                    // This channel and no other. The request seam writes one revision per channel it names,
                    // so naming one is what leaves the neighbours alone.
                    ChannelKeys = [channelKey],
                },
                idempotencyKey,
                cancellationToken);

            return this.IdempotentResult(outcome, accepted => Accepted(
                $"/api/v1/workspaces/{workspaceSlug}/channel-post-requests/{accepted.AiProposalRequestId}",
                accepted));
        }

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await posts.DispositionAsync(userId, contextId, channelKey, model!, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
