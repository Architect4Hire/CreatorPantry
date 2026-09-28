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
/// AIREC-002: one complete structured recipe draft, from a concept the creator selected or from the declared
/// brief fields. Creates no recipe.
/// </summary>
/// <remarks>
/// A draft is a proposal to review, like every other output of this module. Turning an accepted one into a
/// <c>Recipe</c> is a separate explicit step with its own route; nothing here writes to the recipe domain.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipe-draft-requests")]
public sealed class RecipeDraftRequestsController(IAiFirstDraftRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for one structured first draft.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// A selected concept, the declared brief fields, or both — and nothing else. It cannot carry a prompt, a
    /// model, a provider parameter, a task, a scope or a recipe id; see
    /// <see cref="RequestRecipeFirstDraftViewModel"/>. A named concept is resolved against the caller's own
    /// workspace, so its text comes from the stored proposal rather than from this body.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a repeat of the same key and the same request returns
    /// the original with <c>Idempotent-Replayed: true</c> rather than buying a second draft.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <c>202</c> rather than <c>201</c>: nothing has been generated yet, and nothing is created when it is.
    /// The request is durable and the model is called by the worker afterwards, so the API does not stay open
    /// around unbounded provider work (api-contract.md). The <c>Location</c> header points at the status
    /// resource to poll.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestRecipeFirstDraftViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipe-draft-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested generation has got to, and the proposed draft once there is one.</summary>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <remarks>
    /// Polling before the draft exists is a <c>200</c> carrying a status, not a <c>404</c> — the request
    /// resource exists from the moment it was accepted, and reporting it missing would make a normal poll look
    /// like a failure.
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

    /// <summary>Accepts a reviewed draft into a new recipe, or records that it was rejected.</summary>
    /// <param name="requestId">The draft request being decided. It is the operation's id.</param>
    /// <param name="model">
    /// The decision, the parts of the draft being accepted, and any wording the creator rewrote. See
    /// <see cref="AiDraftAcceptanceViewModel"/> — a rewrite may only replace something the draft actually
    /// proposed, and the server checks that against the stored draft.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The only step that creates a recipe from a generated draft.</strong> The recipe, its version 1,
    /// the per-part decisions and this request's terminal status commit together or not at all — an accepted
    /// draft that the recipe rules refuse leaves nothing behind, not even the decision.
    /// </para>
    /// <para>
    /// <strong>No idempotency key, and retrying is safe without one.</strong> A draft that has already been
    /// decided is recognised inside that transaction and answered with the recipe that decision created, never
    /// a second one. A request asking for a <em>different</em> decision is a <c>409</c>: the draft is terminal,
    /// and quietly serving the earlier outcome would tell a creator their selection had been applied when a
    /// different one had.
    /// </para>
    /// <para>
    /// <c>200</c> rather than <c>201</c>: the reply is the decision, which names the recipe it created. A
    /// <c>Location</c> would point at a resource this route does not own.
    /// </para>
    /// </remarks>
    [HttpPost("{requestId:guid}/acceptance")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiDraftAcceptanceServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Accept(
        string workspaceSlug,
        Guid requestId,
        AiDraftAcceptanceViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await requests.AcceptAsync(requestId, model, userId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
