using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiLongFormOutlineRequestBusiness
{
    /// <summary>Starts a draft for a piece of work and asks for its outline.</summary>
    Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> StartAsync(
        StartLongFormDraftViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Asks for another outline for a draft that already exists.</summary>
    Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> RequestAgainAsync(
        Guid draftId, RequestLongFormOutlineViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>One outline request's status and the draft it is for.</summary>
    Task<OperationResult<LongFormOutlineRequestStatusServiceModel>> GetAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around asking for a long-form outline (LF.2.2): that the task is enabled and affordable, that the
/// piece of work is this workspace's, and that the draft is one a new plan may still land on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The request is queued before the draft is made, and names it.</strong> Queueing is what is
/// idempotent on the key, so it goes first, and the draft is then created under the request's own id. A retry
/// of the same key gets the same request back, finds the draft already there, and creates nothing — and a
/// crash between the two steps is healed by the retry, or by the plan itself when it lands.
/// </para>
/// <para>
/// <strong>What it does not do.</strong> It does not assemble the grounding package, read the brand guide or
/// look at a recipe: that is the handler's at the moment it calls the provider, and doing it here would only
/// let it go stale between the request and the call.
/// </para>
/// </remarks>
internal sealed class AiLongFormOutlineRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    ICreativeContextFacade contexts,
    ILongFormDraftFacade drafts,
    ILongFormPlanFacade plans,
    IWorkspaceContext workspace,
    IAiTaskGate tasks,
    IClock clock) : IAiLongFormOutlineRequestBusiness
{
    public async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> StartAsync(
        StartLongFormDraftViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var kind = model.Kind!.Value;

        if (await RefuseBeforeQueueingAsync(kind, model, cancellationToken) is { } refused)
        {
            return refused;
        }

        // Through the content module's facade, inside the resolved workspace. One answer for a context that
        // does not exist and for one that is another workspace's: the facade cannot tell them apart, and this
        // seam does not try to.
        if (!(await contexts.GetAsync(model.CreativeContextId, cancellationToken)).Succeeded)
        {
            return Refuse(
                ContentErrorCodes.LongFormContextUnprocessable,
                "That piece of work cannot be used for a long-form draft.");
        }

        var queued = await QueueAsync(
            Inputs(draftId: null, model.CreativeContextId, kind, model), idempotencyKey, cancellationToken);

        if (queued.Operation is not { } operation)
        {
            return KeyReused();
        }

        // The draft takes the request's id. Created if this is the first time through and found if it is a
        // retry; either way it is the same draft.
        var draft = await drafts.EnsureStartedAsync(operation.Id, model.CreativeContextId, kind, cancellationToken);

        return draft.Succeeded
            ? Started(draft.Value!, operation, queued.Outcome)
            : new IdempotentOutcome<StartedLongFormDraftServiceModel>(
                OperationResult<StartedLongFormDraftServiceModel>.Failure(draft.Error!), Replayed: false);
    }

    public async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> RequestAgainAsync(
        Guid draftId, RequestLongFormOutlineViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var found = await drafts.GetAsync(draftId, cancellationToken);

        // The content module's own 404, for an unknown draft and another workspace's alike.
        if (!found.Succeeded)
        {
            return new IdempotentOutcome<StartedLongFormDraftServiceModel>(
                OperationResult<StartedLongFormDraftServiceModel>.Failure(found.Error!), Replayed: false);
        }

        var draft = found.Value!;

        // A plan lands only while the plan is still open. Refused here, before the allowance is touched, rather
        // than paid for and then left unlanded.
        if (draft.Status is not (LongFormDraftStatus.Draft or LongFormDraftStatus.Outlined))
        {
            return Refuse(
                ContentErrorCodes.LongFormPlanLocked,
                "This draft's sections are already being written, so its plan can no longer be changed.");
        }

        if (await RefuseBeforeQueueingAsync(draft.Kind, model, cancellationToken) is { } refused)
        {
            return refused;
        }

        var queued = await QueueAsync(
            Inputs(draft.Id, draft.CreativeContextId, draft.Kind, model), idempotencyKey, cancellationToken);

        return queued.Operation is { } operation ? Started(draft, operation, queued.Outcome) : KeyReused();
    }

    public async Task<OperationResult<LongFormOutlineRequestStatusServiceModel>> GetAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // Four conditions, one absence: no such request, one in another workspace (the query filter removed it
        // before this read saw it), one that ran a different task, and one that was for a different draft.
        if (operation is null
            || operation.Operation.TaskType != AiTaskType.LongFormOutline
            || LongFormOutlineInputs.ReadDraftId(Deserialize(operation.Operation.TaskInputsJson), requestId) != draftId)
        {
            return RequestNotFound();
        }

        // A read, and only a read. The plan becomes a revision in the worker, straight after the proposal
        // commits — polling a status must not be what writes a creator's content, not least because a Viewer
        // may poll and may not write.
        var draft = await drafts.GetAsync(draftId, cancellationToken);

        return draft.Succeeded
            ? OperationResult<LongFormOutlineRequestStatusServiceModel>.Success(
                new LongFormOutlineRequestStatusServiceModel(
                    AiOperationDescription.Describe(operation.Operation, operation.Proposal), draft.Value!))
            : RequestNotFound();
    }

    /// <summary>The checks every outline request passes before anything is queued or spent.</summary>
    private async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>?> RefuseBeforeQueueingAsync(
        LongFormKind kind, RequestLongFormOutlineViewModel model, CancellationToken cancellationToken)
    {
        if (!await tasks.IsEnabledAsync(AiTaskCatalog.LongFormOutline, cancellationToken))
        {
            return Refuse(
                AiLongFormOutlineRequestErrors.TaskNotEnabled,
                "Planning a long-form piece is not switched on for this workspace.");
        }

        // Asked of the content module's own rules, so a section this kind cannot have — or one asked for more
        // often than it may appear — is refused here as a bad request rather than queued and failed.
        var rules = plans.Describe(kind);
        var wanted = model.WantedRoles ?? [];

        if (wanted.Any(role => rules.Roles.All(rule => rule.Role != role))
            || wanted.GroupBy(role => role).Any(group =>
                rules.Roles.Single(rule => rule.Role == group.Key).MaxOccurrences is { } most && group.Count() > most))
        {
            return new IdempotentOutcome<StartedLongFormDraftServiceModel>(
                OperationResult<StartedLongFormDraftServiceModel>.Failure(OperationError.Validation(
                    AiLongFormOutlineRequestErrors.RequestInvalid,
                    "The request is not valid.",
                    [("wantedRoles", "Ask only for sections this kind of piece has, and no more often than each may appear.")])),
                Replayed: false);
        }

        // Before the operation is written, so a refused request leaves no orphan behind and spends no
        // allowance to say the allowance is spent (USAGE-006).
        return await quota.RefuseIfUnaffordableAsync(AiTaskType.LongFormOutline, cancellationToken) is { } spent
            ? new IdempotentOutcome<StartedLongFormDraftServiceModel>(
                OperationResult<StartedLongFormDraftServiceModel>.Failure(spent.Result.Error!), Replayed: false)
            : null;
    }

    private Task<AiOperationRequest> QueueAsync(
        string inputs, string idempotencyKey, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        return operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.LongFormOutline,

                // Not taken from the request, and NotApplicable: the operation names no recipe, and the scope
                // is what makes "no stored row here has a path to a recipe edit" a property of the row.
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,

                // Null on both. The subject is the piece of work; which recipes it pins is the grounding
                // package's to report.
                RecipeId = null,
                RecipeVersionId = null,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = inputs,
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);
    }

    /// <summary>
    /// The request as the inputs <see cref="LongFormOutlineAiTaskHandler"/> reads back.
    /// </summary>
    /// <remarks>
    /// The whole string is what an idempotency replay is compared against, so every character of it is the
    /// creator's question and nothing else: an optional input is written only when it was sent, and the
    /// request that starts a draft names no draft — see <see cref="LongFormOutlineInputs.LongFormDraftId"/>.
    /// </remarks>
    private static string Inputs(
        Guid? draftId, Guid contextId, LongFormKind kind, RequestLongFormOutlineViewModel model)
    {
        var inputs = new Dictionary<string, string>(5, StringComparer.Ordinal)
        {
            [LongFormOutlineInputs.CreativeContextId] = contextId.ToString("D"),
            [LongFormOutlineInputs.Kind] = kind.ToString(),
        };

        if (draftId is { } draft)
        {
            inputs[LongFormOutlineInputs.LongFormDraftId] = draft.ToString("D");
        }

        if (model.TargetWords is { } words)
        {
            inputs[LongFormOutlineInputs.TargetWords] = words.ToString(CultureInfo.InvariantCulture);
        }

        if (model.WantedRoles is { Count: > 0 } wanted)
        {
            inputs[LongFormOutlineInputs.WantedRoles] = string.Join(
                PhotographyConceptInputs.ListSeparator, wanted.Select(role => role.ToString()));
        }

        return JsonSerializer.Serialize(inputs);
    }

    private static IReadOnlyDictionary<string, string>? Deserialize(string? taskInputsJson) =>
        taskInputsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(taskInputsJson);

    private static IdempotentOutcome<StartedLongFormDraftServiceModel> Started(
        LongFormDraftServiceModel draft, AiOperation operation, AiOperationRequestOutcome outcome) =>
        new(
            OperationResult<StartedLongFormDraftServiceModel>.Success(
                new StartedLongFormDraftServiceModel(draft, AiOperationDescription.Describe(operation, null))),
            Replayed: outcome is AiOperationRequestOutcome.Replayed);

    private static IdempotentOutcome<StartedLongFormDraftServiceModel> KeyReused() => Refuse(
        IdempotencyPolicy.KeyReusedCode, "That idempotency key was already used for a different request.");

    private static IdempotentOutcome<StartedLongFormDraftServiceModel> Refuse(string code, string message) =>
        new(
            OperationResult<StartedLongFormDraftServiceModel>.Failure(
                new OperationError(code, message, new Dictionary<string, string[]>())),
            Replayed: false);

    private static OperationResult<LongFormOutlineRequestStatusServiceModel> RequestNotFound() =>
        OperationResult<LongFormOutlineRequestStatusServiceModel>.Failure(new OperationError(
            AiLongFormOutlineRequestErrors.RequestNotFound,
            "That outline request does not exist.",
            new Dictionary<string, string[]>()));
}
