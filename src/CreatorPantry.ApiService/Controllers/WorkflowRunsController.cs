using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Workflows.Facade;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>FLOW-002: a creator's runs through the guided workflows.</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/workflow-runs")]
public sealed class WorkflowRunsController(IWorkflowRunFacade runs) : ControllerBase
{
    /// <summary>Starts a run of one guided workflow.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The workflow, an optional name for the run, and an optional record to start from. It carries no version,
    /// owner, workspace or status; see <see cref="StartWorkflowRunViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. For 24 hours, a repeat of the same key and the same request returns the run the first one started,
    /// as it stood then, with `Idempotent-Replayed: true`; the same key with a different request answers
    /// `422 idempotency.key_reused`. A name that differs only by surrounding spaces is the same request. After the
    /// window the key starts a new run. A missing key answers `400 idempotency.key_required` and a malformed one
    /// `400 idempotency.key_invalid`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above; a Viewer is refused by the route with `403 forbidden`, and a workspace the caller is
    /// not in — or that does not exist — answers `404`. The run starts on the newest version of the workflow and
    /// belongs to the caller. It records where the creator is and does nothing else: the first step's work is not
    /// done for them, and no record is made in place of one that is missing.
    ///
    /// **The name.** Leave `title` out to use the workflow's goal; one sent blank is a `400`, and one sent is
    /// trimmed.
    ///
    /// **Starting from a record.** The record is checked through the module that owns it. One this workspace does
    /// not have answers `422 workflows.run.starting_resource.unprocessable` — the same answer another workspace's
    /// gets. A kind the workflow does not start from is a `400`. When the first step's whole job is to link a
    /// record of that kind, and another step remains, the run starts with that step done.
    ///
    /// **Refusals.** A workflow that cannot be taken yet answers `422 workflows.definition.unavailable.unprocessable`
    /// with its `reason`. A caller who cannot take the first action, nor any alternative to it, answers
    /// `403 workflows.run.role_insufficient.forbidden` naming the `neededRole`. Something the workflow needs
    /// before it starts answers `422 workflows.run.prerequisites_unmet.unprocessable` with `waitingFor`.
    ///
    /// **The answer** is the run, every step's state, its links, and `nextAction`: the step to do now, its
    /// actions with their workspace-relative `route`s, and anything it is still `waitingFor`. `Location` is the
    /// run's own address.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Start(
        string workspaceSlug,
        StartWorkflowRunViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await runs.StartAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, run => Created(
            $"/api/v1/workspaces/{workspaceSlug}/workflow-runs/{run.Id}", run));
    }

    /// <summary>Reads one run: where it stands, and how to carry on.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read every run in the workspace. Reading changes nothing — no date, no
    /// step, no `concurrencyToken`.
    ///
    /// **The version it started on.** A run is shown against the version of its workflow it was started on, never
    /// a newer one. While it is in progress and a newer one exists, `warnings` says so with `newer_version`; the run
    /// carries on as it began.
    ///
    /// **Carrying on.** `nextAction` is the step the run is on, its actions as the *reader* can take them
    /// (`youCanDoIt` uses the reader's role, so a Viewer's is always false), and anything it is still
    /// `waitingFor`. Once every step is done the run waits on its last one for finishing, and `nextAction` is null. `resumeRoute` is where "Continue" goes: where the creator last was, unless that names a record
    /// that is gone; else the first of the next step's actions that exists and has an address. Both are null once
    /// the run is completed or abandoned, and `resumeRoute` is null when the next step needs a record the run no
    /// longer holds.
    ///
    /// **Warnings** do not stop the run: `link_missing` for a record the run holds that can no longer be found —
    /// gone or never this workspace's, which read alike, and with no title; `draft_missing` for a step's saved draft
    /// that has gone the same way; `newer_version`; and
    /// `next_step_needs_role` when the reader cannot take the next action. Treat the codes as an open set and
    /// show `message` for one you do not know.
    ///
    /// An unknown run and another workspace's run both answer `404 workflows.run.not_found`. A `runId` that is not
    /// a GUID matches no route and answers the router's own `404 not_found`.
    /// </remarks>
    [HttpGet("{runId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid runId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.GetAsync(runId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Points the run's current step at the record its unfinished work is kept in.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run.</param>
    /// <param name="stepKey">The step, which must be the one the run is on.</param>
    /// <param name="model">The record's kind and id, and the run's `concurrencyToken` as last read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **A pointer, never the work.** Each step's work is saved by the screen it happens on, in a record that
    /// screen's module owns; this keeps only where to find it. The kinds a step takes are its actions' draft kinds —
    /// a kind it does not take is a `400`, and a record this workspace does not have answers
    /// `422 workflows.step.draft.unprocessable`, the same answer another workspace's gets.
    ///
    /// **Safe to repeat.** Saving the pointer the step already holds writes nothing and answers `200` with the
    /// token unchanged — whatever the token sent, and even once the run has moved on or finished — so a debounced
    /// client or a retry after a timeout always lands; no `Idempotency-Key` is needed. A token is still required on
    /// every save, and a missing or malformed one is a `400`. **Never over a newer edit:** any other change must send the token last read, and one sent against a
    /// run that has since changed answers `409 workflows.run.conflict` carrying `currentConcurrencyToken` and the
    /// draft the step holds now. Nothing is written then. A stale token is answered before the record is looked at,
    /// so it is a `409` whether or not the record exists.
    ///
    /// The first draft starts the step. The answer is the run as stored after the save — `draftSavedAt` on the
    /// step is the "Saved just now" — and is sent only once the save is committed.
    ///
    /// A finished run answers `409 workflows.run.not_in_progress.conflict`; a step that is not the current one,
    /// `409 workflows.step.not_current.conflict` with `currentStepKey`; the run's last step once it is done,
    /// `409 workflows.step.already_done.conflict`; a step the run's version does not have,
    /// `404 workflows.step.not_found`; an unknown run and another workspace's, `404 workflows.run.not_found`.
    /// </remarks>
    [HttpPut("{runId:guid}/steps/{stepKey}/draft")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> SaveDraft(
        string workspaceSlug,
        Guid runId,
        string stepKey,
        SaveWorkflowStepDraftViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.SaveDraftAsync(runId, stepKey, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Saves where the creator is on the run's current step, so they can leave and carry on later.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run.</param>
    /// <param name="stepKey">The step being left, which must be the one the run is on.</param>
    /// <param name="model">
    /// Optionally the step's draft and which of its actions the creator was in, and the run's `concurrencyToken`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **Leaving is not a status.** The run stays `InProgress` on the step it is on: nothing is completed, skipped
    /// or abandoned, and no step moves. `updatedAt` is when it was saved.
    ///
    /// **The draft.** Send `draft` when the step's last draft may not have reached the run — an autosave still on
    /// its way. It follows the draft save's rules exactly: a kind the step does not take is a `400`, and a record this
    /// workspace does not have answers `422 workflows.step.draft.unprocessable`, the same answer another workspace's
    /// gets. Leave it out to keep the draft the step holds; a save and exit never clears one.
    ///
    /// **Where to carry on.** Send `capabilityKey` — one of the step's actions, as `nextAction` lists them — and the
    /// server stores the route that action opens for this run, filled from the run's own records, as `lastRoute`.
    /// No address is taken from the client. `resumeRoute` then leads back there until the run moves to another step:
    /// completing, skipping or going back clears it. An action this step does not offer is a `400`; one not built
    /// yet, or whose page needs a record the run does not hold, answers `422 workflows.run.last_route.unprocessable`.
    /// Leave it out to keep the route the run holds.
    ///
    /// **Safe to repeat.** A save and exit whose draft and route the run already holds writes nothing and answers
    /// `200` as the run stands, whatever the token — and so does a duplicate that loses a race to the first. No
    /// `Idempotency-Key` is needed. A token is still required on every call, and a missing or malformed one is a
    /// `400`. Any other must send the token last read; a stale one answers `409 workflows.run.conflict` carrying
    /// `currentConcurrencyToken`, the draft the step holds now and the run's `lastRoute`, and nothing is written.
    ///
    /// **Which answer wins**, when more than one applies: a malformed request (`400`), the run (`404`), who may change
    /// it (`403`), the step (`404`), a finished run (`409`), a step that is not current (`409`), an action the step
    /// does not offer (`400`), an action with no page (`422`), nothing to change (`200`), a step already done or
    /// passed over (`409`), a draft of the wrong kind (`400`), a stale token (`409`), and a draft not found (`422`).
    ///
    /// The answer is the run as stored after the save, sent only once it is committed. Codes:
    /// `workflows.run.not_in_progress.conflict`, `workflows.step.not_current.conflict` (with `currentStepKey`),
    /// `workflows.step.already_done.conflict`, `workflows.step.not_found`, `workflows.run.not_found` — the same for
    /// an unknown run and another workspace's.
    /// </remarks>
    [HttpPost("{runId:guid}/steps/{stepKey}/save-and-exit")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> SaveAndExit(
        string workspaceSlug,
        Guid runId,
        string stepKey,
        SaveAndExitWorkflowRunViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.SaveAndExitAsync(runId, stepKey, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Completes the run's current step, once the server finds it done.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run.</param>
    /// <param name="stepKey">The step, which must be the one the run is on.</param>
    /// <param name="model">Which records the step made, if it makes any, and the run's `concurrencyToken`.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above, as for a draft.
    ///
    /// **Not a claim — a question.** The server checks the step's own rule against the modules that own the work: a
    /// fresh readiness evaluation, the recipe's or the piece's state as stored, whether the guide is switched on,
    /// whether a post was kept, the publishing ledger's own state (an outcome still uncertain never counts). Nothing
    /// here changes any of those, so no step completes that the domain would not already call done. When the rule is
    /// not met the answer is `422 workflows.step.incomplete.unprocessable` with `missing` — what stands in the way,
    /// in plain words — and nothing is written.
    ///
    /// **Results.** A step whose job is to make a record — start a recipe, keep a picture, file a piece — is done when
    /// it names that record: in `results`, or, left out, by the step's saved draft, or else by the record of that
    /// kind the run already holds (so a step reopened by Back completes again on what it made the first time). Each is checked first: one this
    /// workspace does not have answers `422 workflows.step.result.unprocessable`, the same answer another workspace's
    /// gets (and says so when it was the draft's), and a kind the step does not make is a `400`. Naming a record the
    /// run already holds makes it the run's latest of its kind again.
    ///
    /// **What it moves, together:** the step is completed, its results are linked to the run (once each), the run's
    /// progress counts it, the run moves on to the next step not yet done or passed over, and its `lastRoute` is
    /// cleared so "Continue" goes there. On the last one the run
    /// waits on the last step of its version and `nextAction` is null; finishing the run is a separate command. `readyToComplete` is true
    /// as soon as every *required* step is done — an optional step may still be open, and `nextAction` then offers
    /// it. The answer is the run as stored after the save.
    ///
    /// **Safe to repeat.** A step already complete answers `200` as the run stands, with nothing written, whatever
    /// results are sent and however far the run has moved since — and so does a duplicate that loses a race to the
    /// first. The request must still be well formed: a token is required on every call, and a missing or malformed
    /// one is a `400`. Any other completion must send the token last read; a stale one answers
    /// `409 workflows.run.conflict` with `currentConcurrencyToken`.
    ///
    /// **Which answer wins**, when more than one applies: the run (`404`), who may change it (`403`), the step
    /// (`404`), a step already complete (`200`), a finished run, a step that is not current, a step passed over (each
    /// `409` — the last is `workflows.step.already_done.conflict`, and Back reopens it), a result of the wrong kind
    /// (`400`), a stale token (`409`), a result not found (`422`), and the rule not met (`422`).
    ///
    /// A finished run answers `409 workflows.run.not_in_progress.conflict`; a step that is not the current one,
    /// `409 workflows.step.not_current.conflict` with `currentStepKey`; a step the run's version does not have,
    /// `404 workflows.step.not_found`; an unknown run and another workspace's, `404 workflows.run.not_found`.
    /// </remarks>
    [HttpPost("{runId:guid}/steps/{stepKey}/completion")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> CompleteStep(
        string workspaceSlug,
        Guid runId,
        string stepKey,
        CompleteWorkflowStepViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.CompleteStepAsync(runId, stepKey, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Goes back from the run's current step to the one before it, reopening that step.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run.</param>
    /// <param name="stepKey">The step being left, which must be the one the run is on.</param>
    /// <param name="model">The run's `concurrencyToken` as last read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **Back reopens the step before.** The run moves to the step immediately before `stepKey` in its version, and
    /// that step becomes open again: a completed step's completion, or a skipped step's skip, is cleared and taken
    /// off the run's counts, so it can be worked on and completed again — through the ordinary completion, which
    /// checks its rule afresh. Nothing goes forward this way, and no step after the current one is touched.
    ///
    /// **Nothing is lost.** The step being left keeps its state and its saved draft; the reopened step keeps its
    /// draft; no record the run holds is unlinked. `lastRoute` is cleared, so "Continue" goes to the reopened step
    /// rather than the page just left.
    ///
    /// **Safe to repeat.** Because `stepKey` names the step being left, a Back whose result already holds — the run
    /// is on the step before `stepKey`, and that step is open — answers `200` as the run stands with nothing written,
    /// whatever the token. That is so whether this Back put the run there or a request named a later step than the
    /// run had reached; either way it is where the Back would leave it. The one exception is a run finished since,
    /// which answers as a finished run. Any other Back must send the token last read; a stale one answers
    /// `409 workflows.run.conflict`. A token is required on every call, and a missing or malformed one is a `400`.
    ///
    /// **Which answer wins**, when more than one applies: a malformed request (`400`), the run (`404`), who may change
    /// it (`403`), the step (`404`), a finished run (`409`), a Back whose result already holds (`200`), a step that
    /// is not current (`409`), the first step (`409`), and a stale token (`409`).
    ///
    /// Back from the first step answers `409 workflows.run.at_first_step.conflict`; from a step that is not the
    /// current one, `409 workflows.step.not_current.conflict` with `currentStepKey`; on a finished run,
    /// `409 workflows.run.not_in_progress.conflict`; a step the run's version does not have,
    /// `404 workflows.step.not_found`; an unknown run and another workspace's, `404 workflows.run.not_found`.
    /// </remarks>
    [HttpPost("{runId:guid}/steps/{stepKey}/back")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Back(
        string workspaceSlug,
        Guid runId,
        string stepKey,
        WorkflowStepBackViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.BackAsync(runId, stepKey, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Passes over the run's current step, which its workflow must mark optional.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run.</param>
    /// <param name="stepKey">The step to pass over, which must be the one the run is on.</param>
    /// <param name="model">Why, optionally, in the creator's words, and the run's `concurrencyToken`.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **Optional steps only.** A step its workflow requires answers `422 workflows.step.required.unprocessable`.
    ///
    /// **A skip is never a result.** The step is marked passed over — when, by whom, and why if the creator said —
    /// and nothing more: no record is linked, nothing is completed, the step's saved draft stays a draft, and the run's
    /// completed count is untouched. The run moves on to the next step not yet done or passed over, or, with none,
    /// waits on its last step to be finished; its `lastRoute` is cleared, as for a completion. Back undoes a skip.
    ///
    /// **Safe to repeat.** A step already skipped answers `200` as the run stands with nothing written, whatever was
    /// sent and however far the run has moved since; so does a duplicate that loses a race to the first. A token is
    /// still required on every call, and a missing or malformed one — or a blank or over-long `reason` — is a `400`.
    ///
    /// **Which answer wins**, when more than one applies: a malformed request (`400`), the run (`404`), who may change
    /// it (`403`), the step (`404`), a step already skipped (`200`), a finished run, a step that is not current, a
    /// step already done (each `409`), a required step (`422`), and a stale token (`409 workflows.run.conflict` with
    /// `currentConcurrencyToken`). Codes: `workflows.run.not_in_progress.conflict`,
    /// `workflows.step.not_current.conflict` (with `currentStepKey`), `workflows.step.already_done.conflict`,
    /// `workflows.step.not_found`, `workflows.run.not_found` — the same for an unknown run and another workspace's.
    /// </remarks>
    [HttpPost("{runId:guid}/steps/{stepKey}/skip")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Skip(
        string workspaceSlug,
        Guid runId,
        string stepKey,
        SkipWorkflowStepViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.SkipAsync(runId, stepKey, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Finishes a run, once the server finds every required step done as things stand now.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run.</param>
    /// <param name="model">The run's `concurrencyToken` as last read — and nothing else: no progress, no ticks.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **Proved now, not remembered.** Every step the run's version requires must be completed, and its rule must
    /// still hold when this is asked: a record a step linked is checked again through the module that owns it, and
    /// every other rule — readiness, the recipe's or the piece's state, the guide switched on, a post kept, the
    /// publishing ledger — is evaluated afresh. A step completed on a recipe since un-approved, or a picture since
    /// deleted, is not done. When anything stands in the way the answer is
    /// `422 workflows.run.incomplete.unprocessable` with `incompleteSteps` — each step's `stepKey`, `title` and
    /// `missing`, in plain words — and nothing is written. Optional steps never stand in the way.
    ///
    /// **The run, and only the run.** The run becomes `Completed` with `completedAt`, and is on no step any more;
    /// `nextAction` and `resumeRoute` are null. No step, link or linked record is touched: an optional step left open
    /// stays open with its draft, and the recipe, piece or picture the run used is exactly as it was. The answer is the
    /// run as stored after the save.
    ///
    /// **Safe to repeat.** A run already completed answers `200` as it stands with nothing written, whatever the
    /// token — and so does a duplicate that loses a race to the first. A token is still required on every call, and a
    /// missing or malformed one is a `400`. Any other must send the token last read; a stale one answers
    /// `409 workflows.run.conflict` with `currentConcurrencyToken`.
    ///
    /// **Which answer wins**, when more than one applies: a malformed request (`400`), the run (`404`), who may change
    /// it (`403`), a run already completed (`200`), a run stopped (`409 workflows.run.not_in_progress.conflict`), a
    /// stale token (`409`), and something still to do (`422`). An unknown run and another workspace's answer
    /// `404 workflows.run.not_found` alike.
    /// </remarks>
    [HttpPost("{runId:guid}/completion")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> CompleteRun(
        string workspaceSlug,
        Guid runId,
        CompleteWorkflowRunViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.CompleteRunAsync(runId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Stops a run for good, keeping it whole and every record it linked as it is.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run to stop.</param>
    /// <param name="model">The confirmation, an optional reason, and the run's `concurrencyToken`.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **Confirmed, or nothing happens.** `confirmed` must be `true`; without it the answer is
    /// `400 workflows.run.abandon.invalid` and nothing changes. `reason` is optional, in the creator's own words, up to
    /// 500 characters; it is kept trimmed and returned as `abandonReason`. Leave it out rather than send it blank: a
    /// blank or over-long one is a `400`, as for a skip.
    ///
    /// **A stop, not a finish.** The run becomes `Abandoned` with `abandonedAt`; `nextAction` and `resumeRoute` are
    /// null. Nothing else in it changes: it keeps the step it was left on, its steps with their drafts and reasons, and
    /// every link, as the record of how far it got. No recipe, piece of content or picture it linked is touched, and
    /// the stop is recorded in the workspace's audit log in the same write. To try the journey again, start it over or
    /// start a new run.
    ///
    /// **Safe to repeat.** A run already stopped — this way or by a start over — answers `200` as it stands with
    /// nothing written, whatever the token, and keeps the reason it was first stopped with; so does a duplicate that
    /// loses a race to the first. A token is still required on every call, and a missing or malformed one is a `400`.
    /// Any other must send the token last read; a stale one answers `409 workflows.run.conflict` with
    /// `currentConcurrencyToken`.
    ///
    /// **Which answer wins**, when more than one applies: a malformed or unconfirmed request (`400`), the run (`404`),
    /// who may change it (`403`), a run already stopped (`200`), a run completed
    /// (`409 workflows.run.not_in_progress.conflict`), and a stale token (`409`). An unknown run and another
    /// workspace's answer `404 workflows.run.not_found` alike.
    /// </remarks>
    [HttpPost("{runId:guid}/abandonment")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Abandon(
        string workspaceSlug,
        Guid runId,
        AbandonWorkflowRunViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await runs.AbandonAsync(runId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Starts a run over: stops it, keeping it whole as history, and starts the same journey afresh.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the start route.</param>
    /// <param name="runId">The run to start over.</param>
    /// <param name="model">The confirmation, an optional new name, and the run's `concurrencyToken`.</param>
    /// <param name="idempotencyKey">
    /// Required. Start over creates a run: a repeat of the same key and the same request returns the run the first one
    /// started, with `Idempotent-Replayed: true`; the same key with a different request answers
    /// `422 idempotency.key_reused`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The run's owner, the person it is for, or an Editor and above; anyone else answers
    /// `403 workflows.run.forbidden`, and a Viewer is refused by the route with `403 forbidden`.
    ///
    /// **Confirmed, or nothing happens.** `confirmed` must be `true`; without it the answer is
    /// `400 workflows.run.restart.invalid` and nothing changes. Edits the creator has not saved live only in their
    /// browser, so they should be saved before this is asked.
    ///
    /// **Not a reset.** The old run is stopped — `Abandoned`, with `replacedByRunId` naming the new one — and kept whole:
    /// its steps, their reasons and drafts, and every record it linked stay as they were. No recipe, piece of content
    /// or picture is touched by either run.
    ///
    /// **The new run** starts as a start does, on the newest version of the same workflow. It keeps the old run's
    /// owner and the person it is for, and its name unless `title` is sent; and the record the old run started from,
    /// when the workflow still starts from that kind and the record is still there. The answer is `201` with the new
    /// run and its `Location`.
    ///
    /// **Never a third run.** A run already started over answers `200` with the run that replaced it, whatever key is
    /// sent and whichever token — though one must still be sent, and well formed. A finished run otherwise answers `409 workflows.run.not_in_progress.conflict`; a stale token,
    /// `409 workflows.run.conflict`; a caller who cannot take the new run's first action,
    /// `403 workflows.run.role_insufficient.forbidden`; an unknown run and another workspace's,
    /// `404 workflows.run.not_found`.
    /// </remarks>
    [HttpPost("{runId:guid}/restart")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<WorkflowRunServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Restart(
        string workspaceSlug,
        Guid runId,
        RestartWorkflowRunViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await runs.RestartAsync(userId, runId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, restart => restart.Started
            ? Created($"/api/v1/workspaces/{workspaceSlug}/workflow-runs/{restart.Run.Id}", restart.Run)
            : Ok(restart.Run));
    }
}
