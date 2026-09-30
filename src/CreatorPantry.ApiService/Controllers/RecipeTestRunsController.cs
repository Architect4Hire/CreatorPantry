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
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/test-runs")]
public sealed class RecipeTestRunsController(IRecipeTestRunFacade testRunFacade) : ControllerBase
{
    /// <summary>Records one cook of one exact version of a recipe.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it beyond building the location
    /// (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe under test. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">The test as it happened. Carries no workspace, tester, or audit field.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same test returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than recording a second run.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// `sourceVersionNumber` is **required** and is a version number as the history lists it, not a version
    /// id: a test is evidence about an exact set of words, so there is no "latest" to fall back on. Requires
    /// the Contributor role, the same bar as creating a recipe — recording a test adds the creator's own
    /// material and changes no canonical content. **Nothing here writes to the recipe**: every `actual` figure
    /// sits on the test beside what the version claims, never over it, and reconciling the two is an ordinary
    /// `PATCH` afterwards. There is **no `expectedConcurrencyToken` and no `409` for staleness** — nothing is
    /// being overwritten, which makes this one of the two recipe-area writes with no state to quote. The body
    /// carries **no attachments**: linking media needs something that can authorize an asset against this
    /// workspace, and that arrives with the media library. An unknown recipe and another workspace's recipe
    /// both answer `404 recipes.recipe.not_found`; a version number the recipe does not have answers
    /// `404 recipes.version.not_found`, naming `sourceVersionNumber`; an archived recipe answers
    /// `409 recipes.archived.conflict`, and the remedy is to bring it back rather than to retry. A `testedAt`
    /// in the future is refused — testers record the past. Issues point at this request's own observations by
    /// position through `observationIndex`, and the response returns both sets of ids in submitted order so a
    /// client can resolve an issue later without listing the test back.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreatedRecipeTestRunServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    // ValidationProblemDetails, as on the duplicate and restore routes: an unknown source version names
    // `sourceVersionNumber` in `errors`, and that naming is the only reason this 404 is worth telling apart
    // from recipes.recipe.not_found. A client cannot be asked to depend on a field the contract omits.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        Guid recipeId,
        CreateRecipeTestRunViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await testRunFacade.CreateAsync(userId, recipeId, model, idempotencyKey, cancellationToken);

        // No mapping beyond choosing the response: the facade already returns the ServiceModel, and the
        // location is the only thing this layer contributes, because only it knows the route shape.
        //
        // The location names the run's own resource even though no GET serves that path yet — the read seam
        // arrives with the test history. A created resource has an address whether or not it can currently be
        // fetched from it, and adding the header later would be a visible change to a shipped contract.
        return this.IdempotentResult(outcome, created =>
            Created(
                $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/test-runs/{created.TestRunId}",
                created));
    }

    /// <summary>Changes part of one recorded test and returns it as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe the test belongs to. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="testRunId">The test to change.</param>
    /// <param name="model">
    /// The fields to change. Fields the body does not mention are left alone, and a field sent as <c>null</c> is
    /// cleared — see <see cref="UpdateRecipeTestRunViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same edit returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than being answered as a conflict.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// A JSON Merge Patch, not a replacement: a field the body does not mention is left exactly as it is, a
    /// field sent with a value is set to it, and a field sent as `null` is cleared. `observations` and `issues`
    /// are the two fields that **replace** rather than merge — a submitted list becomes the test's complete set,
    /// entries carrying an `id` this test owns are updated in place, entries without one are added, anything
    /// omitted is removed, and `[]` clears them. **There is no `sourceVersionNumber`**: a test is evidence about
    /// the version that was cooked, and repointing it would be a claim that something else happened. An issue
    /// pointing at an observation by `observationIndex` may only do so when `observations` is sent in the same
    /// request, because the positions mean that list. **An issue that has been resolved cannot be dropped** by
    /// omitting it — `409 recipes.testIssue.removal.conflict` — because a resolution records a decision and
    /// removing the issue would erase it. `expectedConcurrencyToken` is required and is the `concurrencyToken`
    /// from the read this edit was composed against; one the test has moved past is refused with
    /// `409 recipes.testRun.conflict` rather than overwriting whoever saved first. **Nothing here writes to the
    /// recipe.** An unknown test, another workspace's, and one belonging to a different recipe all answer
    /// `404 recipes.testRun.not_found`; an archived recipe answers `409 recipes.archived.conflict`. The response
    /// is the whole test, carrying the refreshed token the next edit must quote.
    /// </remarks>
    [HttpPatch("{testRunId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<RecipeTestRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Update(
        string workspaceSlug,
        Guid recipeId,
        Guid testRunId,
        UpdateRecipeTestRunViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await testRunFacade.UpdateAsync(
            userId, recipeId, testRunId, model, idempotencyKey, cancellationToken);

        // No location to contribute and no mapping to do: the facade already returns the ServiceModel, and the
        // test is where it always was.
        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Records what was done about one issue a test found.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe the test belongs to.</param>
    /// <param name="testRunId">The test that found the issue.</param>
    /// <param name="issueId">The issue being resolved.</param>
    /// <param name="model">How it was dealt with, and optionally the version carrying the correction.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same resolution returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than reporting the issue as already resolved.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// A separate command from the edit above, and deliberately so. It writes exactly one row and **rewrites
    /// neither the issue nor the observation it came from** — the read that judges it does not even track them.
    /// Requires the Editor role, a step above recording a test: reporting a problem and deciding it is closed
    /// are different acts, and this decision is immutable once written. There is **no
    /// `expectedConcurrencyToken`** because nothing is being overwritten; the conflict this route has instead is
    /// `409 recipes.testIssue.resolved.conflict`, and retrying will never change it. `resolutionVersionNumber`
    /// is a version number of **this recipe** — the database enforces that, not only this seam — and must be
    /// **later than the version that was tested**, because a version written before the problem was found cannot
    /// contain its fix; naming an earlier one needs `predatingVersionOverrideReason` to say why, which is then
    /// recorded. Only a `Fixed` resolution may name a version at all. An unknown test or issue answers
    /// `404`, and a version this recipe does not have answers `404 recipes.version.not_found` naming the field.
    /// </remarks>
    [HttpPost("{testRunId:guid}/issues/{issueId:guid}/resolution")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<ResolvedTestIssueServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> ResolveIssue(
        string workspaceSlug,
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        ResolveTestIssueViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await testRunFacade.ResolveIssueAsync(
            userId, recipeId, testRunId, issueId, model, idempotencyKey, cancellationToken);

        // 201 and a location: this creates a resolution, and the resource it creates is addressable — at most
        // one per issue, so the issue's own resolution path names it without an id of its own.
        return this.IdempotentResult(outcome, resolved =>
            Created(
                $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/test-runs/{testRunId}"
                    + $"/issues/{resolved.TestIssueId}/resolution",
                resolved));
    }
}
