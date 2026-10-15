using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Planning.Facade;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/work-tasks")]
public sealed class WorkTasksController(IWorkTaskFacade tasks) : ControllerBase
{
    /// <summary>Captures one piece of the caller's own work in the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The task. Carries no workspace, owner, assignee, status or place in the list.</param>
    /// <param name="idempotencyKey">Optional. Send one to make a retry safe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above; a Viewer is refused with `403`. **`title` is the
    /// only required field** — everything else is optional, and everything the request does not carry the
    /// server decides: the owner is the caller, the assignee is the owner, the status is always `Backlog`,
    /// and the task goes to the bottom of its list. The place is not published; the order a list arrives in
    /// is the order.
    ///
    /// **A capture lands in the week you are planning in.** With no `planningWeekStart`, the week holding
    /// `scheduledDate` is used; with neither, the week holding today in the workspace's scheduling zone. A
    /// week is named by its first day and **starts on a Monday**; any other day answers `400`. A day outside
    /// its week answers `400` too. A workspace that has set no scheduling zone gets a task in no week rather
    /// than one guessed for it — capture never fails for want of a setting.
    ///
    /// **A due date, an estimate and labels are not part of capturing.** Neither is a status, an assignee or
    /// a recurrence: those are the server's or a later edit's, and sending one is ignored rather than
    /// honoured.
    ///
    /// **Every linked record must be this workspace's own.** `recipeId`, `contentCardId`, `mediaAssetId` and
    /// `workflowRunId` are each checked through the record's owner before anything is written, and
    /// `workflowStepKey` must be a step of the run beside it. One that cannot be linked answers
    /// `422 planning.work_task.link.unprocessable` naming the field — **the same answer whether the record
    /// never existed, can no longer be used, or belongs to another workspace**, so a task cannot be used to
    /// ask what a neighbour owns. A step key with no run is a `400`.
    ///
    /// **Nothing is half-written.** `409 planning.work_task.conflict` means the write did not happen and the
    /// request is safe to send again as it stands.
    ///
    /// With an `Idempotency-Key`, a repeat of the same request returns the first answer with
    /// `Idempotent-Replayed: true` and creates nothing more; the same key with a different body is refused.
    /// Without a key, two identical requests are two tasks.
    ///
    /// Answers `201` with the task. **There is no `Location`**: a task has no read route of its own yet.
    /// The response is `no-store` — it is one person's private working list.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        CreateWorkTaskViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too.
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await tasks.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, created => StatusCode(StatusCodes.Status201Created, created));
    }

    /// <summary>Reads the caller's own day in the workspace named by the route.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="includeSuggestions">Ask for a few unplanned tasks worth a look. Off unless sent.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read, because what comes back is **the caller's own work and
    /// nobody else's**: every group is keyed on the caller's membership as the task's assignee, so another
    /// member's list is not something this route can be asked for. A task handed to the caller appears here
    /// even though someone else captured it, and names that person as its `owner`.
    ///
    /// **The groups do not overlap.** A task is placed by the first rule it matches: `completedToday`, then
    /// `overdue`, then `inProgress`, then `waiting`, then `scheduledToday`. So the day is not double-counted,
    /// and ticking one thing off does not make it disappear from two places. The precedence hides nothing —
    /// every row carries its own `status`, `dueDate` and `scheduledDate`, so a task in `overdue` still says
    /// it is in progress. A task finished on an earlier day is in none of them.
    ///
    /// **`date` and `timeZoneId` say what "today" meant**, and are always present, so a client never infers
    /// the day boundary. The zone is the workspace's own, from brand settings. `completedToday` is a range of
    /// instants — local midnight to the next, the end exclusive — so a day that springs forward is 23 hours
    /// long and nothing is counted twice across a fall-back. `scheduledDate` and `dueDate` are plain dates
    /// and are compared as written.
    ///
    /// **A workspace that has set no time zone answers `422 planning.today.time_zone_not_set.unprocessable`**
    /// rather than guessing a day boundary, because a guessed one does not fail visibly: it quietly files an
    /// evening's work under tomorrow. The remedy is brand settings, and the message says so.
    ///
    /// Each group holds at most 50, newest or most pressing first, with `...HasMore` true when there were
    /// others. There is no cursor: a day is bounded by what a person can do, and a group past 50 is a sign of
    /// something other than a plan.
    ///
    /// `suggested` is empty unless `includeSuggestions=true`. It offers at most three **unplanned** backlog
    /// tasks — never one already in a group above — each with a plain-language `reason`. **Offering one
    /// schedules nothing**: no day, no status, no place in the list is written by this route, which is a
    /// `GET` and writes nothing at all.
    ///
    /// An unknown workspace and another workspace's answer the same `404`. The response is `no-store`: it is
    /// one person's private working list.
    /// </remarks>
    [HttpGet("today")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<TodayWorkTasksServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Today(
        string workspaceSlug,
        [FromQuery] bool includeSuggestions = false,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await tasks.GetTodayAsync(includeSuggestions, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
