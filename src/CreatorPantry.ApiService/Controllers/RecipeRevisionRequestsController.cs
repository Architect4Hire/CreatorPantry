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
/// AIREC-003: scoped revision of one recipe, in the section the creator selected and towards the goal they
/// stated. Changes nothing until the resulting proposal is accepted.
/// </summary>
/// <remarks>
/// Its own route rather than the generic <c>ai-proposals</c> one, because a revision carries a goal and that
/// contract has nowhere to put one — see <see cref="RequestRecipeRevisionViewModel"/>. Reviewing and deciding
/// what comes back is the proposal route's job: a revision produces an ordinary diff against a pinned
/// version, which is exactly what that route already reviews and applies.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/revision-requests")]
public sealed class RecipeRevisionRequestsController(IAiRevisionRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for a scoped revision of one exact version of a recipe.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to revise.</param>
    /// <param name="model">
    /// The section that may change, the version to change it from, and what the creator wants. It cannot name
    /// a field, a task, a prompt, a model or a provider parameter — which fields a section permits is the
    /// server's answer and the bound it enforces against whatever comes back.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a repeat of the same key and the same request returns
    /// the original with <c>Idempotent-Replayed: true</c> rather than buying a second answer.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <c>202</c>: nothing has been generated yet and nothing is changed when it is. The request is durable
    /// and the model is called by the worker afterwards, so the API does not stay open around unbounded
    /// provider work (api-contract.md).
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
        Guid recipeId,
        RequestRecipeRevisionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/revision-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested revision has got to, and the proposed changes once there are any.</summary>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <remarks>
    /// Polling before the changes exist is a <c>200</c> carrying a status, not a <c>404</c> — the request
    /// resource exists from the moment it was accepted.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, Guid recipeId, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetAsync(recipeId, requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
