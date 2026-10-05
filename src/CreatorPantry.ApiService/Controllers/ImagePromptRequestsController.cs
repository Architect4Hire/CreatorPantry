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
/// IMG-002: one editable image prompt, composed for one shot of a photography concept the creator chose.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/image-prompt-requests")]
public sealed class ImagePromptRequestsController(IAiImagePromptRequestFacade requests) : ControllerBase
{
    /// <summary>Composes one image prompt for one shot of a concept.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The concept and shot to compose for, plus an optional channel, recipe pin, uploaded brief and
    /// overrides. It cannot carry a prompt, a model, a provider, a rendering setting or a schema version; see
    /// <see cref="RequestImagePromptViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Composition spends a provider budget, so a repeat of the same key returns the original with
    /// `Idempotent-Replayed: true` rather than buying a second prompt.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, the bar for spending the workspace's allowance. `202` rather than `201`: nothing
    /// is composed yet, and the model is called by the worker afterwards (api-contract.md). The `Location`
    /// header points at the status resource to poll.
    ///
    /// **The concept and the shot are required, and must be ones this workspace was shown.** A concept is
    /// named by the IMG-001 request that produced it plus the concept's own server-minted id, so a client
    /// passes back what it was shown rather than anything it constructed. An unknown request, another
    /// workspace's, one that ran a different task, one with no proposal yet, a concept the proposal does not
    /// hold, and a shot the concept did not plan all answer `404 ai.imagePromptConcept.not_found` — one answer,
    /// because each is a client naming something it was not shown.
    ///
    /// **There is no separate approval step.** IMG-001 stores no "approved" state for a concept, so choosing a
    /// concept and a shot and asking for its prompt is the approval. What the server checks is that the choice
    /// is real.
    ///
    /// **A brief is untrusted.** When `briefDocumentId` names one of the workspace's own documents, its
    /// extracted text is read through the brand module under the workspace filter, capped, and sent to the
    /// model as untrusted material — never as instructions. A brief is written to instruct somebody, so an
    /// instruction inside one is expected and is not obeyed. A document this workspace does not have answers
    /// `404 ai.imagePromptBrief.not_found`; a recipe it does not have answers
    /// `404 ai.imagePromptRecipe.not_found`.
    ///
    /// **Nothing is rendered and no rendering setting is composed.** The answer is one prompt plus a list of
    /// things to avoid; it carries no provider, model, seed, step count or dimension, and a prompt that writes
    /// one into its prose is refused rather than saved. The creator's edit of the prompt is what becomes
    /// authoritative when they save it to their library.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestImagePromptViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/image-prompt-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested composition has got to, and the prompt once there is one.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed, as on the request above.</param>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Polling before the prompt exists is a `200` carrying a
    /// status, not a `404`. An unknown id, another workspace's request and one that ran a different task all
    /// answer `404 ai.imagePromptRequest.not_found`.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetAsync(requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
