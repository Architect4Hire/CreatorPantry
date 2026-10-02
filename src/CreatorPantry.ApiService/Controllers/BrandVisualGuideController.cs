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
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-visual-guide")]
public sealed class BrandVisualGuideController(IBrandVisualGuideFacade guide) : ControllerBase
{
    /// <summary>
    /// Reads which visual style an image setup screen would use, and which references it could name — before
    /// anything is generated.
    /// </summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The image task. Carries no workspace and no guide.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Answers for the workspace's **active** guide only. `activeGuide` is null when none has
    /// been activated, which is an answer rather than an error; `hasVisualGuidance` is false when the active guide
    /// describes no look for this task.
    ///
    /// `styleLines` is the look in the creator's own words, one shortened line each; `negativeGuidance` is what the
    /// guide says to keep out of the picture. The guide's Do/Don't rules are never included — an image task does not
    /// receive them. `isStale` and `staleSourceCount` are reported, never acted on.
    ///
    /// `references` lists the workspace's active visual-reference documents (those typed as visual references or
    /// given the visual-direction purpose), each with `usable` — whether text from it would reach a generation —
    /// and a plain `unusableReason` when not. It is empty when no guide is active, because without one the server
    /// names no documents. A title, an id and a boolean: no file name, storage location, image byte or extracted
    /// text is ever returned. `referencesTruncated` is true when the library held more than are listed, and `referencesAvailable` is false when
    /// the library could not be read — an empty list is then "could not load", not "you have none".
    ///
    /// A task that is not grounded in the brand's look is refused with `400 ai.brandVisualGuide.invalid_request`.
    /// No model is called and nothing is embedded. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandVisualGuideServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, [FromQuery] BrandVisualGuideViewModel query, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await guide.GetAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
