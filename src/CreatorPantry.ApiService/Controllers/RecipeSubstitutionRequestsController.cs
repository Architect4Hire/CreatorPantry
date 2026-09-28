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
/// AIREC-004: ranked alternatives for one ingredient the creator selected, with what each one does to the
/// dish and what to test before trusting it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no acceptance route here, and there is not meant to be.</strong> A substitution proposes
/// no change to the recipe — the operation runs in <see cref="AiOperationScope.Advisory"/> and its stored rows
/// carry a target kind no change can be applied from — so there is nothing to accept. A creator reads the
/// alternatives and makes the edit themselves, which is what "no automatic replacement" means in practice.
/// </para>
/// <para>
/// Its own route rather than the generic <c>ai-proposals</c> one, because a substitution names an ingredient
/// and that contract has nowhere to put one.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/substitution-requests")]
public sealed class RecipeSubstitutionRequestsController(IAiSubstitutionRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for alternatives to one ingredient in one exact version of a recipe.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe the ingredient belongs to.</param>
    /// <param name="model">
    /// The version, the ingredient, and why the creator is asking. It cannot name a task, a prompt, a model, a
    /// provider parameter or a scope — a substitution changes nothing, and the server records that itself.
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
    public async Task<IActionResult> Request(
        string workspaceSlug,
        Guid recipeId,
        RequestIngredientSubstitutionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/substitution-requests/"
                + accepted.AiProposalRequestId,
            accepted));
    }

    /// <summary>Where a requested substitution has got to, and the alternatives once there are any.</summary>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <remarks>
    /// Polling before the alternatives exist is a <c>200</c> carrying a status, not a <c>404</c> — the request
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
