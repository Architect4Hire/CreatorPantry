using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// AF.6.4: asking for posts about one piece of creative work, and following what came back.
/// </summary>
/// <remarks>
/// The posts themselves — the edit and the per-channel decision — live under the creative context they belong
/// to, because the package outlives any one request. See <see cref="CreativeContextPostsController"/>.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/channel-post-requests")]
public sealed class ChannelPostRequestsController(IAiChannelPostsRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for one post per named channel, about one piece of creative work.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The piece of work, and the channels to write for. It cannot carry a body, a prompt, a template, a
    /// model, a provider parameter, a schema version or a workspace; see
    /// <see cref="RequestChannelPostsViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Writing posts spends the account's allowance, so a repeat of the same key and the same
    /// request returns the original with `Idempotent-Replayed: true` rather than buying a second set.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, the bar for spending the workspace's allowance. `202` rather than `201`: nothing
    /// has been written yet, the request is durable, and the model is called by the worker afterwards, so the
    /// API does not stay open around unbounded provider work (api-contract.md). The `Location` header points
    /// at the status resource to poll.
    ///
    /// **The key names the request.** The same key with the same piece of work and the same channels in the
    /// same order is a retry and returns the first answer; the same key with a different context, a different
    /// set of channels, or the same channels in a different order answers `422 idempotency.key_reused` — the
    /// order is part of the request because the posts come back in it.
    ///
    /// **An exhausted allowance is refused here, before any provider is called**: `429 ai.quota.exhausted`
    /// carrying what remains, what this would have taken and a `Retry-After`, or `403 ai.quota.suspended` when
    /// AI is switched off for the account. Nothing is queued in either case.
    ///
    /// A piece of work this workspace does not have answers `404 ai.channelPostsContext.not_found` — the same
    /// answer, in the same words, that another workspace's context gets. A channel key that names nothing is a
    /// `400`; a key that names a **retired** channel is `422 ai.channelPosts.channel.unprocessable` unless
    /// this piece of work already has a post for it, because a post already written for a retired channel can
    /// still be written again.
    ///
    /// **What comes back is a proposal.** Each body is stored as a revision awaiting a decision, with the
    /// count its channel's writing profile measured beside it; a body over its channel's limit is stored as
    /// written and flagged, never trimmed. Nothing is accepted until a creator says so.
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
        RequestChannelPostsViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/channel-post-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a post-writing request has got to, and the posts it produced.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed, as on the request above.</param>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read, and this route writes nothing — the generated bodies become
    /// post revisions in the worker, immediately after the proposal commits, so a poll never has to be the
    /// thing that stores a creator's content.
    ///
    /// Polling before the posts exist is a `200` carrying a status and a null `package`, not a `404`: the
    /// request resource exists from the moment it was accepted. An unknown id, another workspace's request,
    /// and a request that ran a different task all answer `404 ai.channelPostsRequest.not_found`.
    ///
    /// A cancelled or failed run answers `200` with the operation's status and `failureCategory` — `Cancelled`
    /// when the run was cancelled — rather than an error: the request was accepted, and what became of it is
    /// the answer.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<ChannelPostRequestStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetAsync(requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
