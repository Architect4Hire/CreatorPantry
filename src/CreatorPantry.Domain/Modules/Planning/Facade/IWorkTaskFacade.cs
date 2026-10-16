using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Facade;

/// <summary>
/// The application boundary for one person's work tasks: quick capture (13A.24), the day (13A.25), the week
/// (13A.26), editing (13A.27), moving (13A.28), reordering (13A.28a), completing and reopening (13A.28b), and
/// adding a workflow step to the plan (13A.29).
/// </summary>
public interface IWorkTaskFacade
{
    /// <summary>
    /// Captures a task for the caller in the resolved workspace. Contributor and above; a lower role is
    /// refused with <see cref="PlanningErrorCodes.WorkTaskForbidden"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent under <paramref name="idempotencyKey"/>: a replay returns the first answer and writes
    /// nothing, and the key's record commits with the task or not at all. The key is optional; without one,
    /// two identical requests are two tasks, which for a to-do list is usually what someone meant.
    /// </remarks>
    Task<IdempotentOutcome<WorkTaskServiceModel>> CreateAsync(
        string userId, CreateWorkTaskViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's own day in the resolved workspace (13A.25): what is late, planned for today, under way,
    /// held up, and finished since midnight, with suggestions only when asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No role bar, and none is needed.</strong> A resolved workspace context is the authorization
    /// for the lowest role, and the answer is the caller's own work and nobody else's — the query is keyed
    /// on their membership, so there is no other list to refuse them. A Viewer who was handed work before
    /// being demoted can still see it, which is the right answer.
    /// </para>
    /// <para>
    /// Not cached: it changes with every tick of a task and turns over at midnight in a zone the cache key
    /// would have to carry anyway.
    /// </para>
    /// </remarks>
    Task<OperationResult<TodayWorkTasksServiceModel>> GetTodayAsync(
        bool includeSuggestions, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's own week in the resolved workspace (13A.26): each day's plan, the week's backlog, what is
    /// under way, held up and finished, with totals where estimates exist.
    /// </summary>
    /// <remarks>
    /// No role bar and no cache, for the daily view's reasons: the answer is the caller's own work, and it
    /// changes with every tick of a task.
    /// </remarks>
    Task<OperationResult<WeekWorkTasksServiceModel>> GetWeekAsync(
        WeekWorkTasksViewModel query, CancellationToken cancellationToken);

    /// <summary>
    /// Edits one of the caller's own tasks (13A.27): only the submitted fields change. Contributor and above,
    /// and only the task's owner or the person doing it.
    /// </summary>
    /// <remarks>
    /// Not idempotent by key: the required concurrency token already makes a repeat harmless, because a repeat
    /// of an edit that landed is quoting a token the edit moved, and is refused without writing.
    /// </remarks>
    Task<OperationResult<WorkTaskServiceModel>> UpdateAsync(
        Guid taskId, UpdateWorkTaskViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Moves one of the caller's own tasks to another of their lists (13A.28). Contributor and above, and only
    /// the person doing the task.
    /// </summary>
    /// <remarks>
    /// Idempotent under <paramref name="idempotencyKey"/>, which is optional as it is on capture: a replay returns
    /// the first answer and moves nothing, and the key's record commits with the move or not at all. Without a
    /// key a repeat still cannot move twice — it quotes a token the first move used, and is refused.
    /// </remarks>
    Task<IdempotentOutcome<WorkTaskServiceModel>> MoveAsync(
        string userId, Guid taskId, MoveWorkTaskViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Gives one of the caller's own tasks a new place in the list it is in (13A.28a). Contributor and above, and
    /// only the person doing the task.
    /// </summary>
    /// <remarks>
    /// Not idempotent by key, as the card board's reorder is not: the required token already makes a repeat
    /// harmless, because a repeat of a reorder that landed quotes a token it moved.
    /// </remarks>
    Task<OperationResult<WorkTaskServiceModel>> ReorderAsync(
        Guid taskId, ReorderWorkTaskViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Finishes one of the caller's own tasks (13A.28b). Contributor and above, and only the person doing it.
    /// Idempotent under an optional <paramref name="idempotencyKey"/>, as a move is.
    /// </summary>
    Task<IdempotentOutcome<WorkTaskServiceModel>> CompleteAsync(
        string userId, Guid taskId, WorkTaskCompletionViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Takes one of the caller's finished tasks up again, where it stood (13A.28b). Contributor and above, and only
    /// the person doing it. Idempotent under an optional <paramref name="idempotencyKey"/>.
    /// </summary>
    Task<IdempotentOutcome<WorkTaskServiceModel>> ReopenAsync(
        string userId, Guid taskId, WorkTaskCompletionViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// What "Add to my plan" would make for one workflow step (13A.29). Writes nothing. Contributor and above, as
    /// for anything that leads to keeping work.
    /// </summary>
    Task<OperationResult<WorkTaskStepSuggestionServiceModel>> SuggestFromWorkflowStepAsync(
        WorkTaskStepSuggestionViewModel query, CancellationToken cancellationToken);

    /// <summary>
    /// "Add to my plan", confirmed (13A.29): makes the caller's task for one step, or answers with the one they
    /// already have. Idempotent under an optional <paramref name="idempotencyKey"/>; without one a repeat still
    /// makes nothing more, because the store keeps one task per person per step.
    /// </summary>
    Task<IdempotentOutcome<WorkTaskFromStepServiceModel>> AddFromWorkflowStepAsync(
        string userId, AddWorkTaskFromStepViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the caller's top three for today (13A.32). Contributor and above, as for anything that keeps work.
    /// </summary>
    /// <remarks>
    /// Not idempotent by key, and needs none: the request is the whole set, so a repeat writes what is already there.
    /// </remarks>
    Task<OperationResult<WorkTaskTopThreeServiceModel>> SetTopThreeAsync(
        SetWorkTaskTopThreeViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskFacade(
    IValidator<CreateWorkTaskViewModel> createValidator,
    IValidator<WeekWorkTasksViewModel> weekValidator,
    IValidator<UpdateWorkTaskViewModel> updateValidator,
    IValidator<MoveWorkTaskViewModel> moveValidator,
    IValidator<ReorderWorkTaskViewModel> reorderValidator,
    IValidator<WorkTaskCompletionViewModel> completionValidator,
    IValidator<WorkTaskStepSuggestionViewModel> suggestionValidator,
    IValidator<AddWorkTaskFromStepViewModel> fromStepValidator,
    IValidator<SetWorkTaskTopThreeViewModel> topThreeValidator,
    IWorkTaskBusiness business,
    IWorkTaskWeekBusiness weekBusiness,
    IWorkTaskEditBusiness editBusiness,
    IWorkTaskMoveBusiness moveBusiness,
    IWorkTaskReorderBusiness reorderBusiness,
    IWorkTaskCompletionBusiness completionBusiness,
    IWorkTaskFromStepBusiness fromStepBusiness,
    IWorkTaskFocusBusiness focusBusiness,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IWorkTaskFacade
{
    private const string CreateOperation = "planning.work_task.create";

    private const string MoveOperation = "planning.work_task.move";

    private const string CompleteOperation = "planning.work_task.complete";

    private const string ReopenOperation = "planning.work_task.reopen";

    private const string FromWorkflowStepOperation = "planning.work_task.from_workflow_step";

    public async Task<IdempotentOutcome<WorkTaskServiceModel>> CreateAsync(
        string userId, CreateWorkTaskViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Refused(forbidden);
        }

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That task could not be captured as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The submitted fields are the fingerprint: the same key with a different body is a different
        // request. The caller is part of the key's scope, so two people cannot collide on one.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, CreateOperation, idempotencyKey, model, KeyRequired: false),
            token => business.CreateAsync(model, token),
            cancellationToken);
    }

    // Contributor: a Viewer is a read-only member of the workspace — no work in it, and so no work to track.
    // The route policy has already refused a lower role; this is the backstop for a worker or plugin reaching
    // the facade directly, which is why the facade rather than the controller owns the bar.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Contributor
            ? new OperationError(
                PlanningErrorCodes.WorkTaskForbidden,
                "You do not have permission to keep work in this workspace.",
                new Dictionary<string, string[]>())
            : null;

    private static IdempotentOutcome<WorkTaskServiceModel> Refused(OperationError error) =>
        new(OperationResult<WorkTaskServiceModel>.Failure(error), Replayed: false);

    public Task<OperationResult<TodayWorkTasksServiceModel>> GetTodayAsync(
        bool includeSuggestions, CancellationToken cancellationToken) =>
        business.GetTodayAsync(includeSuggestions, cancellationToken);

    public async Task<OperationResult<WeekWorkTasksServiceModel>> GetWeekAsync(
        WeekWorkTasksViewModel query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var validation = await weekValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid || !WeekWorkTasksInputs.TryParse(query.WeekStart, out var weekStart))
        {
            return OperationResult<WeekWorkTasksServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.WeekInvalid,
                "That is not a week that can be read.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await weekBusiness.GetWeekAsync(weekStart, cancellationToken);
    }

    public async Task<OperationResult<WorkTaskServiceModel>> UpdateAsync(
        Guid taskId, UpdateWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return OperationResult<WorkTaskServiceModel>.Failure(forbidden);
        }

        var validation = await updateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkTaskServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That task could not be edited as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await editBusiness.UpdateAsync(taskId, model, cancellationToken);
    }

    public async Task<IdempotentOutcome<WorkTaskServiceModel>> MoveAsync(
        string userId, Guid taskId, MoveWorkTaskViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Refused(forbidden);
        }

        var validation = await moveValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That task could not be moved as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The task is part of the fingerprint: the same key and body for a different task is a different request,
        // and must not replay the first task's answer.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, MoveOperation, idempotencyKey, new { TaskId = taskId, Move = model }, KeyRequired: false),
            token => moveBusiness.MoveAsync(taskId, model, token),
            cancellationToken);
    }

    public async Task<OperationResult<WorkTaskServiceModel>> ReorderAsync(
        Guid taskId, ReorderWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return OperationResult<WorkTaskServiceModel>.Failure(forbidden);
        }

        var validation = await reorderValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkTaskServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That task could not be reordered as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await reorderBusiness.ReorderAsync(taskId, model, cancellationToken);
    }

    public Task<IdempotentOutcome<WorkTaskServiceModel>> CompleteAsync(
        string userId, Guid taskId, WorkTaskCompletionViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
        CompletionAsync(userId, taskId, model, idempotencyKey, CompleteOperation, completionBusiness.CompleteAsync, cancellationToken);

    public Task<IdempotentOutcome<WorkTaskServiceModel>> ReopenAsync(
        string userId, Guid taskId, WorkTaskCompletionViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
        CompletionAsync(userId, taskId, model, idempotencyKey, ReopenOperation, completionBusiness.ReopenAsync, cancellationToken);

    public async Task<OperationResult<WorkTaskStepSuggestionServiceModel>> SuggestFromWorkflowStepAsync(
        WorkTaskStepSuggestionViewModel query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (Forbidden() is { } forbidden)
        {
            return OperationResult<WorkTaskStepSuggestionServiceModel>.Failure(forbidden);
        }

        var validation = await suggestionValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkTaskStepSuggestionServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That step could not be read.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await fromStepBusiness.SuggestAsync(query.WorkflowRunId!.Value, query.WorkflowStepKey!, cancellationToken);
    }

    public async Task<IdempotentOutcome<WorkTaskFromStepServiceModel>> AddFromWorkflowStepAsync(
        string userId, AddWorkTaskFromStepViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return new(OperationResult<WorkTaskFromStepServiceModel>.Failure(forbidden), Replayed: false);
        }

        var validation = await fromStepValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return new(OperationResult<WorkTaskFromStepServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That step could not be added to your plan.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage)))), Replayed: false);
        }

        // The run and step are in the body, so the body is the fingerprint.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, FromWorkflowStepOperation, idempotencyKey, model, KeyRequired: false),
            token => fromStepBusiness.AddAsync(model, token),
            cancellationToken);
    }

    public async Task<OperationResult<WorkTaskTopThreeServiceModel>> SetTopThreeAsync(
        SetWorkTaskTopThreeViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return OperationResult<WorkTaskTopThreeServiceModel>.Failure(forbidden);
        }

        var validation = await topThreeValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkTaskTopThreeServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.TopThreeInvalid,
                "That top three could not be read.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await focusBusiness.SetAsync(model, cancellationToken);
    }

    /// <summary>
    /// Both commands' shape: the role bar, the token's form, then the operation under its own idempotency name —
    /// so a key used to complete a task can never replay as a reopen.
    /// </summary>
    private async Task<IdempotentOutcome<WorkTaskServiceModel>> CompletionAsync(
        string userId,
        Guid taskId,
        WorkTaskCompletionViewModel model,
        string? idempotencyKey,
        string operation,
        Func<Guid, WorkTaskCompletionViewModel, CancellationToken, Task<OperationResult<WorkTaskServiceModel>>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Refused(forbidden);
        }

        var validation = await completionValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That request could not be read.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The task is part of the fingerprint, as for a move: the same key for another task is another request.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, operation, idempotencyKey, new { TaskId = taskId, Request = model }, KeyRequired: false),
            token => run(taskId, model, token),
            cancellationToken);
    }
}
