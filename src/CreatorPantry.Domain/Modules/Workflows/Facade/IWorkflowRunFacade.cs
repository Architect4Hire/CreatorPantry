using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Workflows.Business;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Facade;

/// <summary>The application boundary for guided-workflow runs (FLOW-002).</summary>
public interface IWorkflowRunFacade
{
    /// <summary>Starts a run of one workflow for the caller in the resolved workspace (13A.5).</summary>
    /// <param name="userId">The signed-in account, for scoping the idempotency key.</param>
    /// <param name="idempotencyKey">
    /// Required. For <see cref="IdempotencyPolicy.Retention"/> (24 hours), a repeat of the same key and the same
    /// request returns the run the first one started, as it stood then; the same key with a different request
    /// answers <c>422 idempotency.key_reused</c>. After the window the key is free and starts a new run.
    /// </param>
    /// <remarks>
    /// The role check here answers <see cref="WorkflowErrorCodes.RunForbidden"/> for a caller that is not an HTTP
    /// request — a worker, a plugin. Over HTTP the route's policy refuses a Viewer first.
    /// </remarks>
    Task<IdempotentOutcome<WorkflowRunServiceModel>> StartAsync(
        string userId, StartWorkflowRunViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Reads one run in the resolved workspace, for whoever is asking (13A.6).</summary>
    /// <remarks>
    /// Every member may read every run in their workspace, a Viewer included; what the caller's role changes is
    /// only what the next action says they can do. An unknown run and another workspace's answer alike.
    /// </remarks>
    Task<OperationResult<WorkflowRunServiceModel>> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Points the run's current step at the record its unfinished work is kept in (13A.7).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> SaveDraftAsync(
        Guid runId, string stepKey, SaveWorkflowStepDraftViewModel model, CancellationToken cancellationToken);

    /// <summary>Saves the run's current step's draft and where the creator was, so they can leave and carry on (13A.10).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> SaveAndExitAsync(
        Guid runId, string stepKey, SaveAndExitWorkflowRunViewModel model, CancellationToken cancellationToken);

    /// <summary>Completes the run's current step once the server finds its rule met (13A.8).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> CompleteStepAsync(
        Guid runId, string stepKey, CompleteWorkflowStepViewModel model, CancellationToken cancellationToken);

    /// <summary>Goes back from the run's current step to the one before it (13A.9).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> BackAsync(
        Guid runId, string stepKey, WorkflowStepBackViewModel model, CancellationToken cancellationToken);

    /// <summary>Passes over the run's current step, which its workflow must mark optional (13A.9a).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> SkipAsync(
        Guid runId, string stepKey, SkipWorkflowStepViewModel model, CancellationToken cancellationToken);

    /// <summary>Finishes a run once the server finds every required step's rule met (13A.10a).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> CompleteRunAsync(
        Guid runId, CompleteWorkflowRunViewModel model, CancellationToken cancellationToken);

    /// <summary>One page of the workspace's runs for the reader, with owners and assignees named (13A.11).</summary>
    Task<OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>> ListAsync(
        WorkflowRunListViewModel model, CancellationToken cancellationToken);

    /// <summary>Stops a run for good, keeping it whole and everything it linked as it is (13A.10b).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> AbandonAsync(
        Guid runId, AbandonWorkflowRunViewModel model, CancellationToken cancellationToken);

    /// <summary>Stops a run, keeping it whole, and starts the same journey afresh in its place (13A.9b).</summary>
    /// <param name="idempotencyKey">Required: Start over creates a run, and a retry must not create a second.</param>
    Task<IdempotentOutcome<WorkflowRunRestart>> RestartAsync(
        string userId, Guid runId, RestartWorkflowRunViewModel model, string? idempotencyKey, CancellationToken cancellationToken);
}

internal sealed class WorkflowRunFacade(
    IValidator<StartWorkflowRunViewModel> startValidator,
    IValidator<SaveWorkflowStepDraftViewModel> draftValidator,
    IValidator<SaveAndExitWorkflowRunViewModel> saveAndExitValidator,
    IValidator<CompleteWorkflowStepViewModel> completeValidator,
    IValidator<WorkflowStepBackViewModel> backValidator,
    IValidator<SkipWorkflowStepViewModel> skipValidator,
    IValidator<CompleteWorkflowRunViewModel> completeRunValidator,
    IValidator<AbandonWorkflowRunViewModel> abandonValidator,
    IValidator<RestartWorkflowRunViewModel> restartValidator,
    IValidator<WorkflowRunListViewModel> listValidator,
    IWorkflowRunBusiness business,
    IWorkflowRunListBusiness list,
    IWorkspaceFacade workspaces,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IWorkflowRunFacade
{
    private const string StartOperation = "workflows.run.start";

    public async Task<IdempotentOutcome<WorkflowRunServiceModel>> StartAsync(
        string userId, StartWorkflowRunViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The route's policy says the same; this is the boundary a worker or a plugin would also cross.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't start a journey.",
                new Dictionary<string, string[]>()));
        }

        var validation = await startValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                WorkflowErrorCodes.RunInvalid,
                "That journey could not be started as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // What the start means, not the bytes it arrived in: the same key with a different journey, name or starting
        // record is a different request, while "Once" and "Once " are one name. An explicit record rather than
        // the ViewModel, so a field added to the ViewModel later does not turn every retry in flight into a
        // refused key.
        var fingerprint = new StartFingerprint(
            model.DefinitionKey,
            model.Title?.Trim(),
            model.StartingResource?.Kind,
            model.StartingResource?.Id);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, StartOperation, idempotencyKey, fingerprint),
            token => business.StartAsync(model, token),
            cancellationToken);
    }

    public Task<OperationResult<WorkflowRunServiceModel>> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        business.GetAsync(runId, cancellationToken);

    public async Task<OperationResult<WorkflowRunServiceModel>> SaveDraftAsync(
        Guid runId, string stepKey, SaveWorkflowStepDraftViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await draftValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.StepDraftInvalid,
                "That draft could not be saved as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.SaveDraftAsync(runId, stepKey, model, cancellationToken);
    }

    public async Task<OperationResult<WorkflowRunServiceModel>> SaveAndExitAsync(
        Guid runId, string stepKey, SaveAndExitWorkflowRunViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await saveAndExitValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.SaveAndExitInvalid,
                "That run could not be saved and left as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.SaveAndExitAsync(runId, stepKey, model, cancellationToken);
    }

    public async Task<OperationResult<WorkflowRunServiceModel>> CompleteStepAsync(
        Guid runId, string stepKey, CompleteWorkflowStepViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await completeValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.StepCompletionInvalid,
                "That step could not be completed as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.CompleteStepAsync(runId, stepKey, model, cancellationToken);
    }

    public async Task<OperationResult<WorkflowRunServiceModel>> BackAsync(
        Guid runId, string stepKey, WorkflowStepBackViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await backValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.BackInvalid,
                "That step could not be gone back from as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.BackAsync(runId, stepKey, model, cancellationToken);
    }

    public async Task<OperationResult<WorkflowRunServiceModel>> SkipAsync(
        Guid runId, string stepKey, SkipWorkflowStepViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await skipValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.StepSkipInvalid,
                "That step could not be skipped as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.SkipAsync(runId, stepKey, model, cancellationToken);
    }

    public async Task<OperationResult<WorkflowRunServiceModel>> CompleteRunAsync(
        Guid runId, CompleteWorkflowRunViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await completeRunValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.RunCompletionInvalid,
                "That run could not be finished as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.CompleteRunAsync(runId, model, cancellationToken);
    }

    /// <remarks>
    /// Any member may read the list. Names are put on after the page is read, in one call to the workspace module for
    /// everyone on it, so a page costs the same however many people it names; someone who has left is unnamed.
    /// </remarks>
    public async Task<OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>> ListAsync(
        WorkflowRunListViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await listValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // A bad cursor means something different to a client than a bad filter: start over, rather than fix the input.
            var code = validation.Errors.Any(error => error.PropertyName == "cursor")
                ? WorkflowErrorCodes.RunListCursorInvalid
                : WorkflowErrorCodes.RunListInvalid;

            return OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>.Failure(OperationError.Validation(
                code,
                "That list could not be read as asked.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var result = await list.ListAsync(model, cancellationToken);
        if (!result.Succeeded || result.Value!.Items.Count == 0)
        {
            return result;
        }

        var page = result.Value;
        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. page.Items.SelectMany(item => item.Assignee is { } assignee
                    ? new[] { item.Owner.MembershipId, assignee.MembershipId }
                    : [item.Owner.MembershipId])
                .Distinct()],
            cancellationToken);

        return OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>.Success(page with
        {
            Items =
            [
                .. page.Items.Select(item => item with
                {
                    Owner = Named(item.Owner, names),
                    Assignee = item.Assignee is { } assignee ? Named(assignee, names) : null,
                }),
            ],
        });
    }

    private static WorkflowRunMemberServiceModel Named(WorkflowRunMemberServiceModel member, IReadOnlyDictionary<Guid, string> names) =>
        member with { DisplayName = names.TryGetValue(member.MembershipId, out var name) ? name : null };

    public async Task<OperationResult<WorkflowRunServiceModel>> AbandonAsync(
        Guid runId, AbandonWorkflowRunViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>()));
        }

        var validation = await abandonValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkflowRunServiceModel>.Failure(OperationError.Validation(
                WorkflowErrorCodes.RunAbandonInvalid,
                "That run could not be stopped as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.AbandonAsync(runId, model, cancellationToken);
    }

    private const string RestartOperation = "workflows.run.restart";

    public async Task<IdempotentOutcome<WorkflowRunRestart>> RestartAsync(
        string userId, Guid runId, RestartWorkflowRunViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return new(OperationResult<WorkflowRunRestart>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "You have view-only access to this workspace, so you can't change a run.",
                new Dictionary<string, string[]>())), Replayed: false);
        }

        var validation = await restartValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return new(OperationResult<WorkflowRunRestart>.Failure(OperationError.Validation(
                WorkflowErrorCodes.RunRestartInvalid,
                "That run could not be started over as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage)))), Replayed: false);
        }

        // Which run, under what name: the token is how the request was guarded, not what it asks for.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, RestartOperation, idempotencyKey, new RestartFingerprint(runId, model.Title?.Trim())),
            token => business.RestartAsync(runId, model, token),
            cancellationToken);
    }

    private sealed record RestartFingerprint(Guid RunId, string? Title);

    private static IdempotentOutcome<WorkflowRunServiceModel> Refused(OperationError error) =>
        new(OperationResult<WorkflowRunServiceModel>.Failure(error), Replayed: false);

    private sealed record StartFingerprint(string? DefinitionKey, string? Title, WorkflowResourceKind? Kind, Guid? ResourceId);
}
