using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-writing-guide")]
public sealed class BrandWritingGuideController(IBrandWritingGuideFacade guide) : ControllerBase
{
    /// <summary>
    /// Reads which brand guide a writing screen would use, and what it would ask for — before anything is
    /// generated.
    /// </summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The writing task, and optionally the channel. Carries no workspace and no guide.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Answers for the workspace's **active** guide only — the "use my brand voice"
    /// default; naming another guide is not offered. `activeGuide` is null when the workspace has activated
    /// none, which is an answer rather than an error.
    ///
    /// `isStale` is true when any source the active version cites has since been replaced
    /// (`staleSourceCount` says how many, and is the version history's own figure). The guide is still the
    /// active one — staleness is reported, never acted on: this route changes nothing.
    ///
    /// `rules` is what the guide would contribute to this task and channel, by the same selection rules
    /// generation uses, in the creator's own words shortened to a line each. It contains no prompt text,
    /// retrieved passage or section key. A task that is grounded in no brand context is refused with `400
    /// ai.brandWritingGuide.invalid_request` rather than answered as though it were. A channel the catalogue
    /// does not know is refused the same way.
    ///
    /// No model is called and no passages are retrieved. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandWritingGuideServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, [FromQuery] BrandWritingGuideViewModel query, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await guide.GetAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
