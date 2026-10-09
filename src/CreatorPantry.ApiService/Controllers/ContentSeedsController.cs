using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/content-seeds")]
public sealed class ContentSeedsController(IContentSeedFacade contentSeeds) : ControllerBase
{
    /// <summary>Returns one content idea for the workspace.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">
    /// Optional. A `token` to reproduce an earlier seed, and any facet the creator has already decided, given as
    /// that facet's stable key.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// A starting point for a piece of content, assembled from platform vocabulary — cuisine, dish type, method,
    /// photography style, channel, occasion — plus the workspace's own weekly theme for the chosen day. **Nothing
    /// is written and no model is called.** The seed is a suggestion, not a record: there is no seed to fetch
    /// later, and `GET` is the whole feature.
    ///
    /// `token` is what makes a seed reproducible. The response always carries the token that produced it; sending
    /// that token back returns the same seed. Reproducibility holds against the same catalogue state and the same
    /// week, not forever — retiring the entry a token selected changes that facet, as does rewriting the theme for
    /// the chosen day. Adding an entry usually changes nothing, because each candidate is scored independently
    /// rather than selected by position. A token of your own is fine: any run of up to 64 letters, digits, hyphens
    /// or underscores works, so `spring-bakes` is a valid seed.
    ///
    /// Any facet can be pinned and the rest are chosen around it. A pinned key that no catalogue has, or that
    /// names a retired entry, answers `400 content.seed.invalid` with a field error rather than quietly choosing
    /// something else. Facets are otherwise uniform over active entries — there is no popularity data to weight
    /// them by and inventing some would be a fabricated metric — with one exception: when the workspace's brand
    /// profile lists default channels, the channel is drawn from those, because the creator has already said where
    /// they publish.
    ///
    /// `recipeId` builds the idea around one of the workspace's own recipes, optionally at a pinned
    /// `recipeVersionId`. The recipe's cuisine, course and primary technique become the seed's cuisine, dish type
    /// and method, each marked `fromRecipe`; one the recipe leaves unset is absent rather than chosen at random,
    /// unless it was pinned. The description names the recipe by its title and the response echoes it as `recipe`.
    /// A recipe that does not exist, or belongs to another workspace, answers `400 content.seed.invalid` with a
    /// `recipeId` field error — the same answer for both.
    ///
    /// A facet can be absent from the response. A workspace with no theme on the chosen day is the normal case and
    /// yields a day with no theme, never an error. `method` carries `requiresSafetyCaution`: `true` means a client
    /// showing this seed must show an explicit caution, and `false` means only that no caution has been attached —
    /// it is never a statement that a technique is safe. Every member including a Viewer may read. The response is
    /// `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<ContentSeedServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug,
        [FromQuery] ContentSeedQueryViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await contentSeeds.GenerateAsync(query, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        // Personalised and deliberately different on every call that does not pin a token, so a shared cache must
        // not hold it (gateway.md).
        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
