using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>AIREC-001: recipe concept generation from a creator's structured brief. Names no recipe.</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipe-concept-requests")]
public sealed class RecipeConceptRequestsController(IAiConceptRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for two to five distinct recipe concepts from a structured brief.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The eleven declared brief fields, and nothing else. It cannot carry a prompt, a model, a provider
    /// parameter, or a recipe id -- see <see cref="RequestRecipeConceptsViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a repeat of the same key and the same brief returns
    /// the original with <c>Idempotent-Replayed: true</c> rather than buying a second answer.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <c>202</c> rather than <c>201</c>: nothing has been generated yet. The request is durable and the model
    /// is called by the worker afterwards, so the API does not stay open around unbounded provider work
    /// (api-contract.md). The <c>Location</c> header points at the status resource to poll.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestRecipeConceptsViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipe-concept-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested generation has got to, and the proposed concepts once there are any.</summary>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <remarks>
    /// Polling before the concepts exist is a <c>200</c> carrying a status, not a <c>404</c> -- the request
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
}
