using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for editing one task (13A.27).</summary>
public interface IWorkTaskEditBusiness
{
    /// <summary>
    /// Applies the submitted fields of <paramref name="model"/> to one of the caller's own tasks — one they
    /// captured or are doing — and leaves every other field as it was.
    /// </summary>
    /// <returns>
    /// The task as it now stands, or <see cref="PlanningErrorCodes.WorkTaskNotFound"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskStale"/>, <see cref="PlanningErrorCodes.WorkTaskInvalid"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskLinkUnprocessable"/>, <see cref="PlanningErrorCodes.WorkTaskLabelUnprocessable"/>,
    /// <see cref="PlanningErrorCodes.WorkTaskAssigneeUnprocessable"/> or <see cref="PlanningErrorCodes.WorkTaskNotSaved"/>.
    /// </returns>
    Task<OperationResult<WorkTaskServiceModel>> UpdateAsync(
        Guid taskId, UpdateWorkTaskViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskEditBusiness(
    IWorkTaskDataLayer dataLayer,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    WorkTaskLinkChecks links,
    IClock clock) : IWorkTaskEditBusiness
{
    private const string CannotEdit = "That task could not be edited as described.";

    public async Task<OperationResult<WorkTaskServiceModel>> UpdateAsync(
        Guid taskId, UpdateWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (UpdateWorkTaskInputChecks.Update(model).ToList() is { Count: > 0 } shape)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskInvalid, CannotEdit, shape));
        }

        var me = workspace.MembershipId;
        var task = await dataLayer.FindForUpdateAsync(taskId, cancellationToken);

        // One answer for no such task, another workspace's, and a colleague's: a task is private to the person
        // who captured it and the person doing it, and telling the three apart would say which one it was.
        if (task is null || (task.OwnerMembershipId != me && task.AssigneeMembershipId != me))
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotFound, "That task was not found.", new Dictionary<string, string[]>()));
        }

        // Before anything else is judged, so every later answer is about the task the caller actually read.
        if (!WorkTaskConcurrencyToken.Matches(model.ExpectedConcurrencyToken, task.RowVersion))
        {
            return Failure(Stale());
        }

        var next = Requested(task, model);

        if (SideEffectFailures(task, model, next) is { Count: > 0 } sideEffects)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskInvalid, CannotEdit, sideEffects));
        }

        // Only what the edit newly chooses is checked; a link, a label or an assignee the task already has is
        // not judged again, so an unrelated edit never fails because something changed around it.
        if (await links.RefusedAsync(NewlyLinking(task, next), cancellationToken) is { Count: > 0 } refused)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskLinkUnprocessable, CannotEdit, refused));
        }

        HashSet<Guid>? currentLabels = null;
        if (next.LabelIds is { } labelIds)
        {
            currentLabels = [.. (await dataLayer.LabelsForAsync([task.Id], cancellationToken)).Select(row => row.WorkspaceTagId)];

            var adding = labelIds.Except(currentLabels).ToList();
            var active = await dataLayer.ActiveTagIdsAsync(adding, cancellationToken);

            // Absent, retired and another workspace's all read the same: not a tag this task can be given.
            if (adding.Any(id => !active.Contains(id)))
            {
                return Failure(OperationError.Validation(
                    PlanningErrorCodes.WorkTaskLabelUnprocessable,
                    CannotEdit,
                    [("labelIds", "One of those is not a tag of this workspace that a task can be given.")]));
            }
        }

        if (next.Assignee != task.AssigneeMembershipId && !await CanHandToAsync(task, next.Assignee, cancellationToken))
        {
            return Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskAssigneeUnprocessable,
                CannotEdit,
                [("assigneeMembershipId", "That task cannot be handed to them.")]));
        }

        // A new list means a new place at its bottom. The day divides only not-started tasks into lists: in
        // progress and waiting are one list each, so re-planning one of those leaves it where it is.
        var regroup = next.Assignee != task.AssigneeMembershipId
            || (task.Status == WorkTaskStatus.Backlog && next.ScheduledDate != task.ScheduledDate);
        var labelsChanged = next.LabelIds is { } wanted && !wanted.SetEquals(currentLabels!);
        var (runBefore, stepBefore) = (task.WorkflowRunId, task.WorkflowStepKey);
        var fieldsChanged = Apply(task, next);

        // A step's task is the task for that step. Pointed at another run or step, or at none, it no longer is —
        // so it stops holding the place, and the step can be added to its owner's plan again (13A.29).
        if (task.Origin == WorkTaskOrigin.WorkflowStep
            && (task.WorkflowRunId != runBefore || task.WorkflowStepKey != stepBefore))
        {
            task.Origin = WorkTaskOrigin.Capture;
        }

        // An edit that changes nothing writes nothing, and the token the caller holds stays valid.
        if (!fieldsChanged && !labelsChanged)
        {
            return await AnswerAsync(task, cancellationToken);
        }

        // Always the task's own row, even for labels alone, so the row version moves and a stale edit of labels
        // is refused exactly as a stale edit of anything else is.
        task.UpdatedAt = clock.UtcNow;

        var write = await dataLayer.UpdateAsync(task, labelsChanged ? next.LabelIds : null, regroup, cancellationToken);

        return write switch
        {
            WorkTaskWrite.Saved => await AnswerAsync(task, cancellationToken),
            WorkTaskWrite.Stale => Failure(Stale()),
            _ => Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That edit could not be saved just then. Nothing was written, so you can send it again as it stands.",
                new Dictionary<string, string[]>())),
        };
    }

    /// <summary>What the task would hold with the edit applied: the submitted fields, and the rest as they are.</summary>
    private static WorkTaskEdit Requested(WorkTask task, UpdateWorkTaskViewModel model)
    {
        var scheduledDate = model.ScheduledDate.Or(task.ScheduledDate);

        // A week named outright; else the week of a day named outright; else the week the task has. Clearing the
        // day leaves the week, so the task drops into that week's backlog rather than out of the week.
        var planningWeekStart = model.PlanningWeekStart.IsSubmitted
            ? model.PlanningWeekStart.Value
            : model.ScheduledDate.TryGetSubmitted(out var day) && day is { } named
                ? WorkTaskWeek.StartOf(named)
                : task.PlanningWeekStart;

        return new WorkTaskEdit(
            Title: model.Title.IsSubmitted ? WorkTaskInputChecks.Normalize(model.Title.Value)! : task.Title,
            Description: model.Description.IsSubmitted ? WorkTaskInputChecks.Normalize(model.Description.Value) : task.Description,
            Priority: model.Priority.IsSubmitted ? model.Priority.Value!.Value : task.Priority,
            ScheduledDate: scheduledDate,
            PlanningWeekStart: planningWeekStart,
            DueDate: model.DueDate.Or(task.DueDate),
            EstimateMinutes: model.EstimateMinutes.Or(task.EstimateMinutes),
            LabelIds: model.LabelIds.IsSubmitted ? new HashSet<Guid>(model.LabelIds.Value ?? []) : null,
            Assignee: model.AssigneeMembershipId.IsSubmitted ? model.AssigneeMembershipId.Value!.Value : task.AssigneeMembershipId,
            RecipeId: model.RecipeId.Or(task.RecipeId),
            ContentCardId: model.ContentCardId.Or(task.ContentCardId),
            MediaAssetId: model.MediaAssetId.Or(task.MediaAssetId),
            WorkflowRunId: model.WorkflowRunId.Or(task.WorkflowRunId),
            WorkflowStepKey: model.WorkflowStepKey.IsSubmitted
                ? WorkTaskInputChecks.Normalize(model.WorkflowStepKey.Value)
                : task.WorkflowStepKey);
    }

    /// <summary>
    /// The edits that would drop something the caller did not mention — a day outside the new week, a step of a
    /// run no longer linked. Refused, never done quietly: the caller sends the other field too, and then it is
    /// their choice rather than a side effect.
    /// </summary>
    private static List<(string Field, string Message)> SideEffectFailures(
        WorkTask task, UpdateWorkTaskViewModel model, WorkTaskEdit next)
    {
        var failures = new List<(string Field, string Message)>();

        if (next.ScheduledDate is { } day)
        {
            if (next.PlanningWeekStart is not { } week)
            {
                failures.Add(("scheduledDate",
                    "A task planned for a day is planned for that day's week. Clear scheduledDate as well, or keep the week."));
            }
            else if (!WorkTaskWeek.Holds(week, day))
            {
                failures.Add(model.ScheduledDate.IsSubmitted
                    ? ("scheduledDate", "That day is not in that week.")
                    : ("scheduledDate",
                        "This task is planned for a day outside that week. Send scheduledDate as well: a day in the new week, or null."));
            }
        }

        if (next.WorkflowRunId != task.WorkflowRunId && task.WorkflowStepKey is not null && !model.WorkflowStepKey.IsSubmitted)
        {
            failures.Add(("workflowStepKey",
                "This task names a step of the run it points at. Send workflowStepKey as well: a step of the new run, or null."));
        }
        else if (next.WorkflowStepKey is not null && next.WorkflowRunId is null)
        {
            failures.Add(("workflowStepKey", "A step is a step of a run. Keep the run, or clear the step too."));
        }

        return failures;
    }

    /// <summary>
    /// The links the edit newly chooses. A step newly chosen is checked against its run, so the run is named
    /// too even when it is the one the task already has.
    /// </summary>
    private static WorkTaskLinkRequest NewlyLinking(WorkTask task, WorkTaskEdit next)
    {
        var runChanged = next.WorkflowRunId != task.WorkflowRunId;
        var stepChanged = next.WorkflowStepKey != task.WorkflowStepKey;

        return new WorkTaskLinkRequest(
            RecipeId: next.RecipeId != task.RecipeId ? next.RecipeId : null,
            ContentCardId: next.ContentCardId != task.ContentCardId ? next.ContentCardId : null,
            MediaAssetId: next.MediaAssetId != task.MediaAssetId ? next.MediaAssetId : null,
            WorkflowRunId: runChanged || (stepChanged && next.WorkflowStepKey is not null) ? next.WorkflowRunId : null,
            WorkflowStepKey: runChanged || stepChanged ? next.WorkflowStepKey : null);
    }

    /// <summary>
    /// The owner may hand the task to anyone who can keep work in the workspace, themselves included; the person
    /// doing it may only hand it back to the owner. Nobody else may hand it anywhere — 13A.25's rule that one
    /// member does not manage another's list without an approved policy.
    /// </summary>
    private async Task<bool> CanHandToAsync(WorkTask task, Guid target, CancellationToken cancellationToken)
    {
        var mayHand = workspace.MembershipId == task.OwnerMembershipId || target == task.OwnerMembershipId;
        if (!mayHand)
        {
            return false;
        }

        // Active and able to keep work: a Viewer, an invitation or a removed member cannot be handed a task, and
        // another workspace's membership is simply not found.
        var roles = await workspaces.FindActiveMemberRolesAsync([target], cancellationToken);

        return roles.TryGetValue(target, out var role) && role >= WorkspaceRole.Contributor;
    }

    /// <summary>Writes the requested values onto the tracked task, and says whether any of them differed.</summary>
    private static bool Apply(WorkTask task, WorkTaskEdit next)
    {
        var changed = false;

        Set(task.Title, next.Title, value => task.Title = value);
        Set(task.Description, next.Description, value => task.Description = value);
        Set(task.Priority, next.Priority, value => task.Priority = value);
        Set(task.ScheduledDate, next.ScheduledDate, value => task.ScheduledDate = value);
        Set(task.PlanningWeekStart, next.PlanningWeekStart, value => task.PlanningWeekStart = value);
        Set(task.DueDate, next.DueDate, value => task.DueDate = value);
        Set(task.EstimateMinutes, next.EstimateMinutes, value => task.EstimateMinutes = value);
        Set(task.AssigneeMembershipId, next.Assignee, value => task.AssigneeMembershipId = value);
        Set(task.RecipeId, next.RecipeId, value => task.RecipeId = value);
        Set(task.ContentCardId, next.ContentCardId, value => task.ContentCardId = value);
        Set(task.MediaAssetId, next.MediaAssetId, value => task.MediaAssetId = value);
        Set(task.WorkflowRunId, next.WorkflowRunId, value => task.WorkflowRunId = value);
        Set(task.WorkflowStepKey, next.WorkflowStepKey, value => task.WorkflowStepKey = value);

        return changed;

        void Set<T>(T current, T value, Action<T> write)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                write(value);
                changed = true;
            }
        }
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
        "That task has changed since you opened it. Nothing was saved: read it again and send your edit again.",
        new Dictionary<string, string[]>());

    private static OperationResult<WorkTaskServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskServiceModel>.Failure(error);
}

/// <summary>Every editable value of a task as the edit would leave it.</summary>
internal sealed record WorkTaskEdit(
    string Title,
    string? Description,
    WorkTaskPriority Priority,
    DateOnly? ScheduledDate,
    DateOnly? PlanningWeekStart,
    DateOnly? DueDate,
    int? EstimateMinutes,
    IReadOnlySet<Guid>? LabelIds,
    Guid Assignee,
    Guid? RecipeId,
    Guid? ContentCardId,
    Guid? MediaAssetId,
    Guid? WorkflowRunId,
    string? WorkflowStepKey);
