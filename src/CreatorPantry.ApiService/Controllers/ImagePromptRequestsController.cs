using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// IMG-002: one editable image prompt, composed for one shot of a photography concept the creator chose.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/image-prompt-requests")]
public sealed class ImagePromptRequestsController(
    IAiImagePromptRequestFacade requests,
    IAiImagePromptCompactionRequestFacade compactions) : ControllerBase
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
    [OperationLimit(LimitedOperation.AiRequest)]
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

    /// <summary>Asks for a shorter version of a prompt, to fit a picture quality whose model takes fewer characters.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed, as on the request above.</param>
    /// <param name="model">
    /// The prompt and avoid list a picture request was refused for, and the quality class it asked for. It
    /// carries no length: the server measures the prompt against that class's model itself.
    /// </param>
    /// <param name="idempotencyKey">Required, for the reason composing a prompt requires one.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. `202`, and the `Location` header points at the same status resource a composed
    /// prompt is read from: a shorter prompt is an image-prompt request, and its proposal has the same shape — the
    /// prompt, and the avoid list.
    ///
    /// **Measured the way a picture request is.** The prompt and avoid list are measured against the model the
    /// class sends to, exactly as `POST .../generated-images` measures them. One that already fits is refused
    /// `422 ai.imagePromptCompaction.prompt.unprocessable`, and one whose avoid list alone leaves no room is refused
    /// `422 ai.imagePromptCompaction.avoid.unprocessable`; both carry `characterLimit`, `characterCount` and
    /// `promptBudget`. A class this host does not offer is `422 media.generation.quality_class.unprocessable`, the
    /// picture request's own refusal. A deployment that cannot say its limit answers
    /// `503 ai.imagePromptCompaction.unavailable`.
    ///
    /// **The avoid list is kept word for word and never shown to the model.** The proposal carries it as written,
    /// beside a prompt shortened to fit what is left. A prompt the model cannot fit to that is refused, never cut,
    /// and the operation fails.
    ///
    /// **Never silent.** Every proposal says it was shortened by AI and that the creator's own prompt is
    /// unchanged, and lists what the model says it left out. Nothing is sent for pictures until the creator does
    /// so, with the shortened text and this proposal's id.
    /// </remarks>
    [OperationLimit(LimitedOperation.AiRequest)]
    [HttpPost("compactions")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Compact(
        string workspaceSlug,
        RequestImagePromptCompactionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await compactions.RequestAsync(model, idempotencyKey, cancellationToken);

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
