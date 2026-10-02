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
}
