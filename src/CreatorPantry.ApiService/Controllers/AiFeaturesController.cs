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
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/ai-features")]
public sealed class AiFeaturesController(IAiFeatureFacade features) : ControllerBase
{
    /// <summary>Returns which AI features are switched on for the workspace.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every feature a workspace can switch, always all of them and always in the same order, each with the
    /// `task` discriminator a request for it names. `enabled` is the answer a request for that task would get
    /// right now. `setByWorkspace` says where the answer came from: `true` once an Owner has saved a choice,
    /// `false` while the workspace is still on the site's default for that feature.
    ///
    /// Every member including a Viewer may read. The response is `no-store`: it is workspace-private and
    /// editable.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiFeaturesServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string workspaceSlug, CancellationToken cancellationToken)
    {
        var result = await features.GetAsync(cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Replaces which AI features are switched on for the workspace.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).
    /// </param>
    /// <param name="model">The features to switch on. Every switchable feature not named is switched off.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Owner only, and audited. One write for the whole set rather than a toggle per feature: `enabled` lists
    /// the `task` discriminators that are on, and every other switchable feature is off afterwards. An empty
    /// list switches all of them off. The write is idempotent — the same body leaves the same state.
    ///
    /// The workspace's choice is the whole answer once saved: a feature switched on here is accepted even
    /// where the site's default leaves it off, and one switched off here is refused even where the default
    /// has it on. What a request may then spend is bounded by the caller's AI allowance, not by this setting.
    /// A request already queued when its feature is switched off still runs.
    ///
    /// A name that is not a switchable feature, or one listed twice, answers `400 ai.features.invalid` with
    /// the offending entry under `errors`. Two Owners saving at the same moment is last-write-wins, except
    /// that a genuine collision answers `409 ai.features.conflict` and writes nothing.
    /// </remarks>
    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceOwner)]
    [ProducesResponseType<AiFeaturesServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Replace(
        string workspaceSlug, ReplaceAiFeaturesViewModel model, CancellationToken cancellationToken)
    {
        var result = await features.ReplaceAsync(model, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }
}
