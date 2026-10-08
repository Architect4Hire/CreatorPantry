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

/// <summary>
/// Putting a library asset to use on a recipe, and taking it off again (RCPUB-005).
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/asset-links")]
public sealed class RecipeAssetLinksController(IRecipeAssetLinkFacade assetLinks) : ControllerBase
{
    /// <summary>Links one library asset to the recipe and returns the recipe as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to link to. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">The asset, its role, and the recipe's concurrency token. Carries no workspace and nothing about the asset itself.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same link returns the original response with
    /// `Idempotent-Replayed: true` rather than being answered as a conflict.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, the same bar as editing the recipe — **because this is an edit of it**. A recipe's
    /// links are part of what each version records, so linking writes one new version, requires the recipe's
    /// `expectedConcurrencyToken`, refreshes it, and reopens an approved recipe exactly as any other edit does.
    /// The response is the whole recipe in the shape a read returns, with the new link among its `assetLinks`.
    ///
    /// `role` is one of `Hero`, `Gallery`, `Process`, `Social` or `Step`. **A recipe has at most one `Hero`**:
    /// linking a second answers `409 recipes.assetLink.hero.conflict` rather than replacing the first. A `Step`
    /// image must name an `instructionStepId` of this same recipe, and no other role may name one. The same asset
    /// in the same role (and step) twice answers `409 recipes.assetLink.duplicate.conflict`; the same asset in two
    /// different roles is allowed. The new link goes to the end.
    ///
    /// `versionNumber` **pins the link to one version of the asset**; leave it out to follow whichever is current.
    /// A pin stays where it is when the asset gains newer versions.
    ///
    /// **Only an asset in this workspace's library can be linked.** An unknown asset, another workspace's, and
    /// one removed from the library all answer `422 recipes.assetLink.target.unprocessable` naming
    /// `mediaAssetId`, in the same words, so a link request cannot be used to ask what a neighbour owns. The
    /// same code names `versionNumber` for a version the asset does not have, and `instructionStepId` for a step
    /// that is not part of this recipe.
    ///
    /// **Nothing about the asset changes.** No bytes are read, copied or moved, and its usage history is not
    /// touched. A token the recipe has moved past answers `409 recipes.recipe.conflict`; an archived recipe
    /// answers `409 recipes.archived.conflict`.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Link(
        string workspaceSlug,
        Guid recipeId,
        LinkRecipeAssetViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await assetLinks.LinkAsync(userId, recipeId, model, idempotencyKey, cancellationToken);

        // 200 with the recipe, not 201 with a link: the resource this route changes is the recipe, the link
        // has no address of its own to put in a Location header, and the caller needs the recipe's refreshed
        // token before it can do anything else to it.
        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Removes one link from the recipe and returns the recipe as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to unlink from. Constrained to a Guid.</param>
    /// <param name="linkId">The link to remove — an `id` from the recipe's `assetLinks`, not an asset id.</param>
    /// <param name="model">The recipe's concurrency token. Carries nothing else.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same unlink returns the original response with
    /// `Idempotent-Replayed: true`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. Like linking, this is an edit of the recipe: it writes one new version, requires
    /// and refreshes the recipe's `expectedConcurrencyToken`, and reopens an approved recipe.
    ///
    /// **Unlinking removes the link and nothing else.** The asset stays in the library with every version, its
    /// tags, its usage history and its links to anything else exactly as they were; no bytes are deleted. The
    /// other links keep their positions.
    ///
    /// A link id this recipe does not have answers `404 recipes.assetLink.not_found` — the same answer for an id
    /// that names nothing, a link of another recipe, and a link in another workspace. **Without an
    /// `Idempotency-Key`, unlinking the same link twice answers that `404` the second time**, or
    /// `409 recipes.recipe.conflict` if the first call's token is quoted again; send a key if a retry should
    /// receive the original answer.
    /// </remarks>
    [HttpDelete("{linkId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Unlink(
        string workspaceSlug,
        Guid recipeId,
        Guid linkId,
        UnlinkRecipeAssetViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await assetLinks.UnlinkAsync(userId, recipeId, linkId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, Ok);
    }
}
