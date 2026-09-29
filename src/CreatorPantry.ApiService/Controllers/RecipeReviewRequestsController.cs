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
/// AIREC-006: field-linked findings about one pinned version of an existing recipe — completeness,
/// consistency, timing, temperature, ambiguous steps, unused ingredients, likely failures, allergen and dietary
/// conflicts, and unsupported claims — each with a severity and evidence.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no acceptance route here, and there is not meant to be.</strong> A review proposes no
/// change to the recipe — the operation runs in <see cref="AiOperationScope.Advisory"/> and its stored rows
/// carry a target kind no change can be applied from — so there is nothing to accept. A finding is a review
/// signal a creator judges, not a certification and not an edit.
/// </para>
/// <para>
/// Its own route rather than the generic <c>ai-proposals</c> one, because that contract lets the client choose
/// a scope and a review's scope is the server's alone to fix (<see cref="AiTaskCatalog.RequiresTaskInputs"/>).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/review-requests")]
public sealed class RecipeReviewRequestsController(IAiReviewRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for a review of one exact version of a recipe.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to review.</param>
    /// <param name="model">
    /// The version to review. It cannot name a task, a prompt, a model, a provider parameter, or a scope — a
    /// review changes nothing, and the server records that itself.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a repeat of the same key and the same request returns
    /// the original with <c>Idempotent-Replayed: true</c> rather than buying a second answer.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <c>202</c>: nothing has been generated yet, and nothing about the recipe changes when it has. The
    /// request is durable and the model is called by the worker afterwards, so the API does not stay open
    /// around unbounded provider work (api-contract.md).
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
        RequestRecipeReviewViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/review-requests/"
                + accepted.AiProposalRequestId,
            accepted));
    }

    /// <summary>Where a requested review has got to, and the findings once there are any.</summary>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <remarks>
    /// Polling before the findings exist is a <c>200</c> carrying a status, not a <c>404</c> — the request
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
