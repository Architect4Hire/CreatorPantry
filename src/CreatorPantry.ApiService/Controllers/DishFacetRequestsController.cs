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
/// Reads a dish name into a cuisine, a dish type and a cooking method, each from the platform vocabulary.
/// Names no recipe.
/// </summary>
/// <remarks>
/// The answer is a <em>suggestion</em>, which is a property of what the caller does with it rather than of
/// this route: the facets come back as catalogue codes the creator's own controls are filled from and which
/// they can change. Nothing here records a cuisine against a recipe, and there is no code path from the
/// stored rows to one (<c>AiChangeTargetKind.DishFacetSuggestion</c>).
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/dish-facet-requests")]
public sealed class DishFacetRequestsController(IAiDishFacetsRequestFacade requests) : ControllerBase
{
    /// <summary>Asks which cuisine, dish type and cooking method a dish name points at.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The dish name, and nothing else. It cannot carry a prompt, a model, a provider parameter, a recipe id,
    /// or the candidate values a model may choose from -- see <see cref="RequestDishFacetsViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. A repeat of the same key and the same name returns the original with
    /// <c>Idempotent-Replayed: true</c> rather than buying a second reading -- which matters more here than on
    /// most of these routes, because this is requested automatically as a creator fills in a name rather than
    /// by a button they pressed once.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <c>202</c> rather than <c>201</c>: nothing has been read yet. The request is durable and the model is
    /// called by the worker afterwards, so the API does not stay open around unbounded provider work
    /// (api-contract.md). The <c>Location</c> header points at the status resource to poll.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestDishFacetsViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/dish-facet-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested reading has got to, and the suggested facets once there are any.</summary>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <remarks>
    /// Polling before the reading exists is a <c>200</c> carrying a status, not a <c>404</c> -- the request
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
