using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for moving a task from one of its person's lists to another (13A.28).</summary>
public interface IWorkTaskMoveBusiness
{
    /// <summary>
    /// Moves one of the caller's own tasks — one they are doing — to another of their lists, at a place in it.
    /// </summary>
    /// <returns>
    /// The task as it now stands, or <see cref="PlanningErrorCodes.WorkTaskNotFound"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskStale"/>, <see cref="PlanningErrorCodes.WorkTaskCompleted"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskInvalid"/>, <see cref="PlanningErrorCodes.WorkTaskMoveUnprocessable"/>
    /// or <see cref="PlanningErrorCodes.WorkTaskNotSaved"/>.
    /// </returns>
    Task<OperationResult<WorkTaskServiceModel>> MoveAsync(
        Guid taskId, MoveWorkTaskViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskMoveBusiness(
    IWorkTaskDataLayer dataLayer,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IClock clock) : IWorkTaskMoveBusiness
{
    private const string CannotMove = "That task could not be moved as described.";

    public async Task<OperationResult<WorkTaskServiceModel>> MoveAsync(
        Guid taskId, MoveWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (MoveWorkTaskInputChecks.Move(model).ToList() is { Count: > 0 } shape)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskInvalid, CannotMove, shape));
        }

        var task = await dataLayer.FindForUpdateAsync(taskId, cancellationToken);

        // Only the person doing it. A move rearranges their list, and one member does not manage another's list
        // without an approved policy (13A.25) — not even the owner who handed the task on. One answer for that,
        // for no such task, and for another workspace's, so none can be told apart.
        if (task is null || task.AssigneeMembershipId != workspace.MembershipId)
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotFound, "That task was not found.", new Dictionary<string, string[]>()));
        }

        // Before anything else is judged, so every later answer is about the task the caller actually read.
        if (!WorkTaskConcurrencyToken.Matches(model.ExpectedConcurrencyToken, task.RowVersion))
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskStale,
                "That task has changed since you opened it. Nothing was moved: read it again and move it again.",
                new Dictionary<string, string[]>()));
        }

        // A finished task is reopened, which restores where it was (13A.28b); a move would skip that and lose it.
        if (task.Status == WorkTaskStatus.Done)
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskCompleted,
                "That task is finished. Reopen it to work on it again.",
                new Dictionary<string, string[]>()));
        }

        var status = model.Status!.Value;
        DateOnly? day;
        DateOnly? week;

        if (status != WorkTaskStatus.Backlog)
        {
            // In progress and waiting keep the plan: a task started on Monday is still Monday's.
            (day, week) = (task.ScheduledDate, task.PlanningWeekStart);
        }
        else if (model.ScheduledDate is { } named)
        {
            // A day's list, and the week that holds it.
            (day, week) = (named, model.PlanningWeekStart ?? WorkTaskWeek.StartOf(named));
        }
        else
        {
            // The backlog of no day: in the week named, or in the week the task already has.
            (day, week) = (null, model.PlanningWeekStart ?? task.PlanningWeekStart);
        }

        // Already exactly there: that is a reorder, not a move, and reordering is its own route (13A.28a).
        if (status == task.Status && day == task.ScheduledDate && week == task.PlanningWeekStart)
        {
            return Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                CannotMove,
                [("status", "That task is already there. Reorder it within its list instead.")]));
        }

        task.Status = status;
        task.ScheduledDate = day;
        task.PlanningWeekStart = week;
        task.UpdatedAt = clock.UtcNow;

        var write = await dataLayer.MoveAsync(task, model.BeforeTaskId, cancellationToken);

        return write switch
        {
            WorkTaskWrite.Saved => await AnswerAsync(task, cancellationToken),
            WorkTaskWrite.Stale => Failure(new OperationError(
                PlanningErrorCodes.WorkTaskStale,
                "That task, or one beside it, changed while it was being moved. Nothing was moved: read the list again and move it again.",
                new Dictionary<string, string[]>())),

            // The same answer whether the task named does not exist, is in another list, is the one being moved,
            // or is somebody else's — so a move cannot be used to ask where a colleague's tasks are.
            WorkTaskWrite.PlaceUnusable => Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskMoveUnprocessable,
                CannotMove,
                [("beforeTaskId", "That is not a task in the list this one is moving to.")])),
            _ => Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That move could not be saved just then. Nothing was written, so you can send it again as it stands.",
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

    private static OperationResult<WorkTaskServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskServiceModel>.Failure(error);
}
