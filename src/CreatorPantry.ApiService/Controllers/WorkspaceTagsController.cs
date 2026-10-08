using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The workspace's own tag vocabulary, as a picker reads it.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/tags")]
public sealed class WorkspaceTagsController(IRecipeFacade recipes) : ControllerBase
{
    /// <summary>Lists the workspace's tags that may be offered as new choices, by name.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Each item is an `id` to send and a `name` to show — the id is what a recipe's or a
    /// library asset's `tags` field takes.
    ///
    /// **Active tags only.** A retired tag stays named on whatever already carries it, through that record's
    /// own read, and is simply not offered here. A workspace with no tags yet answers an empty list, never a
    /// `404`.
    ///
    /// **Not paged, and capped at 500.** A picker filters the whole vocabulary as a creator types, and a
    /// workspace's own tags are a small set; past the cap the list is cut alphabetically rather than growing
    /// without limit. Ordered by name, case-insensitively.
    ///
    /// **Read-only.** A tag enters the vocabulary when a creator tags a recipe. The response is `no-store`:
    /// the vocabulary is the creator's own words.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<IReadOnlyList<WorkspaceTagServiceModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string workspaceSlug, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        return Ok(await recipes.ListWorkspaceTagsAsync(cancellationToken));
    }
}
