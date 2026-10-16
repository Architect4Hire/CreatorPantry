using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for finishing a task and taking it up again (13A.28b).</summary>
/// <remarks>
/// <para>
/// <strong>Completing keeps the plan.</strong> The day, the week and the place in its list are left as they were,
/// and the status it was completed from is recorded, so reopening puts the task back exactly where it stood.
/// </para>
/// <para>
/// <strong>Nothing else finishes with it.</strong> A workflow step the task is about is done when the workflow's
/// own rule is satisfied, judged by the Workflows module; a linked recipe, card or picture is never touched.
/// </para>
/// </remarks>
public interface IWorkTaskCompletionBusiness
{
    /// <summary>Finishes one of the caller's own tasks — one they are doing.</summary>
    /// <returns>
    /// The task as it now stands, or <see cref="PlanningErrorCodes.WorkTaskNotFound"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskStale"/>, <see cref="PlanningErrorCodes.WorkTaskCompleted"/> or
    /// <see cref="PlanningErrorCodes.WorkTaskNotSaved"/>.
    /// </returns>
    Task<OperationResult<WorkTaskServiceModel>> CompleteAsync(
        Guid taskId, WorkTaskCompletionViewModel model, CancellationToken cancellationToken);

    /// <summary>Takes one of the caller's finished tasks up again, in the status and place it was finished from.</summary>
    /// <returns>
    /// The task as it now stands, or <see cref="PlanningErrorCodes.WorkTaskNotFound"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskStale"/>, <see cref="PlanningErrorCodes.WorkTaskOpen"/> or
    /// <see cref="PlanningErrorCodes.WorkTaskNotSaved"/>.
    /// </returns>
    Task<OperationResult<WorkTaskServiceModel>> ReopenAsync(
        Guid taskId, WorkTaskCompletionViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskCompletionBusiness(
    IWorkTaskDataLayer dataLayer,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IClock clock) : IWorkTaskCompletionBusiness
{
    public Task<OperationResult<WorkTaskServiceModel>> CompleteAsync(
        Guid taskId, WorkTaskCompletionViewModel model, CancellationToken cancellationToken) =>
        ChangeAsync(taskId, model, task =>
        {
            if (task.Status == WorkTaskStatus.Done)
            {
                return new OperationError(
                    PlanningErrorCodes.WorkTaskCompleted,
                    "That task is already finished.",
                    new Dictionary<string, string[]>());
            }

            var now = clock.UtcNow;

            // Where it stood, so a reopen can put it back; its day, week and place stay as they are.
            task.CompletedFromStatus = task.Status;
            task.Status = WorkTaskStatus.Done;
            task.CompletedAt = now;
            task.CompletedByMembershipId = workspace.MembershipId;
            task.UpdatedAt = now;

            return null;
        }, cancellationToken);

    public Task<OperationResult<WorkTaskServiceModel>> ReopenAsync(
        Guid taskId, WorkTaskCompletionViewModel model, CancellationToken cancellationToken) =>
        ChangeAsync(taskId, model, task =>
        {
            if (task.Status != WorkTaskStatus.Done)
            {
                return new OperationError(
                    PlanningErrorCodes.WorkTaskOpen,
                    "That task is not finished, so there is nothing to reopen.",
                    new Dictionary<string, string[]>());
            }

            // Back to the status it was finished from. The store insists a finished task records one, so there
            // is always a status to go back to.
            task.Status = task.CompletedFromStatus!.Value;
            task.CompletedFromStatus = null;
            task.CompletedAt = null;
            task.CompletedByMembershipId = null;
            task.UpdatedAt = clock.UtcNow;

            return null;
        }, cancellationToken);

    /// <summary>
    /// The shared shape of both commands: the caller's own task, at the version they read, changed by
    /// <paramref name="change"/> — which refuses with an error or returns null having changed the tracked task.
    /// </summary>
    private async Task<OperationResult<WorkTaskServiceModel>> ChangeAsync(
        Guid taskId,
        WorkTaskCompletionViewModel model,
        Func<WorkTask, OperationError?> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (!WorkTaskConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            return Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "Send the concurrencyToken of the task.",
                [("expectedConcurrencyToken", "Send the concurrencyToken of the task.")]));
        }

        var task = await dataLayer.FindForUpdateAsync(taskId, cancellationToken);

        // Only the person doing it, as for a move: finishing the work is theirs to say. One answer for that, for
        // no such task, and for another workspace's.
        if (task is null || task.AssigneeMembershipId != workspace.MembershipId)
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotFound, "That task was not found.", new Dictionary<string, string[]>()));
        }

        if (!WorkTaskConcurrencyToken.Matches(model.ExpectedConcurrencyToken, task.RowVersion))
        {
            return Failure(Stale());
        }

        if (change(task) is { } refused)
        {
            return Failure(refused);
        }

        return await dataLayer.SaveAsync(task, cancellationToken) switch
        {
            WorkTaskWrite.Saved => await AnswerAsync(task, cancellationToken),
            WorkTaskWrite.Stale => Failure(Stale()),
            _ => Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That could not be saved just then. Nothing was written, so you can send it again as it stands.",
                new Dictionary<string, string[]>())),
        };
    }

    private async Task<OperationResult<WorkTaskServiceModel>> AnswerAsync(WorkTask task, CancellationToken cancellationToken)
    {
        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. new[] { task.OwnerMembershipId, task.AssigneeMembershipId }.Distinct()], cancellationToken);
        var labels = WorkTaskMapper.LabelsByTask(await dataLayer.LabelsForAsync([task.Id], cancellationToken));

        return OperationResult<WorkTaskServiceModel>.Success(WorkTaskMapper.ToServiceModel(task, names, labels));
    }

    private static OperationError Stale() => new(
        PlanningErrorCodes.WorkTaskStale,
        "That task has changed since you opened it. Nothing was saved: read it again and try again.",
        new Dictionary<string, string[]>());

    private static OperationResult<WorkTaskServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskServiceModel>.Failure(error);
}
