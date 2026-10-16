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
    /// **Two sections are yours, not the workspace's.** `workflows` and `workPlan` are keyed on your own
    /// membership — the runs you started or were handed, and your own tasks — so two members of one workspace are
    /// answered differently. Everything else reads the same for both. Within `workflows`, the count of runs that
    /// need a look is the workspace's runs as your role sees them, exactly as the workflow hub's list is.
    ///
    /// **Every list holds at most five** — three for the two sections above — whatever the workspace holds, and
    /// every count beside it is the whole count:
    ///
    /// - `workflows` — your guided workflows: how many are in progress, the three to carry on with (most
    ///   recently touched first, each naming the step it is on and whether that step is yours to take), how many
    ///   runs need a look, and the three that have waited longest, each with its reasons (`idle`,
    ///   `newer_version`, `not_yours_to_do`, `version_gone`). A run is opened at `workflows/runs/{id}`, which resumes it on the
    ///   step it is on; no draft, reason or linked record is in the answer.
    /// - `workPlan` — your own day and week: how many tasks are late and the first three, how many are planned
    ///   for today and the first three, which of those you chose as today's top three, what the week holding
    ///   today adds up to (planned, estimated where anyone estimated, under way, held up, finished), and how many
    ///   tasks have been captured and not yet planned. A task's title is your own words; no description, label or
    ///   link is in the answer. The groups do not overlap: a task both late and planned for today is counted as
    ///   late and nowhere else.
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
    /// `today` is the workspace's own date, in its brand profile's time zone, and `timeZoneId` names that zone.
    /// Without a usable one both are null and every section that depends on a day — `upcoming` and `workPlan` —
    /// is `Unavailable`, because answering in UTC would be wrong by up to a day at either end. `workflows` and
    /// the rest still answer. `timeZoneState` says why there is no day, which is not one case but three: `Set`,
    /// `NotSet` (read, and the workspace has none — the creator sets one in Brand settings) and `Unreadable`
    /// (the profile would not read, or names a zone this server does not know, so nothing is claimed about
    /// whether one is set). A client must not send a creator off to set a zone on `Unreadable`: it is very
    /// likely already set. No provider id, post text, prompt or proposal wording is in the answer.
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
