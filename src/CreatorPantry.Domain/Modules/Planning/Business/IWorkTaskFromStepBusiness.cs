using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Workflows.Facade;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for "Add to my plan" from a workflow step (13A.29).</summary>
/// <remarks>
/// <para>
/// <strong>Only ever on confirmation.</strong> Suggesting writes nothing; a task is made by the confirm alone, for
/// the one step it names, and nothing here runs for every step of a run.
/// </para>
/// <para>
/// <strong>The run is read, never written.</strong> The task points at the run and its step; the run gains no
/// link and no step changes state. A step is done when the workflow's own rule is satisfied, and finishing the
/// task is not that.
/// </para>
/// </remarks>
public interface IWorkTaskFromStepBusiness
{
    /// <summary>What "Add to my plan" would make for one step, and the task the caller already has for it if any.</summary>
    Task<OperationResult<WorkTaskStepSuggestionServiceModel>> SuggestAsync(
        Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken);

    /// <summary>Makes the caller's task for one step from what they confirmed, or answers with the one they have.</summary>
    Task<OperationResult<WorkTaskFromStepServiceModel>> AddAsync(
        AddWorkTaskFromStepViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskFromStepBusiness(
    IWorkTaskDataLayer dataLayer,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IWorkflowRunFacade workflowRuns,
    IBrandProfileFacade brandProfiles,
    ITimeZoneConverter zones,
    IClock clock) : IWorkTaskFromStepBusiness
{
    private const string CannotAdd = "That step could not be added to your plan.";

    public async Task<OperationResult<WorkTaskStepSuggestionServiceModel>> SuggestAsync(
        Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken)
    {
        var stepKey = WorkTaskInputChecks.Normalize(workflowStepKey)!;
        var (step, refused) = await PlannableStepAsync(workflowRunId, stepKey, cancellationToken);
        if (refused is not null)
        {
            return OperationResult<WorkTaskStepSuggestionServiceModel>.Failure(refused);
        }

        var existing = await dataLayer.FindFromWorkflowStepAsync(workspace.MembershipId, workflowRunId, stepKey, cancellationToken);

        return OperationResult<WorkTaskStepSuggestionServiceModel>.Success(new WorkTaskStepSuggestionServiceModel
        {
            WorkflowRunId = workflowRunId,
            WorkflowStepKey = stepKey,
            RunTitle = step!.RunTitle,
            StepTitle = step.StepTitle,
            Title = step.StepTitle,
            Description = Describe(step.RunTitle),
            EstimateMinutes = null,
            ScheduledDate = null,
            PlanningWeekStart = await CurrentWeekAsync(cancellationToken),
            WorkflowRunRoute = WorkTaskPolicy.WorkflowRunRoute(workflowRunId),
            ExistingTask = existing is null ? null : await ServiceModelAsync(existing, cancellationToken),
        });
    }

    public async Task<OperationResult<WorkTaskFromStepServiceModel>> AddAsync(
        AddWorkTaskFromStepViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (WorkTaskFromStepInputChecks.Add(model).ToList() is { Count: > 0 } shape)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskInvalid, CannotAdd, shape));
        }

        var runId = model.WorkflowRunId!.Value;
        var stepKey = WorkTaskInputChecks.Normalize(model.WorkflowStepKey)!;
        var (step, refused) = await PlannableStepAsync(runId, stepKey, cancellationToken);
        if (refused is not null)
        {
            return Failure(refused);
        }

        var now = clock.UtcNow;
        var me = workspace.MembershipId;

        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = me,
            AssigneeMembershipId = me,
            Title = WorkTaskInputChecks.Normalize(model.Title) ?? step!.StepTitle,

            // Left out, the suggestion; sent — even blank — the creator's own choice.
            Description = model.Description is null ? Describe(step!.RunTitle) : WorkTaskInputChecks.Normalize(model.Description),
            Status = WorkTaskStatus.Backlog,
            Priority = model.Priority ?? WorkTaskPriority.Normal,
            ScheduledDate = model.ScheduledDate,
            PlanningWeekStart = model.PlanningWeekStart
                ?? (model.ScheduledDate is { } day ? WorkTaskWeek.StartOf(day) : await CurrentWeekAsync(cancellationToken)),
            EstimateMinutes = model.EstimateMinutes,
            WorkflowRunId = runId,
            WorkflowStepKey = stepKey,
            Origin = WorkTaskOrigin.WorkflowStep,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (await dataLayer.AddFromWorkflowStepAsync(task, cancellationToken) is not { } added)
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That could not be added just then. Nothing was written, so you can send it again as it stands.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<WorkTaskFromStepServiceModel>.Success(new WorkTaskFromStepServiceModel
        {
            Task = await ServiceModelAsync(added.Task, cancellationToken),
            Created = added.Created,
        });
    }

    /// <summary>
    /// The run and step, read through the module that owns them in the resolved workspace, and whether there is
    /// still something to plan there.
    /// </summary>
    private async Task<(PlannableStep? Step, OperationError? Refused)> PlannableStepAsync(
        Guid runId, string stepKey, CancellationToken cancellationToken)
    {
        var run = await workflowRuns.GetAsync(runId, cancellationToken);

        // The capture route's answer, for its reason: one for a run that never existed and another workspace's.
        if (!run.Succeeded)
        {
            return (null, OperationError.Validation(
                PlanningErrorCodes.WorkTaskLinkUnprocessable, CannotAdd, [("workflowRunId", WorkTaskLinkChecks.NotFoundHere)]));
        }

        var step = run.Value!.Steps.FirstOrDefault(candidate => string.Equals(candidate.Key, stepKey, StringComparison.Ordinal));
        if (step is null)
        {
            return (null, OperationError.Validation(
                PlanningErrorCodes.WorkTaskLinkUnprocessable, CannotAdd, [("workflowStepKey", "That run has no such step.")]));
        }

        if (run.Value.Status != WorkflowRunStatus.InProgress)
        {
            return (null, OperationError.Validation(
                PlanningErrorCodes.WorkTaskStepUnprocessable,
                CannotAdd,
                [("workflowRunId", "That workflow is no longer in progress, so there is nothing left in it to plan.")]));
        }

        if (step.Status is WorkflowStepRunStatus.Completed or WorkflowStepRunStatus.Skipped)
        {
            return (null, OperationError.Validation(
                PlanningErrorCodes.WorkTaskStepUnprocessable,
                CannotAdd,
                [("workflowStepKey", "That step is already done or passed over, so there is nothing left to plan.")]));
        }

        return (new PlannableStep(run.Value.Title, step.Title), null);
    }

    /// <summary>The line a suggested task carries: which journey it came from, in the creator's own name for it.</summary>
    private static string Describe(string runTitle) => $"From your “{runTitle}” workflow.";

    /// <summary>
    /// The week holding today where the workspace keeps its calendar, the default a capture takes; none when no
    /// zone is set, rather than a guessed one.
    /// </summary>
    private async Task<DateOnly?> CurrentWeekAsync(CancellationToken cancellationToken)
    {
        var profile = await brandProfiles.GetAsync(cancellationToken);
        var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

        return zone is { Length: > 0 } && zones.IsValidZone(zone)
            ? WorkTaskWeek.StartOf(zones.LocalDate(clock.UtcNow, zone))
            : null;
    }

    private async Task<WorkTaskServiceModel> ServiceModelAsync(WorkTask task, CancellationToken cancellationToken)
    {
        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. new[] { task.OwnerMembershipId, task.AssigneeMembershipId }.Distinct()], cancellationToken);
        var labels = WorkTaskMapper.LabelsByTask(await dataLayer.LabelsForAsync([task.Id], cancellationToken));

        return WorkTaskMapper.ToServiceModel(task, names, labels);
    }

    private static OperationResult<WorkTaskFromStepServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskFromStepServiceModel>.Failure(error);

    private sealed record PlannableStep(string RunTitle, string StepTitle);
}
