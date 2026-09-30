using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/readiness")]
public sealed class RecipeReadinessController(IRecipeReadinessFacade readinessFacade) : ControllerBase
{
    /// <summary>Evaluates one recipe against the readiness rules and returns every rule's verdict.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe to evaluate. Constrained to a Guid, so a malformed id never reaches this action — routing
    /// answers 404, the same status as an unknown recipe and as one belonging to another workspace.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Deterministic, and no model decides any of it** (TESTRUN-004). Every verdict comes out of a comparison
    /// against a column or a count; a model may have written some of the material being judged — a proposal's
    /// warnings are among the facts — but what those facts mean for approval is settled by code, the same way
    /// every time. The same recipe evaluated twice with nothing changed in between answers identically.
    ///
    /// **It writes nothing.** No state changes, no audit row, no cache entry: this is a read, and a readiness
    /// verdict stored anywhere would be a second source of truth that goes stale on the next edit. `no-store`
    /// for the same reason — the answer describes the recipe at the moment it was read.
    ///
    /// **It is not a status and must not be stored as one.** The recipe's editorial state is `status` on the
    /// recipe itself. `evaluatedVersionNumber` and `concurrencyToken` say which moment this describes, so a
    /// client holding both can tell whether the answer still describes the recipe it is showing and re-fetch
    /// when they diverge. A screen that cached `hasBlockers` as a badge would be showing a verdict about
    /// content that has since changed.
    ///
    /// **It is not a safety, allergen, nutrition or dietary clearance**, and no combination of findings makes
    /// it one. The nearest rule, `recipe.allergens.traitUnreviewed`, reports how complete the allergen
    /// *records* are and never what is in the dish — and no absence of findings here means a recipe is safe
    /// for anyone.
    ///
    /// `findings` carries **every rule that ran, satisfied ones included**, because the thing it feeds is a
    /// checklist: a creator needs to see what they have cleared as much as what they have not. There are no
    /// query parameters — no filtering, no paging — and a client that wants only the blockers filters on
    /// `status`. `ruleId` is the stable identifier to branch on; `summary` and `detail` are sentences for a
    /// reader and may be reworded. `detail` and `evidence` are populated on an unmet rule and empty on a
    /// satisfied or inapplicable one, and `evidence` is what names the exact record and field behind a
    /// finding — `label` quotes the creator's own words where the evidence has any. `recordId` is null where
    /// the finding is about a record that does not exist, which is what "this recipe has no version to have
    /// tested" looks like; `fieldName` is null where the finding is about the record's existence rather than
    /// one of its fields.
    ///
    /// `status` distinguishes four answers, not two: `NotApplicable` means the rule did not apply rather than
    /// that it passed — the attribution rule against a recipe citing no source, say. `disabledRuleIds` lists
    /// rules configuration switched off, which are **absent from `findings` entirely** rather than reported as
    /// satisfied, because a rule that did not run has not passed; `unknownConfiguredRuleIds` lists ids
    /// configuration named that the catalogue does not have, so a typo is discoverable rather than silent.
    /// `ruleSetVersion` is the catalogue's own version, so a result stays interpretable after the rules move.
    ///
    /// A recipe with no content at all is evaluated rather than refused: it answers `200` with blockers, which
    /// is what a creator who has just started typing must see. An unknown recipe and another workspace's
    /// recipe both answer `404 recipes.recipe.not_found`. Every member including a Viewer may read this —
    /// every fact it surfaces is already readable through the test history, a proposal or the ingredient
    /// tools.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeReadinessServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug,
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        var result = await readinessFacade.EvaluateAsync(recipeId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // The answer describes one moment and nothing may serve it again as though it were current. Set on the
        // success path only: a 404 carries nothing worth caching either way, and ProblemFor owns that response.
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
