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
/// IMG-001: photography concepts for a channel, planned as short shot lists. The recipe is optional.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/photography-concept-requests")]
public sealed class PhotographyConceptRequestsController(
    IAiPhotographyConceptRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for up to three ways of photographing a subject.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// A channel, an optional recipe and version, the creator's own concept, and scene and style overrides —
    /// and nothing else. It cannot carry a prompt, a model, a provider parameter, a concept count or a schema
    /// version; see <see cref="RequestPhotographyConceptViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a repeat of the same key and the same request returns
    /// the original with `Idempotent-Replayed: true` rather than buying a second set of concepts.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, the bar for spending the workspace's allowance. `202` rather than `201`: nothing
    /// has been generated yet, the request is durable, and the model is called by the worker afterwards, so the
    /// API does not stay open around unbounded provider work (api-contract.md). The `Location` header points at
    /// the status resource to poll.
    ///
    /// **Every field is optional.** A creator may plan a shoot for a recipe they have written, for an exact
    /// version of it, or for an idea with no recipe at all — so a request that names no recipe is complete
    /// rather than incomplete. A `recipeVersionId` without its `recipeId` is refused, and a recipe this
    /// workspace does not have answers `404 ai.photographyConceptRecipe.not_found` — the same answer another
    /// workspace's recipe gets, in the same words.
    ///
    /// **What comes back is a proposal, and it can never become an edit.** Concepts are stored as structured
    /// changes whose target kind has no path to a recipe, so nothing here rewrites the dish being
    /// photographed. The concepts are planning material: a look, a palette, a mood and one to three framed
    /// shots, exactly one of them the hero.
    ///
    /// **It composes no image prompt and generates no image.** The document has no field for a finished
    /// prompt; composing one from an approved concept is IMG-002, and rendering is later still. The concepts
    /// also carry no numeral other than a crop ratio, no claim about what the food is, and no brand or person —
    /// a concept that breaks any of those is refused rather than trimmed.
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
        RequestPhotographyConceptViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/photography-concept-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested generation has got to, and the proposed concepts once there are any.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed, as on the request above.
    /// </param>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Polling before the concepts exist is a `200` carrying a
    /// status, not a `404` — the request resource exists from the moment it was accepted, and reporting it
    /// missing would make a normal poll look like a failure. An unknown id, another workspace's request, and a
    /// request that ran a different task all answer `404 ai.photographyConceptRequest.not_found`.
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
