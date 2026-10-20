using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// One workspace's decision about which nutrition reference one platform ingredient is (14.6b, B-40): read it, and
/// confirm, reject or remap it — one ingredient and one decision per request, never in bulk.
/// </summary>
/// <remarks>
/// A mapping is keyed by the platform ingredient, because a creator decides "all-purpose flour is FDC 169761" once
/// for the whole workspace (B-34). The workspace is the route's, resolved with the caller's membership; nothing
/// in a body names one. A mapping never writes to a recipe: no line's display text, quantity or ingredient changes.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/nutrition-mappings/{ingredientId:guid}")]
public sealed class NutritionMappingsController(INutritionMappingFacade mappingFacade) : ControllerBase
{
    /// <summary>This workspace's mapping for one platform ingredient, with the concurrency token a decision quotes.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="ingredientId">The platform ingredient. Constrained to a Guid; a malformed id matches no route.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. `concurrencyToken` is what confirm, reject and remap must quote, and the answer is
    /// `no-store` so a stale token is never served from a cache. A workspace with no mapping for the ingredient —
    /// nothing proposed, nothing decided — answers `404 nutrition.mapping.not_found`, as it does for an ingredient
    /// another workspace has mapped.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<NutritionMappingServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid ingredientId, CancellationToken cancellationToken) =>
        Answer(await mappingFacade.GetAsync(ingredientId, cancellationToken));

    /// <summary>Confirms the reference the mapping holds: a proposal, or a newer version of the reference under review.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see <see cref="Get"/>.</param>
    /// <param name="ingredientId">The platform ingredient whose mapping to confirm.</param>
    /// <param name="model">The token of the mapping as read, and optionally the portion that turns volumes or counts into grams.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Takes an Editor, and is audited: a confirmed reference is what a published estimate rests on. Nothing is
    /// ever confirmed automatically, and this confirms exactly one mapping. `expectedConcurrencyToken` is required;
    /// one the mapping has moved past — another decision, or a refresh that put a newer version under review — is
    /// `409 nutrition.mapping.conflict`, so nobody confirms a version they did not see. Confirming a mapping with
    /// nothing to confirm (already confirmed, or unresolved) is `409 nutrition.mapping.state.conflict`.
    ///
    /// A `portion` replaces the mapping's own; it is recorded as the source's when it is exactly one of the
    /// reference's measured portions, and as this workspace's figure otherwise. A unit that is not a volume or count
    /// is `422 nutrition.mapping.portion.unprocessable`.
    ///
    /// Other refusals: `400 nutrition.mapping.invalid` (field errors), `400 request.malformed` (no body),
    /// `403 nutrition.mapping.forbidden` or a policy 403 below Editor, `404 nutrition.mapping.not_found`,
    /// `503 nutrition.mapping.source.unavailable` when the source cannot be asked and
    /// `503 nutrition.mapping.save.unavailable` when the store refused — nothing written either way, safe to send
    /// again. No idempotency key: the token makes a repeat a `409`, never a second write.
    /// </remarks>
    [HttpPost("confirm")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<NutritionMappingServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Confirm(
        string workspaceSlug,
        Guid ingredientId,
        [FromBody] ConfirmNutritionMappingViewModel model,
        CancellationToken cancellationToken) =>
        Answer(await mappingFacade.ConfirmAsync(ingredientId, model, cancellationToken));

    /// <summary>Turns down the reference the mapping holds, so no proposal offers it again, and leaves the mapping unresolved.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see <see cref="Get"/>.</param>
    /// <param name="ingredientId">The platform ingredient whose mapping's reference to reject.</param>
    /// <param name="model">The token of the mapping as read. Required.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Takes an Editor, and is audited. The rejection is remembered for this workspace: a later proposal never
    /// offers that reference, and remapping to it withdraws the rejection. A missing `expectedConcurrencyToken` is
    /// `400 nutrition.mapping.invalid` naming it — there is nothing to reject without a mapping the caller read — and
    /// a stale one is `409 nutrition.mapping.conflict`. An unresolved mapping has no reference to reject:
    /// `409 nutrition.mapping.state.conflict`. No mapping is `404 nutrition.mapping.not_found`.
    /// </remarks>
    [HttpPost("reject")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<NutritionMappingServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Reject(
        string workspaceSlug,
        Guid ingredientId,
        [FromBody] NutritionMappingDecisionViewModel model,
        CancellationToken cancellationToken) =>
        Answer(await mappingFacade.RejectAsync(ingredientId, model, cancellationToken));

    /// <summary>Points the mapping at a reference the creator chose, from any state or none, and confirms it.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see <see cref="Get"/>.</param>
    /// <param name="ingredientId">The platform ingredient to map.</param>
    /// <param name="model">The reference by source and record id, the token when a mapping exists, and optionally a portion.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Takes an Editor, and is audited. The creator's own choice overrides whatever was proposed and is confirmed as
    /// `CreatorSelected`. With no mapping yet, this makes the first one and needs no token; with one, the token is
    /// required, and a missing or stale one is `409 nutrition.mapping.conflict` — someone decided before you did.
    /// Either way the answer is `200` with the mapping: it acts on the ingredient's one mapping, which has no address
    /// of its own to send back.
    ///
    /// The reference must be approved evidence: a source other than the one approved for nutrition, a record the
    /// source does not have, or one from an unapproved data set is `422 nutrition.mapping.reference.unprocessable`,
    /// and a retired or unknown ingredient `422 nutrition.mapping.ingredient.unprocessable`. Remapping to a reference
    /// this workspace rejected withdraws the rejection. Other refusals as for confirm.
    /// </remarks>
    [HttpPost("remap")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<NutritionMappingServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Remap(
        string workspaceSlug,
        Guid ingredientId,
        [FromBody] RemapNutritionMappingViewModel model,
        CancellationToken cancellationToken) =>
        Answer(await mappingFacade.RemapAsync(ingredientId, model, cancellationToken));

    private IActionResult Answer(OperationResult<NutritionMappingServiceModel> result)
    {
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // A mapping carries the token the next decision must quote; a cached copy would hand out a stale one.
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
