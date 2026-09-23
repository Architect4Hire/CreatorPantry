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
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes")]
public sealed class RecipesController(IRecipeFacade recipeFacade) : ControllerBase
{
    /// <summary>Creates a recipe in the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace itself is resolved server-side from this
    /// segment and the caller's membership before the action runs; nothing here reads it, and no request
    /// field may name a workspace (tenancy.md).
    /// </param>
    /// <param name="model">The recipe to create. Carries no workspace, owner, version or audit field.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same recipe returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than creating a second recipe.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreatedRecipeServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        CreateRecipeViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await recipeFacade.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        // No mapping beyond choosing the response: the facade already returns the ServiceModel, and the
        // location is the only thing this layer contributes, because only it knows the route shape.
        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/recipes/{created.RecipeId}", created));
    }

    /// <summary>Reads one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe to read. Constrained to a Guid, so a malformed id never reaches this action: routing answers
    /// 404, the same status as an unknown recipe and as one belonging to another workspace. The problem body
    /// differs — an edge 404 carries the generic <c>not_found</c> code rather than this module's — so nothing
    /// is disclosed, but a client branching on <c>code</c> sees two codes for what is one condition to it.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("{recipeId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid recipeId, CancellationToken cancellationToken)
    {
        var result = await recipeFacade.GetDetailAsync(recipeId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Changes part of one recipe and returns it as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to change. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">
    /// The fields to change. Fields the body does not mention are left alone, and a field sent as
    /// <c>null</c> is cleared — see <see cref="UpdateRecipeViewModel"/>. The body carries the recipe's
    /// concurrency token and no workspace, owner, version or audit field.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same edit returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than being answered as a conflict.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// This is a JSON Merge Patch, not a replacement. A field the body does not mention is left exactly as
    /// it is; a field sent with a value is set to it; a field sent as `null` is cleared. Omitting a field
    /// never blanks it. `tags` is the one field that replaces rather than merges — a submitted list becomes
    /// the recipe's complete set, and `[]` or `null` removes them all. `title` and `status` may be changed
    /// but not cleared. `expectedConcurrencyToken` is required and is not a field of the recipe: it is the
    /// `concurrencyToken` from the read this edit was composed against, and an edit quoting a token the
    /// recipe has moved past is refused with `409 recipes.recipe.conflict` rather than overwriting whoever
    /// saved first. `reason` is likewise not a recipe field; it is recorded on the version this edit writes.
    /// An edit that would change nothing writes no version and leaves the token valid. The response is the
    /// whole recipe, in the same shape a read returns, so an editor can rebind from it: it carries the
    /// refreshed token the next edit must quote, and the version this one wrote.
    /// </remarks>
    [HttpPatch("{recipeId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Update(
        string workspaceSlug,
        Guid recipeId,
        UpdateRecipeViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await recipeFacade.UpdateAsync(userId, recipeId, model, idempotencyKey, cancellationToken);

        // No location to contribute and no mapping to do: the facade already returns the ServiceModel, and
        // the recipe is where it always was.
        return this.IdempotentResult(outcome, Ok);
    }
}
