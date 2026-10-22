using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// LF.2.2: a long-form draft — a blog article or a newsletter issue — and the plan a creator approves before
/// any of it is written.
/// </summary>
/// <remarks>
/// Two facades, because the work is two modules': asking for an outline spends the account's allowance and is
/// the AI module's, and the draft and its plan are creator content and the content module's. Each action calls
/// exactly one.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/long-form-drafts")]
public sealed class LongFormDraftsController(
    IAiLongFormOutlineRequestFacade outlines,
    ILongFormDraftFacade drafts,
    IAiLongFormSectionRequestFacade writing,
    ILongFormSectionFacade sections,
    ILongFormDocumentFacade documents) : ControllerBase
{
    /// <summary>Lists the workspace's long-form drafts, most recently updated first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="query">The page to read and, optionally, a kind or a creative context to keep to.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer. Each row names a draft, what kind it is, where it stands and the
    /// number of its latest revision — and no word of any plan.
    ///
    /// Read until `nextCursor` is null. A cursor is bound to this workspace and to the filters it was issued
    /// under: one from another workspace, or replayed with a different `kind` or `creativeContextId`, answers
    /// `400 content.long_form.cursor.invalid`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<CursorPageServiceModel<LongFormDraftSummaryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> List(
        string workspaceSlug, [FromQuery] LongFormDraftListViewModel query, CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too.
        Response.Headers.CacheControl = "no-store";

        var result = await drafts.ListRecentAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Starts a long-form draft for one piece of creative work and asks for its outline.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="model">
    /// The piece of work, the kind of piece, and optionally a length and the sections the plan must include.
    /// It cannot carry a heading, a body, a prompt, a model or a workspace; see
    /// <see cref="StartLongFormDraftViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Planning a piece spends the account's allowance, so a repeat of the same key and the same
    /// request returns the original draft with `Idempotent-Replayed: true` rather than starting a second.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, the bar for spending the workspace's allowance. `202` rather than `201`: the draft
    /// exists, but its plan does not yet — the model is called by the worker afterwards, so the API does not
    /// stay open around provider work (api-contract.md). The `Location` header is the draft; poll
    /// `outline-requests/{request.aiProposalRequestId}` under it for the plan.
    ///
    /// **The key names the request.** The same key with the same piece of work, kind, length and wanted
    /// sections is a retry and returns the same draft; the same key with any of those changed answers
    /// `422 idempotency.key_reused`.
    ///
    /// **An exhausted allowance is refused here, before any provider is called and before any draft is made**:
    /// `429 ai.quota.exhausted` carrying what remains and a `Retry-After`, or `403 ai.quota.suspended` when AI
    /// is switched off for the account.
    ///
    /// A piece of work this workspace does not have answers `422 content.long_form.context.unprocessable` —
    /// the same answer, in the same words, that another workspace's context gets. A wanted section this kind
    /// of piece does not have, or one asked for more often than it may appear, is a `400`.
    ///
    /// **What comes back is a plan to review.** The outline lands as the draft's first revision and the draft
    /// stays `Draft` until a creator approves it. No prose is written by this request.
    /// </remarks>
    [OperationLimit(LimitedOperation.AiRequest)]
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<StartedLongFormDraftServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Start(
        string workspaceSlug,
        StartLongFormDraftViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var outcome = await outlines.StartAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, started => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/long-form-drafts/{started.Draft.Id}", started));
    }

    /// <summary>Reads one long-form draft with its latest plan.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The draft to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer. `latest` is null until an outline has landed. `planCheck` says whether
    /// the latest plan could be approved as it stands and, if not, which sections stand in the way — a plan
    /// may be saved before it is ready. `concurrencyToken` is what the next write must send back.
    ///
    /// An unknown draft and another workspace's draft both answer `404 content.long_form.draft.not_found`.
    /// </remarks>
    [HttpGet("{draftId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<LongFormDraftServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid draftId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await drafts.GetAsync(draftId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Asks for another outline for a draft that already exists.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The draft to plan again.</param>
    /// <param name="model">Optionally a length and the sections the plan must include.</param>
    /// <param name="idempotencyKey">Required, for the reason given on the route that starts a draft.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. For a draft whose outline failed or was cancelled, or whose plan the creator
    /// wants proposed afresh. The new outline lands as the draft's next revision — earlier revisions are kept —
    /// and an approved plan goes back to `Draft`, because the new plan is one nobody has approved.
    ///
    /// Once a draft's sections are being written its plan is closed:
    /// `409 content.long_form.plan.locked.conflict`. The allowance, key and wanted-section rules are those of
    /// the route that starts a draft.
    /// </remarks>
    [OperationLimit(LimitedOperation.AiRequest)]
    [HttpPost("{draftId:guid}/outline-requests")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<StartedLongFormDraftServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> RequestOutline(
        string workspaceSlug,
        Guid draftId,
        RequestLongFormOutlineViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var outcome = await outlines.RequestAgainAsync(draftId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, started => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/long-form-drafts/{draftId}/outline-requests/{started.Request.AiProposalRequestId}",
            started));
    }

    /// <summary>Where an outline request has got to, and the draft as it stands.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The draft the request was made for.</param>
    /// <param name="requestId">The request's id, as the route that made it returned.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer, and this route writes nothing: the plan becomes a revision in the
    /// worker, straight after the proposal commits, so a poll is never what stores a creator's content.
    ///
    /// A cancelled or failed run answers `200` with the request's status and `failureCategory` — `Cancelled`
    /// when the run was cancelled — rather than an error: the request was accepted, and what became of it is
    /// the answer. The draft then still has whatever plan it had before, or none.
    ///
    /// An unknown request, another workspace's, one that ran a different task and one made for a different
    /// draft all answer `404 content.long_form.outline_request.not_found`.
    /// </remarks>
    [HttpGet("{draftId:guid}/outline-requests/{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<LongFormOutlineRequestStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetOutlineRequest(
        string workspaceSlug, Guid draftId, Guid requestId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await outlines.GetAsync(draftId, requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Saves the creator's own plan as a new revision of the draft.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The draft whose plan to change.</param>
    /// <param name="model">The whole plan in order, and the token of the read it was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. Send every section in the order the piece should read: a section left out is
    /// removed, one without an `id` is added, and one with an `id` keeps it. Nothing is edited in place — the
    /// plan before this one is still the revision it was.
    ///
    /// **Saving is not approving.** A plan that is not yet valid — a heading still blank, a role used twice
    /// while sections are rearranged — is saved, and `planCheck` on the response says what stands in the way.
    /// What is refused here is only what could not be kept: `400 content.long_form.plan.invalid` for a role
    /// that is not a role, an `id` that is not a section of this plan, a `sourceReferenceId` on a section
    /// that is not an item or that names a recipe this plan was not written for, or a different
    /// `sourceReferenceId` on an item the plan already has.
    ///
    /// **A newsletter item is the recipe it covers.** An item already in the plan keeps its `sourceReferenceId`.
    /// Leaving an item out takes its recipe out of the issue: the new revision no longer covers it, and the
    /// revision before is unchanged.
    ///
    /// Changing an approved plan returns the draft to `Draft`, so the changed plan must be approved again.
    /// Saving the plan exactly as it stands changes nothing and creates no revision.
    ///
    /// `expectedConcurrencyToken` is required and a stale one is `409 content.long_form.draft.stale.conflict`.
    /// A draft with no plan yet is `409 content.long_form.plan.not_started.conflict`; one whose sections are
    /// being written is `409 content.long_form.plan.locked.conflict`.
    /// </remarks>
    [HttpPatch("{draftId:guid}/plan")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormDraftServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> PatchPlan(
        string workspaceSlug, Guid draftId, PatchLongFormPlanViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await drafts.PatchPlanAsync(userId, draftId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Approves the draft's latest plan, so its sections may be written.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The draft whose plan to approve.</param>
    /// <param name="model">The revision being approved, and the token of the read it was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. The draft moves from `Draft` to `Outlined`. Nothing is written, generated or
    /// spent by approving.
    ///
    /// **The approval names the revision that was read.** If the plan has been edited since, the revision is
    /// no longer the latest and the answer is `409 content.long_form.draft.stale.conflict`.
    ///
    /// **Approving twice is one approval.** A repeat for the revision that is already approved returns the
    /// draft as it stands, whatever token it carries, and records nothing.
    ///
    /// A plan that does not meet the long-form shape rules answers `422 content.long_form.plan.unprocessable`
    /// with one error for each failing section, keyed `sections[n]` by its position in the plan. For a
    /// newsletter issue those rules include one item for each recipe the plan was written for, each naming a
    /// different one; a recipe with no item is reported against `sections`.
    /// </remarks>
    [HttpPost("{draftId:guid}/plan/approval")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormDraftServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> ApprovePlan(
        string workspaceSlug, Guid draftId, ApproveLongFormPlanViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await drafts.ApprovePlanAsync(userId, draftId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Puts a newsletter issue's items in a new order, named by the recipe each covers.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The issue whose items to order.</param>
    /// <param name="model">Every item's `sourceReferenceId`, once each, in the order the items should read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. The items change places among themselves; every other section stays where it is,
    /// and each item moves whole, with its heading, text and settled mark. Saved as a new revision. No text
    /// changes, so this works while the plan is open and while the issue is being written, and it leaves the
    /// draft's status as it was. The order already in force changes nothing and creates no revision.
    ///
    /// `400 content.long_form.item_order.invalid` unless the list names each of the issue's items exactly once.
    /// `409 content.long_form.draft.not_an_issue.conflict` for a piece that is not a newsletter issue;
    /// `409` also for a stale `expectedConcurrencyToken`, a draft with no plan yet, and an accepted draft.
    /// </remarks>
    [HttpPut("{draftId:guid}/items/order")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormDraftServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> ReorderItems(
        string workspaceSlug, Guid draftId, ReorderLongFormItemsViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await drafts.ReorderItemsAsync(userId, draftId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Saves a newsletter issue's subject line and the creator's note.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="draftId">The issue to frame.</param>
    /// <param name="model">The subject line and the note, each as the creator wrote it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above. Both are the creator's own words: nothing generates either, and neither is sent
    /// anywhere by saving it. Send both each time — a blank or omitted one is cleared. Saved as a new revision,
    /// which leaves the plan and its approval as they were; the same framing again changes nothing.
    ///
    /// The subject is one line of at most 150 characters and the note at most 600; neither counts towards the
    /// issue's length. Outside that is `400 content.long_form.framing.invalid`.
    /// `409 content.long_form.draft.not_an_issue.conflict` for a piece that is not a newsletter issue; `409`
    /// also for a stale `expectedConcurrencyToken`, a draft with no plan yet, and an accepted draft.
    /// </remarks>
    [HttpPut("{draftId:guid}/framing")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormDraftServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> PutFraming(
        string workspaceSlug, Guid draftId, PutLongFormFramingViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await drafts.PutFramingAsync(userId, draftId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>The draft's sections with their text, and where the writing of each stands.</summary>
    /// <remarks>
    /// Each section carries its own text, whether it is settled, how long it is and whether that is over the
    /// section ceiling. `requests` holds the latest writing request for each section that has one, with an
    /// `outcome`: `Waiting`, `Written`, `Superseded` (it finished, and its text is not the section's text now)
    /// or `Failed`. A read only: nothing is written or spent by asking.
    /// </remarks>
    [HttpGet("{draftId:guid}/sections")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<LongFormSectionsStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetSections(string workspaceSlug, Guid draftId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await writing.GetSectionsAsync(draftId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Asks for one section to be written, or written again.</summary>
    /// <remarks>
    /// Requires an `Idempotency-Key`. The same key for a different section, revision or instruction is refused
    /// with `422`. The section is written only if its text has not changed since `revisionNumber`; other
    /// sections changing does not matter. `409` when the draft is not approved or is finished, when the section
    /// is settled, or when its text has changed. The first request closes the draft's plan. Writing a section
    /// never changes another.
    /// </remarks>
    [OperationLimit(LimitedOperation.AiRequest)]
    [HttpPost("{draftId:guid}/sections/{sectionId:guid}/writing-requests")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormSectionRequestedServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> RequestSection(
        string workspaceSlug,
        Guid draftId,
        Guid sectionId,
        RequestLongFormSectionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var outcome = await writing.RequestAsync(draftId, sectionId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, requested => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/long-form-drafts/{draftId}/sections", requested));
    }

    /// <summary>Asks for every section not yet written to be written, one request to a section.</summary>
    /// <remarks>
    /// Requires an `Idempotency-Key`. Queues, in plan order, each section that has no text, is not settled and
    /// is not already being written. All or none: when the AI allowance cannot cover every one of them the
    /// answer is `429` with `sectionsRequested` and `sectionsCoverable`, and nothing is queued. `requests` is
    /// empty when there was nothing to write. Each section's run still holds its own allowance when it starts.
    /// </remarks>
    [OperationLimit(LimitedOperation.AiRequest)]
    [HttpPost("{draftId:guid}/section-writing-batches")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormSectionBatchServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> RequestRemainingSections(
        string workspaceSlug,
        Guid draftId,
        RequestLongFormSectionBatchViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var outcome = await writing.RequestRestAsync(draftId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, batch => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/long-form-drafts/{draftId}/sections", batch));
    }

    /// <summary>Saves the creator's own text for one section, as a new revision of the draft.</summary>
    /// <remarks>
    /// Every other section is carried over unchanged. Blank text makes the section not yet written again. Text
    /// over the section ceiling is saved and reported as over it, never refused or trimmed. An edit clears the
    /// section's settled mark and closes the draft's plan. `409` when `expectedConcurrencyToken` is not the
    /// draft's, or the draft is not approved or is finished.
    /// </remarks>
    [HttpPatch("{draftId:guid}/sections/{sectionId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormSectionsServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> EditSection(
        string workspaceSlug, Guid draftId, Guid sectionId, EditLongFormSectionViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await sections.EditSectionAsync(userId, draftId, sectionId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Marks one section as settled, or clears the mark.</summary>
    /// <remarks>
    /// Settled says the creator is content with a section's text for now. It is reversible, changes no text,
    /// and is not acceptance — that is of the whole document. A settled section is left alone by "write the
    /// rest" and refuses to be written again until the mark is cleared. `409` for a section with no text.
    /// </remarks>
    [HttpPost("{draftId:guid}/sections/{sectionId:guid}/settlement")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormSectionsServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> SettleSection(
        string workspaceSlug, Guid draftId, Guid sectionId, SettleLongFormSectionViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await sections.SettleSectionAsync(userId, draftId, sectionId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>The draft read whole: the document a creator would be accepting, and what is true of it.</summary>
    /// <remarks>
    /// `document.markdown` is every written section in plan order under the creator's own headings; the same
    /// revision always gives the same text, and an unwritten section is left out of it. `document.sections`
    /// lists every section of the plan, written or not, with its own text and length. `document.needsConfirmation`
    /// is what an acceptance would have to confirm. `warnings` are what was found about each generated section
    /// as it reads now — `code` is the server's finding, and `fromModel` marks a caution the model raised about
    /// its own work. `requests` is the latest writing request for each section. A read only.
    /// </remarks>
    [HttpGet("{draftId:guid}/document")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<LongFormDocumentReviewServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetDocument(string workspaceSlug, Guid draftId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await writing.GetDocumentAsync(draftId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Accepts the draft's latest revision as the article of record.</summary>
    /// <remarks>
    /// Acceptance is of the whole document. It writes no new revision, and accepting the revision already
    /// accepted returns the same answer and records nothing more. A document with an unwritten section or text
    /// over a ceiling is accepted only when `confirmed` lists exactly the issues it has; otherwise the answer is `422`
    /// with an `issues` extension naming each issue and its sections, and nothing is accepted. `422` also when
    /// no section has any text. `409` when `expectedConcurrencyToken` or `revisionNumber` is not the draft's
    /// latest, or the draft is not being written. Nothing here publishes, schedules or exports.
    /// </remarks>
    [HttpPost("{draftId:guid}/acceptance")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<LongFormAssembledDocumentServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> AcceptDocument(
        string workspaceSlug, Guid draftId, AcceptLongFormDocumentViewModel model, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await documents.AcceptAsync(userId, draftId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
