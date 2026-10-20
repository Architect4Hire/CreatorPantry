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
public sealed class FoodSafetyWarningsController(IFoodSafetyWarningFacade warningFacade) : ControllerBase
{
    /// <summary>
    /// Reads one recipe version's own text and choices for a fixed set of high-risk methods and claims, and says
    /// what each one needs checked.
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
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Not a safety verdict, in either direction.** `disclaimer` is on every answer, an empty one most of all:
    /// finding nothing is not a statement that a recipe is safe, and a warning is not a ruling that it is unsafe.
    /// No message says what a hazard is or what a reader must do — each would be a claim this product has no
    /// approved reference to make (B-33 approves no food-safety source, so every warning carries
    /// `verificationRequired` with its reason).
    ///
    /// **What it reads:** the recipe's own words — title, description, headnote, notes, storage notes, ingredient
    /// lines and their preparation notes, steps and their notes — plus its structured technique and equipment
    /// choices. A stated internal temperature is recorded as written and never called too low, because no
    /// approved minimum exists to call it against. `unrecognisedTechniques` counts the techniques named that are
    /// not in the active vocabulary, so an empty `warnings` list read from less than the whole record says so.
    ///
    /// **GET, and read-only.** It writes nothing, takes no body, and answers `no-store`: the technique vocabulary
    /// and the ingredient categories it reads change as they are curated. Every member including a Viewer may ask.
    ///
    /// Refusals: an unknown recipe and another workspace's both answer `404 recipes.recipe.not_found`; a version
    /// number the recipe does not have answers `404 recipes.version.not_found`, naming `versionNumber`; a number
    /// below 1 matches no route and is a plain `404 not_found`.
    /// </remarks>
    [HttpGet("food-safety")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<FoodSafetyWarningsServiceModel>(StatusCodes.Status200OK)]
    // ValidationProblemDetails, as on the sibling analysis routes: an unknown version number names
    // `versionNumber` in `errors`, which is the only thing that tells it apart from recipes.recipe.not_found.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> FoodSafety(
        string workspaceSlug,
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        var result = await warningFacade.DetectByNumberAsync(recipeId, versionNumber, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // The answer describes the vocabulary and categories as they stood when read. Success path only.
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
