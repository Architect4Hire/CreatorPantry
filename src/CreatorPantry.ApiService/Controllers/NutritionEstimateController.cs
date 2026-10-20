using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/versions/{versionNumber:int:min(1)}/analysis")]
public sealed class NutritionEstimateController(INutritionEstimateFacade estimateFacade) : ControllerBase
{
    /// <summary>
    /// Estimates one recipe version's nutrition from the workspace's confirmed ingredient mappings, whole-recipe and
    /// per serving, and says exactly what it could not count.
    /// </summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe. Constrained to a Guid, so a malformed id never reaches this action — routing answers 404, the
    /// same status as an unknown recipe and as one belonging to another workspace.
    /// </param>
    /// <param name="versionNumber">The version, by the number the history lists it under. Versions are immutable.</param>
    /// <param name="model">The yield basis: a serving count to divide by, or nothing to use the version's own.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **An estimate, and labelled as one.** `label` is always `Estimate`; `method` says in words how it was made
    /// and `methodVersion` names the rules. Nothing here is a measured, certified or regulatory value. The
    /// arithmetic is deterministic — the same version, mappings and source records give the same estimate — and no
    /// model computes any of it.
    ///
    /// **POST, but read-only.** It writes nothing, not even to a mapping it finds out of date, takes no idempotency
    /// key, and a successful answer is `no-store`: mappings and source records change. Every member including a
    /// Viewer may ask; only this workspace's recipe and this workspace's mappings are read.
    ///
    /// **The yield basis.** With `servings` omitted or null the version's own basis is used — its serving count,
    /// else its yield divided by its serving size, else none — and `servingsSource` says which. With none, every
    /// `perServing` is null and only whole-recipe figures are given; a per-100 g of finished dish is never
    /// invented, because its weight is unknown. A `servings` value divides by that instead.
    ///
    /// **Unresolved mappings stay explicit.** Every line has an `outcome` (`Included`, `Unmapped`, `NeedsReview`,
    /// `Unconvertible`, `NotQuantified`, `Optional`), a `reason` and whether it is `required`; `unresolved` groups
    /// the required lines that were not counted. `publishable` is true only when every required line is counted;
    /// otherwise `partial` is true, `status` is `Incomplete`, and the totals are a known-incomplete lower bound.
    /// Publish nothing from an estimate that is not publishable, and of one that is, only the nutrients whose
    /// `complete` is true: a nutrient a counted line's source does not report is unknown for that line — named in
    /// `linesMissingIt` — never zero.
    ///
    /// Each value is exact (`low`/`high`, six places; a range when a counted line was) and rounded once for
    /// display; `trace` marks a value that is not zero but rounds to zero — show "trace", never 0. `confidence` is
    /// the weakest way a counted line's grams were known. `sources` cites each source used, with its licence.
    ///
    /// Refusals: a missing or unparseable body is `400 request.malformed`, as on every route; a `servings` that is
    /// not above zero, above 10,000 or past four decimal places is `400 nutrition.estimate.invalid` naming it. An
    /// unknown recipe and another workspace's both answer `404 recipes.recipe.not_found`; a version number the
    /// recipe does not have answers `404 recipes.version.not_found`, naming `versionNumber`; a number below 1 matches no route and is a plain
    /// `404 not_found`. A total too large to
    /// state is `422 nutrition.estimate.quantities.unprocessable`. When the nutrition source cannot be asked — down,
    /// throttled, or not available on this installation — the answer is `503 nutrition.estimate.source.unavailable`
    /// and no estimate at all, rather than a smaller one that looks complete.
    /// </remarks>
    [HttpPost("nutrition")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<NutritionEstimateServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    // ValidationProblemDetails, as on the other version routes: an unknown version number names `versionNumber`.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Nutrition(
        string workspaceSlug,
        Guid recipeId,
        int versionNumber,
        [FromBody] NutritionEstimateRequestViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await estimateFacade.EstimateByNumberAsync(recipeId, versionNumber, model, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // Mappings and source records change; an estimate describes the moment it was made. Success path only.
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
