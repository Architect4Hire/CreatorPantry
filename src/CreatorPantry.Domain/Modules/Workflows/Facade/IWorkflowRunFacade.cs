using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
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

    /// <summary>Completes the run's current step once the server finds its rule met (13A.8).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> CompleteStepAsync(
        Guid runId, string stepKey, CompleteWorkflowStepViewModel model, CancellationToken cancellationToken);

    /// <summary>Goes back from the run's current step to the one before it (13A.9).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> BackAsync(
        Guid runId, string stepKey, WorkflowStepBackViewModel model, CancellationToken cancellationToken);

    /// <summary>Passes over the run's current step, which its workflow must mark optional (13A.9a).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> SkipAsync(
        Guid runId, string stepKey, SkipWorkflowStepViewModel model, CancellationToken cancellationToken);

    /// <summary>Stops a run, keeping it whole, and starts the same journey afresh in its place (13A.9b).</summary>
    /// <param name="idempotencyKey">Required: Start over creates a run, and a retry must not create a second.</param>
    Task<IdempotentOutcome<WorkflowRunRestart>> RestartAsync(
        string userId, Guid runId, RestartWorkflowRunViewModel model, string? idempotencyKey, CancellationToken cancellationToken);
}

internal sealed class WorkflowRunFacade(
    IValidator<StartWorkflowRunViewModel> startValidator,
    IValidator<SaveWorkflowStepDraftViewModel> draftValidator,
    IValidator<CompleteWorkflowStepViewModel> completeValidator,
    IValidator<WorkflowStepBackViewModel> backValidator,
    IValidator<SkipWorkflowStepViewModel> skipValidator,
    IValidator<RestartWorkflowRunViewModel> restartValidator,
    IWorkflowRunBusiness business,
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
