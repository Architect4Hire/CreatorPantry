using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Modules.Dashboard.Facade;
using CreatorPantry.Domain.Modules.Dashboard.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/dashboard")]
public sealed class DashboardController(IDashboardFacade dashboard) : ControllerBase
{
    /// <summary>Reads the workspace at a glance.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. **Read-only**: nothing is written, stored or cached, and every
    /// figure is asked of the part of the product that owns it at the moment of the request.
    ///
    /// **Each section answers on its own.** It has a `status` of `Ready`, with its `data`, or `Unavailable`,
    /// with `data` null, when its source could not be read just now. One section being unavailable does not
    /// stop the others, and the route still answers `200`.
    ///
    /// **Every list holds at most five**, whatever the workspace holds, and every count beside it is the whole
    /// count:
    ///
    /// - `recipesInProgress` — recipes in Draft, In development, Testing and Ready for review, each state
    ///   counted (zero included), and the five changed most recently.
    /// - `readinessBlockers` — the readiness evaluation, run for those five recipes and no others: the ones it
    ///   blocks, each with its blocker count and up to three blockers named. `evaluated` is how many were actually
    ///   checked and `unevaluated` how many could not be just now; neither is a claim about recipes outside the five.
    /// - `aiProposals` — AI proposals awaiting accept or reject: how many, and the five newest, with the
    ///   recipe's title where there is one.
    /// - `testFollowUps` — test issues nobody has resolved: how many at each severity, and five to look at
    ///   first — Blocking first, then the most recently tested.
    /// - `upcoming` — from `today` through the thirteenth day after it: cards planned for a day in that window
    ///   and posts scheduled to go out in it and still on their way, each counted, five of each listed soonest
    ///   first.
    /// - `board` — every column of the content board with its count, and the total.
    /// - `recentUploads` — the five pictures most recently added to the library and not removed. Names and
    ///   kinds only: never bytes, a file address or file details.
    /// - `publishing` — every publishing state with its count, and the five most recently changed records that
    ///   need attention (`Failed` or `NeedsAttention`).
    ///
    /// `today` is the workspace's own date, in its brand profile's time zone; `timeZoneId` names the zone, or
    /// is null when none is set and UTC was used. No provider id, post text, prompt or proposal wording is in
    /// the answer.
    ///
    /// An unknown workspace and another workspace's answer the same `404`. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<DashboardServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string workspaceSlug, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        return Ok(await dashboard.GetAsync(cancellationToken));
    }
}
