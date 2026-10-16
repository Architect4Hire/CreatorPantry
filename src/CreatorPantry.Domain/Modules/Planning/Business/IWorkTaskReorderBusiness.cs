using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for reordering a task within its list (13A.28a).</summary>
public interface IWorkTaskReorderBusiness
{
    /// <summary>Gives one of the caller's own tasks — one they are doing — a new place in the list it is in.</summary>
    /// <returns>
    /// The task as it now stands, or <see cref="PlanningErrorCodes.WorkTaskNotFound"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskStale"/>, <see cref="PlanningErrorCodes.WorkTaskCompleted"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskInvalid"/>, <see cref="PlanningErrorCodes.WorkTaskMoveUnprocessable"/>
    /// or <see cref="PlanningErrorCodes.WorkTaskNotSaved"/>.
    /// </returns>
    Task<OperationResult<WorkTaskServiceModel>> ReorderAsync(
        Guid taskId, ReorderWorkTaskViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskReorderBusiness(
    IWorkTaskDataLayer dataLayer,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IClock clock) : IWorkTaskReorderBusiness
{
    private const string CannotReorder = "That task could not be reordered as described.";

    public async Task<OperationResult<WorkTaskServiceModel>> ReorderAsync(
        Guid taskId, ReorderWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (ReorderWorkTaskInputChecks.Reorder(model).ToList() is { Count: > 0 } shape)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskInvalid, CannotReorder, shape));
        }

        var task = await dataLayer.FindForUpdateAsync(taskId, cancellationToken);

        // Only the person doing it, for a move's reason: it is their list. One answer for that, for no such task,
        // and for another workspace's.
        if (task is null || task.AssigneeMembershipId != workspace.MembershipId)
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotFound, "That task was not found.", new Dictionary<string, string[]>()));
        }

        if (!WorkTaskConcurrencyToken.Matches(model.ExpectedConcurrencyToken, task.RowVersion))
        {
            return Failure(Stale());
        }

        // Finished tasks read in the order they were finished; a place among them means nothing.
        if (task.Status == WorkTaskStatus.Done)
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskCompleted,
                "That task is finished. Finished tasks are listed in the order they were finished.",
                new Dictionary<string, string[]>()));
        }

        var write = await dataLayer.ReorderAsync(task, model.BeforeTaskId, clock.UtcNow, cancellationToken);

        return write switch
        {
            // Already there writes nothing, and the token the caller holds stays good.
            WorkTaskWrite.Saved or WorkTaskWrite.Unchanged => await AnswerAsync(task, cancellationToken),
            WorkTaskWrite.Stale => Failure(Stale()),

            // The move's answer, for the move's reason: the same whatever the task named turned out to be.
            WorkTaskWrite.PlaceUnusable => Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskMoveUnprocessable,
                CannotReorder,
                [("beforeTaskId", "That is not a task in the list this one is in.")])),
            _ => Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That reorder could not be saved just then. Nothing was written, so you can send it again as it stands.",
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
        "That task, or one beside it, changed since you opened the list. Nothing was moved: read it again and try again.",
        new Dictionary<string, string[]>());

    private static OperationResult<WorkTaskServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskServiceModel>.Failure(error);
}
