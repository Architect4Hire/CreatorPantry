using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>Which workflow step to suggest a task for (13A.29). Asking writes nothing.</summary>
public sealed record WorkTaskStepSuggestionViewModel(
    [property: FromQuery(Name = "workflowRunId")]
    [property: Description("The workflow run the step belongs to.")]
    Guid? WorkflowRunId = null,
    [property: FromQuery(Name = "workflowStepKey")]
    [property: Description("The step's key in that run.")]
    string? WorkflowStepKey = null);

/// <summary>
/// "Add to my plan" for one workflow step, confirmed (13A.29). Only the run and the step are required: anything
/// left out takes the suggestion, so confirming it unchanged is one click.
/// </summary>
public sealed record AddWorkTaskFromStepViewModel
{
    public Guid? WorkflowRunId { get; init; }

    public string? WorkflowStepKey { get; init; }

    /// <summary>Left out, the step's own title.</summary>
    public string? Title { get; init; }

    /// <summary>Left out, a line naming the run. Sent blank, none.</summary>
    public string? Description { get; init; }

    public WorkTaskPriority? Priority { get; init; }

    /// <summary>Left out, unknown: a step carries no estimate, and none is made up for it.</summary>
    public int? EstimateMinutes { get; init; }

    public DateOnly? ScheduledDate { get; init; }

    /// <summary>Left out, the week of <see cref="ScheduledDate"/>, or else the week holding today.</summary>
    public DateOnly? PlanningWeekStart { get; init; }
}

/// <summary>What "Add to my plan" would make for one step, before anything is made (13A.29).</summary>
/// <remarks>
/// Every field is a suggestion the creator can change before confirming. <see cref="ExistingTask"/> is the task
/// they already added for this step, when they have, so the screen can say so instead of offering it twice.
/// </remarks>
public sealed record WorkTaskStepSuggestionServiceModel
{
    public required Guid WorkflowRunId { get; init; }

    public required string WorkflowStepKey { get; init; }

    public required string RunTitle { get; init; }

    public required string StepTitle { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>Always null: a step carries no estimate, and an unknown is never offered as a number.</summary>
    public int? EstimateMinutes { get; init; }

    public DateOnly? ScheduledDate { get; init; }

    /// <summary>The week holding today in the workspace's zone; null when no zone is set.</summary>
    public DateOnly? PlanningWeekStart { get; init; }

    /// <summary>Where the run is, relative to the workspace.</summary>
    public required string WorkflowRunRoute { get; init; }

    public WorkTaskServiceModel? ExistingTask { get; init; }
}

/// <summary>The task "Add to my plan" made, or the one the caller already had for that step (13A.29).</summary>
public sealed record WorkTaskFromStepServiceModel
{
    public required WorkTaskServiceModel Task { get; init; }

    /// <summary>True when this request made the task; false when it was already in the caller's plan.</summary>
    public required bool Created { get; init; }
}

/// <summary>The checks an "Add to my plan" request can fail on its own, before the run has been read.</summary>
public static class WorkTaskFromStepInputChecks
{
    public static IEnumerable<(string Field, string Message)> Step(Guid? workflowRunId, string? workflowStepKey)
    {
        if (workflowRunId is not { } runId || runId == Guid.Empty)
        {
            yield return ("workflowRunId", "Name the workflow run.");
        }

        if (WorkTaskInputChecks.Normalize(workflowStepKey) is not { } step)
        {
            yield return ("workflowStepKey", "Name the step.");
        }
        else if (step.Length > WorkTaskPolicy.WorkflowStepKeyMaxLength)
        {
            yield return ("workflowStepKey", $"A step key is at most {WorkTaskPolicy.WorkflowStepKeyMaxLength} characters.");
        }
    }

    public static IEnumerable<(string Field, string Message)> Add(AddWorkTaskFromStepViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var failure in Step(model.WorkflowRunId, model.WorkflowStepKey))
        {
            yield return failure;
        }

        if (model.Title is not null)
        {
            if (WorkTaskInputChecks.Normalize(model.Title) is not { } title)
            {
                yield return ("title", "A task needs a title. Leave it out to use the step's.");
            }
            else if (title.Length > WorkTaskPolicy.TitleMaxLength)
            {
                yield return ("title", $"A title can be at most {WorkTaskPolicy.TitleMaxLength} characters.");
            }
        }

        if (WorkTaskInputChecks.Normalize(model.Description) is { Length: > WorkTaskPolicy.DescriptionMaxLength })
        {
            yield return ("description", $"A description can be at most {WorkTaskPolicy.DescriptionMaxLength} characters.");
        }

        if (model.Priority is { } priority && !Enum.IsDefined(priority))
        {
            yield return ("priority", "Name a priority: Low, Normal or High.");
        }

        if (model.EstimateMinutes is < 1 or > WorkTaskPolicy.EstimateMaxMinutes)
        {
            yield return ("estimateMinutes", $"An estimate is between 1 and {WorkTaskPolicy.EstimateMaxMinutes} minutes.");
        }

        if (model.PlanningWeekStart is { } week && !WorkTaskWeek.IsStart(week))
        {
            yield return ("planningWeekStart", $"A planning week starts on a {WorkTaskWeek.StartsOn}.");
        }
        else if (model.PlanningWeekStart is { } start && model.ScheduledDate is { } day && !WorkTaskWeek.Holds(start, day))
        {
            yield return ("scheduledDate", "That day is not in that week.");
        }
    }
}

public sealed class WorkTaskStepSuggestionViewModelValidator : AbstractValidator<WorkTaskStepSuggestionViewModel>
{
    public WorkTaskStepSuggestionViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in WorkTaskFromStepInputChecks.Step(model.WorkflowRunId, model.WorkflowStepKey))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class AddWorkTaskFromStepViewModelValidator : AbstractValidator<AddWorkTaskFromStepViewModel>
{
    public AddWorkTaskFromStepViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in WorkTaskFromStepInputChecks.Add(model))
            {
                context.AddFailure(field, message);
            }
        });
}
