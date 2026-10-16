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
    /// **An estimate and labels may come with a capture** (13A.31): `estimateMinutes` from 1 to 1440, and
    /// `labelIds` — at most 25, each named once — which must each be an active tag of this workspace, or the
    /// answer is `422 planning.work_task.label.unprocessable`, the same whether a tag never existed, was
    /// retired or is another workspace's. The task and its labels are one write. **A due date, a status, an
    /// assignee and a recurrence are not part of capturing**: those are the server's or a later edit's, and
    /// sending one is ignored rather than honoured.
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

    /// <summary>Edits one of the caller's own tasks. Only what the patch mentions changes.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="taskId">The task to edit.</param>
    /// <param name="model">The fields to change, and the token of the read they were composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, and only **the task's owner or the person doing it**: anyone else's task, an
    /// unknown one and another workspace's all answer the same `404 planning.work_task.not_found`.
    ///
    /// **A field left out is untouched; a field sent with a value is set; a field sent as `null` is cleared.**
    /// `title`, `priority` and `assigneeMembershipId` can be changed but not cleared. A `description` sent
    /// blank is cleared. `labelIds` is the whole set and replaces what the task carries; `null` or `[]`
    /// clears it.
    ///
    /// **An edit does not move, reorder or complete a task.** Sending `status` or `position` answers `400`
    /// naming it.
    ///
    /// **Days and weeks.** A `scheduledDate` sent alone brings its week with it. Clearing the day keeps the
    /// week, so the task joins that week's backlog. **A day is never dropped as a side effect**: changing
    /// `planningWeekStart` while the task keeps a day outside the new week answers `400` unless
    /// `scheduledDate` is sent in the same patch — a day in the new week, or `null`.
    ///
    /// **Links.** `recipeId`, `contentCardId`, `mediaAssetId`, `workflowRunId` and `workflowStepKey` can each
    /// be set, replaced or cleared, and none is unlinked unless its field is sent as `null`. Changing the run
    /// while the task names one of its steps answers `400` unless `workflowStepKey` is sent too. A newly
    /// linked record is checked exactly as on capture, and one that cannot be linked answers
    /// `422 planning.work_task.link.unprocessable` — the same whether it never existed, can no longer be used,
    /// or belongs to another workspace. A record the task already points at is not checked again. An edit
    /// never changes a linked record.
    ///
    /// **Labels** are the workspace's own tags. A newly added one must be an active tag of this workspace, or
    /// the answer is `422 planning.work_task.label.unprocessable`; one the task already carries stays after it
    /// is retired.
    ///
    /// **Handing a task on.** The owner may hand it to any active member who can keep work — Contributor and
    /// above — themselves included. The person doing it may hand it back to the owner and to nobody else.
    /// Anything else answers `422 planning.work_task.assignee.unprocessable`. A task handed on, or planned for
    /// another day, goes to the bottom of its new list.
    ///
    /// `expectedConcurrencyToken` is required. When the task has changed since that read the answer is
    /// `409 planning.work_task.stale.conflict` and **nothing is written** — keep what was typed, read the task
    /// again, and send again. A token that could never have been issued is a `400`. A patch that changes
    /// nothing writes nothing and returns the same token.
    ///
    /// Answers `200` with the task, its labels and its new `concurrencyToken`. The response is `no-store`.
    /// </remarks>
    [HttpPatch("{taskId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Update(
        string workspaceSlug,
        Guid taskId,
        UpdateWorkTaskViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await tasks.UpdateAsync(taskId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Moves one of the caller's own tasks to another of their lists, at a place in it.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="taskId">The task to move.</param>
    /// <param name="model">Where it should go, and the token of the read the move was composed against.</param>
    /// <param name="idempotencyKey">
    /// Optional. For 24 hours, a repeat of the same key and the same request returns the first answer with
    /// `Idempotent-Replayed: true` and moves nothing more; the same key with a different request answers
    /// `422 idempotency.key_reused`. Without a key a repeat cannot move twice either: it quotes a token the first
    /// move used, and answers `409`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, and only **the person doing the task** — a move rearranges their list, and not even
    /// the owner who handed it on rearranges someone else's. Anyone else's task, an unknown one and another
    /// workspace's all answer the same `404 planning.work_task.not_found`.
    ///
    /// **The lists are the planner's.** `status` is required. `Backlog` with a `scheduledDate` is that day's
    /// list, and the week follows the day. `Backlog` without one is the backlog of no day: in
    /// `planningWeekStart` when sent, in the week the task already has when not. `InProgress` and `Waiting` are
    /// one list each and **keep the task's day and week** — a task started on Monday is still Monday's — so
    /// sending a day or week with them answers `400`; changing the plan is an edit. `Done` is not a list a task
    /// is moved to: complete it. A finished task answers `409 planning.work_task.completed.conflict`: reopen it.
    /// A day and a week that disagree answer `400`.
    ///
    /// **A move is a change of list.** Naming the list the task is already in answers `400`: reorder it within
    /// its list instead.
    ///
    /// **Its place.** `beforeTaskId` names the task it goes immediately before, which must be in the target
    /// list; left out, it goes to the bottom. One that is not answers `422 planning.work_task.move.unprocessable`
    /// — the same whether that task does not exist, is in another list, is the task being moved, or is somebody
    /// else's. The order is deterministic: rank, then id.
    ///
    /// **The move is one write.** When two neighbours have no room between them the target list is re-spaced in
    /// its own order, in the same write; no other list is touched, though re-spacing can change the
    /// `concurrencyToken` of other tasks in that list, never what they say.
    ///
    /// `expectedConcurrencyToken` is required. `409 planning.work_task.stale.conflict` means the task — or a task
    /// that had to be re-spaced to make room — changed first, and **nothing was written**: read the list again
    /// and move it again. A token that could never have been issued is a `400`.
    ///
    /// A move changes only the task: it completes no workflow step and touches no linked record. Answers `200`
    /// with the task and its new `concurrencyToken`. The response is `no-store`.
    /// </remarks>
    [HttpPost("{taskId:guid}/move")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Move(
        string workspaceSlug,
        Guid taskId,
        MoveWorkTaskViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await tasks.MoveAsync(userId, taskId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, moved => Ok(moved));
    }

    /// <summary>Gives one of the caller's own tasks a new place in the list it is already in.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="taskId">The task to reorder.</param>
    /// <param name="model">Its new place, and the token of the read the reorder was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, and only **the person doing the task**: anyone else's task — the owner who handed it
    /// on included — an unknown one and another workspace's all answer the same `404 planning.work_task.not_found`.
    ///
    /// **Only its place changes**: never its status, day, week, links or labels. Its list is the one it is in —
    /// in progress, waiting, the not-started tasks of its day, or the not-started tasks of no day.
    ///
    /// `beforeTaskId` names the task it goes immediately before, which must be in the same list; left out, it goes
    /// to the bottom. The task itself, or one not in its list, answers `422 planning.work_task.move.unprocessable`
    /// — the same whatever that task turns out to be. The order is deterministic: rank, then id. When two
    /// neighbours have no room between them the list is re-spaced in its own order in the same write, which can
    /// change other tasks' `concurrencyToken` in that list, never what they say; no other list is touched.
    ///
    /// **A task already exactly there is left alone** and keeps its token. A finished task answers
    /// `409 planning.work_task.completed.conflict`: finished tasks read in the order they were finished.
    ///
    /// `expectedConcurrencyToken` is required. `409 planning.work_task.stale.conflict` means the task, or a task
    /// that had to be re-spaced, changed first, and **nothing was written**. A token that could never have been
    /// issued is a `400`. Answers `200` with the task and its `concurrencyToken`. The response is `no-store`.
    /// </remarks>
    [HttpPost("{taskId:guid}/reorder")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Reorder(
        string workspaceSlug,
        Guid taskId,
        ReorderWorkTaskViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await tasks.ReorderAsync(taskId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Finishes one of the caller's own tasks.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="taskId">The task to finish.</param>
    /// <param name="model">The token of the read this was composed against.</param>
    /// <param name="idempotencyKey">
    /// Optional. For 24 hours, a repeat of the same key and the same request returns the first answer with
    /// `Idempotent-Replayed: true` and changes nothing more; the same key with a different request answers
    /// `422 idempotency.key_reused`. Without a key a repeat cannot take effect twice either: it quotes a token the
    /// first used, and answers `409`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, and only **the person doing the task**: anyone else's task — the owner who handed it
    /// on included — an unknown one and another workspace's all answer the same `404 planning.work_task.not_found`.
    ///
    /// The task becomes `Done` and records when, by whom, and the status it was finished from. **Its day, week and
    /// place in its list are kept**, so reopening puts it back exactly where it stood. A task already finished
    /// answers `409 planning.work_task.completed.conflict`.
    ///
    /// **Nothing else finishes with it.** A workflow step the task is about is done only when the workflow's own
    /// rule is satisfied; no linked record is touched.
    ///
    /// `expectedConcurrencyToken` is required: `409 planning.work_task.stale.conflict` means the task changed first
    /// and nothing was written; a token that could never have been issued is a `400`. Answers `200` with the task
    /// and its new `concurrencyToken`. The response is `no-store`.
    /// </remarks>
    [HttpPost("{taskId:guid}/complete")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Complete(
        string workspaceSlug,
        Guid taskId,
        WorkTaskCompletionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await tasks.CompleteAsync(userId, taskId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, completed => Ok(completed));
    }

    /// <summary>Takes one of the caller's finished tasks up again, where it stood.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="taskId">The task to reopen.</param>
    /// <param name="model">The token of the read this was composed against.</param>
    /// <param name="idempotencyKey">
    /// Optional. For 24 hours, a repeat of the same key and the same request returns the first answer with
    /// `Idempotent-Replayed: true` and changes nothing more; the same key with a different request answers
    /// `422 idempotency.key_reused`. Without a key a repeat cannot take effect twice either: it quotes a token the
    /// first used, and answers `409`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, and only **the person doing the task**; anyone else's, an unknown one and another
    /// workspace's answer the same `404 planning.work_task.not_found`.
    ///
    /// The task goes back to **the status it was finished from**, on the day and week and in the place it had, and
    /// its record of when and by whom it was finished is cleared. A task that is not finished answers
    /// `409 planning.work_task.open.conflict`. A workflow step the task is about is not reopened with it.
    ///
    /// `expectedConcurrencyToken` is required, as for completing. Answers `200` with the task and its new
    /// `concurrencyToken`. The response is `no-store`.
    /// </remarks>
    [HttpPost("{taskId:guid}/reopen")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Reopen(
        string workspaceSlug,
        Guid taskId,
        WorkTaskCompletionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await tasks.ReopenAsync(userId, taskId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, reopened => Ok(reopened));
    }

    /// <summary>What "Add to my plan" would make for one workflow step. Writes nothing.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="query">The run and the step.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. Every field is a suggestion to show the creator before anything exists: the step's
    /// own title, a line naming the run, **no estimate** — a step carries none and an unknown is never offered as
    /// a number — no day, and the week holding today in the workspace's zone (none when no zone is set).
    /// `workflowRunRoute` is the run's address, relative to the workspace. `existingTask` is the task the caller
    /// already added for this step, when they have, so the screen can say so instead of offering it twice.
    ///
    /// A run this workspace does not have, or a step not in it, answers `422 planning.work_task.link.unprocessable`
    /// — one answer for a run that never existed and another workspace's. A run no longer in progress, or a step
    /// already done or passed over, answers `422 planning.work_task.step.unprocessable`: there is nothing left to
    /// plan. The response is `no-store`.
    /// </remarks>
    [HttpGet("from-workflow-step")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskStepSuggestionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> SuggestFromWorkflowStep(
        string workspaceSlug,
        [FromQuery] WorkTaskStepSuggestionViewModel query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await tasks.SuggestFromWorkflowStepAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>"Add to my plan": makes the caller's task for one workflow step, or answers with the one they have.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="model">The run and step, and anything the creator changed from the suggestion.</param>
    /// <param name="idempotencyKey">
    /// Optional. For 24 hours, a repeat of the same key and the same request returns the first answer — the same
    /// status and `created` — with `Idempotent-Replayed: true`; the same key with a different request answers
    /// `422 idempotency.key_reused`. Without a key a repeat still makes nothing more: it answers with the task
    /// already there.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **The task is made only here, on confirmation, for the one step named** — never for
    /// every step of a run. Only `workflowRunId` and `workflowStepKey` are required; anything left out takes the
    /// suggestion, so confirming it unchanged is one click. A `description` sent blank is none.
    ///
    /// **One task per person per step.** `201` with `created: true` when this request made it; `200` with
    /// `created: false` and the task the caller already added for this step. The store keeps it so, whatever
    /// arrives at once. A task pointed at the same step by hand is not that task and does not count.
    ///
    /// **The run is read, never written.** The task points at the run and step, and its `links.workflowRunRoute`
    /// leads back there; the run gains nothing and no step changes state. Finishing the task does not finish the
    /// step. Refusals are the suggestion route's. The response is `no-store`.
    /// </remarks>
    [HttpPost("from-workflow-step")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskFromStepServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<WorkTaskFromStepServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> AddFromWorkflowStep(
        string workspaceSlug,
        AddWorkTaskFromStepViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await tasks.AddFromWorkflowStepAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, added => added.Created ? StatusCode(StatusCodes.Status201Created, added) : Ok(added));
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
    /// `topThree` names the tasks the caller chose as today's top three (13A.32), by id and in order — each is
    /// still in its own group, and only one shown in a group is named. Empty until they choose; see
    /// `PUT top-three`.
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

    /// <summary>Chooses the caller's top three for today in the workspace named by the route.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="model">The chosen tasks, in order. The day is the server's.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **The whole set, replacing the last**: at most three `taskIds`, each once, first the one
    /// that matters most; an empty list clears the choice. A repeat writes what is already there, so there is no
    /// idempotency key. More than three, one named twice, or a missing list is a `400 planning.top_three.invalid`.
    ///
    /// **Only a task on the caller's day may be chosen**: theirs to do, and late, planned for today, in progress,
    /// waiting or finished today — exactly what `GET today` shows them. Anything else answers
    /// `422 planning.top_three.unprocessable`, **the same whether the task is planned for another day, somebody
    /// else's, another workspace's, or was never a task.**
    ///
    /// **Choosing changes no task**: no status, day, priority or place in a list. The choice is kept for today in the
    /// workspace's own zone, so tomorrow starts with none; a workspace with no zone set answers
    /// `422 planning.today.time_zone_not_set.unprocessable`. `GET today` returns it as `topThree`.
    ///
    /// `409 planning.top_three.conflict` means nothing was written and the request is safe to send again. Answers
    /// `200` with the day and the ids as they now stand. The response is `no-store`.
    /// </remarks>
    [HttpPut("top-three")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkTaskTopThreeServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> SetTopThree(
        string workspaceSlug,
        SetWorkTaskTopThreeViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await tasks.SetTopThreeAsync(model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads the caller's own week in the workspace named by the route.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the capture route.</param>
    /// <param name="query">The week to read, by the Monday it starts on. Leave it out for the current week.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read, because what comes back is **the caller's own work and
    /// nobody else's**: every group is keyed on the caller's membership as the task's assignee.
    ///
    /// **A week runs Monday to Sunday, everywhere.** `weekStart` is written `yyyy-MM-dd` and must be a Monday;
    /// any other day or format answers `400 planning.week.invalid`. The caller's language does not move the
    /// week. Left out, it is the week holding today in the workspace's own zone. `weekStart`, `weekEnd`,
    /// `today` and `timeZoneId` are always present, so a client never infers a boundary, and `isCurrentWeek`
    /// says whether this is the week holding today.
    ///
    /// **The groups do not overlap, and none is a content-board column.** `completed` is what was finished
    /// between this Monday's local midnight and the next, the end exclusive — so a week the clocks spring
    /// forward in is 167 hours long and nothing is counted twice across a fall-back. `inProgress` and
    /// `waiting` are the caller's now, whatever week they were planned for. Each of the seven `days` holds the
    /// not-started tasks planned for that date. `backlog` is planned for the week and no day of it;
    /// `unplanned` is planned for no week at all; `carriedOver` is not started and planned for an earlier
    /// week, oldest first, and only in the current week — empty in any other. A not-started task planned for a
    /// later week is in none of them.
    ///
    /// **Order is the caller's own arrangement**: the place each task was given, then its id, in every group
    /// but `completed`, which is newest first. Unlike `today`, priority does not reorder a week.
    ///
    /// **Totals count the whole group**, not the rows returned: `taskCount`, `estimatedTaskCount`, and
    /// `estimatedMinutes` — the sum of the estimates there are. A task with no estimate is never counted as
    /// nothing, so `estimatedMinutes` is null when no task in the group has one. `planned` adds up the seven
    /// days and `backlog`: the week's own work, and nothing nobody planned for it.
    ///
    /// Each group, and each day, holds at most 50 rows, with `hasMore` true when its `taskCount` is more.
    ///
    /// **A workspace that has set no time zone answers `422 planning.week.time_zone_not_set.unprocessable`**,
    /// even for a week named outright: the week's edges are local midnights, and a guessed zone would quietly
    /// move Sunday evening's work into the next week. The remedy is brand settings.
    ///
    /// An unknown workspace and another workspace's answer the same `404`. The response is `no-store`: it is
    /// one person's private working list.
    /// </remarks>
    [HttpGet("week")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<WeekWorkTasksServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Week(
        string workspaceSlug,
        [FromQuery] WeekWorkTasksViewModel query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await tasks.GetWeekAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
