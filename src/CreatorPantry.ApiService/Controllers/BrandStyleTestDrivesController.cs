using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// 11A.24: try one version of a brand style guide out. Writes the same blog introduction, social caption and
/// image prompt twice — once with no brand guidance at all, once from the guide version named — so a creator can
/// see what their guide does before they rely on it.
/// </summary>
/// <remarks>
/// Not nested under a recipe, and not under the guide either: the subject is a brand guide version, but the
/// result belongs to the workspace's AI operations — a guide may be tried many times, and each attempt is
/// followed through this route by its own request id, like every other generation.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-style-test-drives")]
public sealed class BrandStyleTestDrivesController(IAiBrandStyleTestDriveFacade testDrives) : ControllerBase
{
    /// <summary>Asks for a test drive of one guide version. Returns 202 with the request to follow.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The guide, its version number, and optionally what the samples should be about.</param>
    /// <param name="idempotencyKey">
    /// Required. A test drive is two generations against the caller's allowance, so a retried request returns
    /// the first answer rather than buying a second pair; the same key with a different body is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, as for every other generation request. The work runs in the background: this
    /// answers `202` with a request id, and the `GET` below reports where it got to.
    ///
    /// **The version is required and is never defaulted.** Trying a draft out before activating it is the point
    /// of this route, so a request that named no version would be answered about a different guide than the one
    /// the creator is looking at. A guide or version that does not resolve answers `404` identically for every
    /// cause, including one belonging to another workspace.
    ///
    /// **A version with nothing written in it is `400`**, before any model is called. Both columns would come
    /// back the same, and a creator should not spend two generations to discover that.
    ///
    /// `subject` is optional and short: it is what the samples are about, and it reaches both halves unchanged,
    /// because a comparison whose subject moved between the two calls would be measuring the subject. Omitting
    /// it uses the platform's own example.
    ///
    /// **Nothing returned here is saved to the creator's content.** There is no acceptance route, no
    /// disposition, and no path from a stored sample to a guide, a recipe, a draft or a publication. A test
    /// drive is read and then it is history.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<BrandStyleTestDriveServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestBrandStyleTestDriveViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await testDrives.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/brand-style-test-drives/{accepted.RequestId}",
            accepted));
    }

    /// <summary>Reports where one test drive got to, and the comparison once there is one.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="requestId">The request to follow, as the `POST` returned it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. An unknown id, another workspace's request, and a request that ran a different task
    /// all answer `404` identically.
    ///
    /// `comparison` is null until the work has produced one, and when it failed. Once present it carries the
    /// three samples as pairs, the guide version that produced the right-hand column, and
    /// **`appliedRules`: what that guide asks for, in the creator's own words**, selected by the same rules the
    /// generation used — so the screen names the guidance behind the writing rather than asserting that it
    /// sounds more like them. `citations` names the passages of their own examples that were sent, as recorded
    /// at generation time.
    ///
    /// `appliedRules` describes the guide **as it reads now**, because which sections apply is recomputed from
    /// the stored version rather than copied. `groundingChangedSince` is `true` when the guide, the brand
    /// profile or the examples have moved since the samples were written, or when they cannot be read at all;
    /// the samples themselves are never re-derived.
    ///
    /// The response is `no-store`: it carries creator content and is personal to the caller's workspace.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandStyleTestDriveServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid requestId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await testDrives.GetAsync(requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
