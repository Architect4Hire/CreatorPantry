using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/readiness-transitions")]
public sealed class RecipeReadinessTransitionsController(
    IRecipeStatusTransitionFacade transitionFacade) : ControllerBase
{
    /// <summary>Moves a recipe to another editorial state and returns it as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe to move. Constrained to a Guid, so a malformed id never reaches this action — routing
    /// answers 404, the same status as an unknown recipe and as one belonging to another workspace.
    /// </param>
    /// <param name="model">The target state, the reason, and the token the request was composed against.</param>
    /// <param name="idempotencyKey">
    /// <strong>Required.</strong> A repeat of the same key and the same body returns the original response with
    /// <c>Idempotent-Replayed: true</c>; the same key with a different body is <c>422</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editorial state is a machine, and this is its only door (TESTRUN-005). An edit cannot change `status`,
    /// a version restore no longer puts one back, and the archive and restore commands run through the same
    /// rules — so a recipe's `readiness-transitions` history is a complete account of how it got where it is.
    ///
    /// **Which moves exist depends on where the recipe is now**, which is why the body carries no `fromStatus`
    /// and the route no list of legal targets. Forward movement is one step at a time —
    /// `Draft → InDevelopment → Testing → ReadyForReview → Approved` — so `Draft → Approved` is refused as an
    /// invalid jump with `400 recipes.transition.invalid_request`, and the answer names the states the recipe
    /// *could* have gone to. Reopening goes straight to `InDevelopment` from anywhere past it. `Archived` is
    /// reachable from every state and leaves only to `Draft`.
    ///
    /// **Roles differ per move.** Advancing is a Contributor's; approving, reopening, archiving and restoring
    /// are an Editor's. A move above the caller's role is `403 recipes.transition.forbidden`, which is
    /// deliberately distinct from the 400 above: one says nobody may do this from here, the other says you may
    /// not.
    ///
    /// **A reason is required to reopen** a recipe and optional on every other move — a reopen withdraws work
    /// somebody else advanced, and it is the one move a later reader cannot reconstruct from its two states.
    ///
    /// **Approving requires a readiness evaluation with no blockers, and the server makes it.** There is no
    /// field for one: a body that carried a verdict would be a client asserting its own recipe was ready.
    /// Blockers outstanding is `409 recipes.transition.blocked.conflict`, carrying the blocking rule ids in
    /// `errors.blockingRules` so a client can name them rather than send the approver back to guess. An
    /// approval also **writes a version** — the content as it stood, so the approval names words that cannot
    /// then change underneath it — and that version is the `currentVersion` of the response.
    ///
    /// `expectedConcurrencyToken` is required and is the `concurrencyToken` from the read this request was
    /// composed against; the readiness evaluation publishes the same token for exactly this. One the recipe
    /// has moved past is `409 recipes.recipe.conflict` rather than acting on work the caller has not seen, and
    /// an approval whose evaluation describes content that has since changed is refused the same way — an
    /// evaluation of words that have moved is not a fresh evaluation, whatever it said.
    ///
    /// **Asking for the state the recipe is already in succeeds and changes nothing**: no transition row, no
    /// audit entry, no stamped `updatedAt`. That is a repeat rather than a jump, it is the behaviour REC-006
    /// documented for archiving an archived recipe, and it is what keeps a replayed approval from writing a
    /// second version even under a fresh key.
    ///
    /// The response is the whole recipe, carrying its new `status` and the refreshed token the next write must
    /// quote. An unknown recipe and another workspace's recipe both answer `404 recipes.recipe.not_found`.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        Guid recipeId,
        RecipeReadinessTransitionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var outcome = await transitionFacade.TransitionAsync(
            userId, recipeId, model, idempotencyKey, cancellationToken);

        // 200 rather than 201 with a location, although this writes a transition row: the useful answer is the
        // recipe as it now stands, which is what the archive and restore commands already return, and after an
        // approval the version the approval wrote is this response's `currentVersion`. A location would have
        // to name a route that reads one transition, which nothing needs — a recipe's editorial history is read
        // as a history.
        return this.IdempotentResult(outcome, recipe => Ok(recipe));
    }
}
