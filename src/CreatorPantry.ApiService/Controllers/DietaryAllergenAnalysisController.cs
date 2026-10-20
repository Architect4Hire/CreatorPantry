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
public sealed class DietaryAllergenAnalysisController(IDietaryAllergenAnalysisFacade analysisFacade) : ControllerBase
{
    /// <summary>
    /// Reads one recipe version's approved ingredient records against the allergens and dietary profiles chosen,
    /// and returns what they say, what they do not, and where each word came from.
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
    /// <param name="model">The allergens and dietary profiles to read the records against, by catalogue id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **A reading of records, never a verdict about food or a person.** Nothing here says a recipe is safe, free
    /// from anything, certified or suitable for anyone, and `crossContactDisclaimer` is on every answer, the best
    /// one included. Findings come only from approved traits in effect on `evaluatedOn` (UTC); no model, no
    /// ingredient category and no line's own words decide one. Missing data is `Unknown`, never absent.
    ///
    /// **POST, but read-only.** The selection travels in the body because forty ids do not belong in a URL. It
    /// writes nothing, takes no idempotency key, and a successful answer is `no-store`: the records change as they
    /// are reviewed, so an answer describes the moment it was read. Every member including a Viewer may ask.
    ///
    /// Per allergen and per profile: `finding` is the most cautious of its lines, and `statement` says it in one
    /// fixed sentence. `confidence` says how complete the records behind it are — `Recorded` (every line has a
    /// record that counts, no disagreement), `Partial` (some lines have none), `Disputed` (approved sources
    /// disagree; the more cautious word was taken and both are cited) or `None` — and `coverage` gives the counts.
    /// It describes the records, not how likely the finding is: `Contains` on `Partial` records still contains.
    /// `unknownLines` and each line's `reason` say what could not be read; `possibleSources` and
    /// `dependsOnProductLines` are the hidden sources and cross-contact the sources themselves state.
    ///
    /// Each line's `evidence` cites the source, the date its statement took effect and its own note; `counted` is
    /// false for a reassuring word from a source that may not give one (an unvetted kind, or a retired source),
    /// which is cited so it can be seen and never moves a finding. `sources` lists every source cited, once, with
    /// its licence and the latest effective date read — a trait record has no version of its own, so a source and
    /// that date are what a reader checks it against. `methodVersion` names the decision table.
    ///
    /// Refusals: a missing or unparseable body is `400 request.malformed`, as on every route. A selection that
    /// cannot be read — nothing chosen, more than twenty of either, an empty or repeated id — is
    /// `400 nutrition.analysis.invalid` with field errors; an allergen or profile the catalogue does not
    /// offer, or has retired, is `422 nutrition.analysis.subject.unprocessable`. An unknown recipe and another
    /// workspace's both answer `404 recipes.recipe.not_found`; a version number the recipe does not have answers
    /// `404 recipes.version.not_found`, naming `versionNumber`; a number below 1 matches no route and is a plain
    /// `404 not_found`.
    /// </remarks>
    [HttpPost("dietary-allergen")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<DietaryAllergenAnalysisServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    // ValidationProblemDetails, as on the version comparison and restore routes: an unknown version number names
    // `versionNumber` in `errors`, which is the only thing that tells it apart from recipes.recipe.not_found.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> DietaryAllergen(
        string workspaceSlug,
        Guid recipeId,
        int versionNumber,
        [FromBody] DietaryAllergenSelectionViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await analysisFacade.AnalyzeByNumberAsync(recipeId, versionNumber, model, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // The answer describes the records as they stood when read. Success path only; ProblemFor owns refusals.
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
