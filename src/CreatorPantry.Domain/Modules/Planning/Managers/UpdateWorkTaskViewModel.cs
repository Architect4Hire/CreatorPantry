using CreatorPantry.Domain.Managers.Patching;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>
/// An edit to one task (13A.27). <strong>A field left out is untouched; a field sent with a value is set; a
/// field sent as <c>null</c> is cleared.</strong>
/// </summary>
/// <remarks>
/// <para>
/// There is no owner, workspace, completion or recurrence field: those are the server's. An edit does not
/// move, reorder or complete a task — <see cref="Status"/> and <see cref="Position"/> exist only so that
/// sending them is refused by name rather than silently ignored.
/// </para>
/// <para>
/// Whether a value is allowed given what the task holds now — a day inside its week, a step of its run, who
/// the task may be handed to — is Business's to decide, because only Business has read the task.
/// </para>
/// </remarks>
public sealed record UpdateWorkTaskViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the read this edit was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    public PatchField<string?> Title { get; init; }

    /// <summary>Sent blank or <c>null</c>, cleared.</summary>
    public PatchField<string?> Description { get; init; }

    public PatchField<WorkTaskPriority?> Priority { get; init; }

    /// <summary>The day. Set alone, the week follows it; cleared, the week stays and the task joins its backlog.</summary>
    public PatchField<DateOnly?> ScheduledDate { get; init; }

    /// <summary>The Monday of the week. A day the task keeps must fall inside it.</summary>
    public PatchField<DateOnly?> PlanningWeekStart { get; init; }

    public PatchField<DateOnly?> DueDate { get; init; }

    public PatchField<int?> EstimateMinutes { get; init; }

    /// <summary>The whole set of the task's labels, replacing what it has. <c>null</c> or empty clears them.</summary>
    public PatchField<IReadOnlyList<Guid>?> LabelIds { get; init; }

    /// <summary>Who is doing it. The owner may hand it to anyone who can keep work; the assignee may hand it back.</summary>
    public PatchField<Guid?> AssigneeMembershipId { get; init; }

    public PatchField<Guid?> RecipeId { get; init; }

    public PatchField<Guid?> ContentCardId { get; init; }

    public PatchField<Guid?> MediaAssetId { get; init; }

    public PatchField<Guid?> WorkflowRunId { get; init; }

    public PatchField<string?> WorkflowStepKey { get; init; }

    /// <summary>Not editable: a task changes status through the move and complete routes. Refused when sent.</summary>
    public PatchField<object?> Status { get; init; }

    /// <summary>Not editable: a task's place in its list changes through the reorder route. Refused when sent.</summary>
    public PatchField<object?> Position { get; init; }
}

/// <summary>The checks an edit can fail on its own, before the task it edits has been read.</summary>
public static class UpdateWorkTaskInputChecks
{
    public static IEnumerable<(string Field, string Message)> Update(UpdateWorkTaskViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!WorkTaskConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            yield return ("expectedConcurrencyToken", "Send the concurrencyToken of the task you are editing.");
        }

        if (model.Status.IsSubmitted)
        {
            yield return ("status", "An edit does not change where a task stands. Move it, or complete it, instead.");
        }

        if (model.Position.IsSubmitted)
        {
            yield return ("position", "An edit does not change a task's place in its list. Reorder it instead.");
        }

        if (model.Title.TryGetSubmitted(out var title))
        {
            if (WorkTaskInputChecks.Normalize(title) is not { } trimmed)
            {
                yield return ("title", "A task needs a title.");
            }
            else if (trimmed.Length > WorkTaskPolicy.TitleMaxLength)
            {
                yield return ("title", $"A title can be at most {WorkTaskPolicy.TitleMaxLength} characters.");
            }
        }

        if (model.Description.TryGetSubmitted(out var description)
            && WorkTaskInputChecks.Normalize(description) is { Length: > WorkTaskPolicy.DescriptionMaxLength })
        {
            yield return ("description", $"A description can be at most {WorkTaskPolicy.DescriptionMaxLength} characters.");
        }

        if (model.Priority.TryGetSubmitted(out var priority))
        {
            if (priority is null)
            {
                yield return ("priority", "A task always has a priority: Low, Normal or High.");
            }
            else if (!Enum.IsDefined(priority.Value))
            {
                yield return ("priority", "Name a priority: Low, Normal or High.");
            }
        }

        if (model.PlanningWeekStart.TryGetSubmitted(out var week) && week is { } start && !WorkTaskWeek.IsStart(start))
        {
            yield return ("planningWeekStart", $"A planning week starts on a {WorkTaskWeek.StartsOn}.");
        }

        if (model.ScheduledDate.TryGetSubmitted(out var day) && day is { } date
            && week is { } named && WorkTaskWeek.IsStart(named) && !WorkTaskWeek.Holds(named, date))
        {
            yield return ("scheduledDate", "That day is not in that week.");
        }

        if (model.EstimateMinutes.TryGetSubmitted(out var estimate)
            && estimate is < 1 or > WorkTaskPolicy.EstimateMaxMinutes)
        {
            yield return ("estimateMinutes", $"An estimate is between 1 and {WorkTaskPolicy.EstimateMaxMinutes} minutes.");
        }

        if (model.LabelIds.TryGetSubmitted(out var labels) && labels is { } ids)
        {
            if (ids.Count > WorkTaskPolicy.MaxLabels)
            {
                yield return ("labelIds", $"A task may carry at most {WorkTaskPolicy.MaxLabels} labels.");
            }

            if (ids.Any(id => id == Guid.Empty))
            {
                yield return ("labelIds", "That is not an identifier.");
            }
            else if (ids.Distinct().Count() != ids.Count)
            {
                yield return ("labelIds", "Name each label once.");
            }
        }

        if (model.AssigneeMembershipId.TryGetSubmitted(out var assignee))
        {
            if (assignee is null)
            {
                yield return ("assigneeMembershipId", "A task always has somebody doing it.");
            }
            else if (assignee == Guid.Empty)
            {
                yield return ("assigneeMembershipId", "That is not an identifier.");
            }
        }

        foreach (var (field, link) in new[]
                 {
                     ("recipeId", model.RecipeId), ("contentCardId", model.ContentCardId),
                     ("mediaAssetId", model.MediaAssetId), ("workflowRunId", model.WorkflowRunId),
                 })
        {
            if (link.TryGetSubmitted(out var id) && id == Guid.Empty)
            {
                yield return (field, "That is not an identifier.");
            }
        }

        if (model.WorkflowStepKey.TryGetSubmitted(out var stepKey) && WorkTaskInputChecks.Normalize(stepKey) is { } step)
        {
            if (step.Length > WorkTaskPolicy.WorkflowStepKeyMaxLength)
            {
                yield return ("workflowStepKey", $"A step key is at most {WorkTaskPolicy.WorkflowStepKeyMaxLength} characters.");
            }
            else if (model.WorkflowRunId.TryGetSubmitted(out var run) && run is null)
            {
                yield return ("workflowStepKey", "A step is a step of a run. Keep the run, or clear the step too.");
            }
        }
    }
}

public sealed class UpdateWorkTaskViewModelValidator : AbstractValidator<UpdateWorkTaskViewModel>
{
    public UpdateWorkTaskViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in UpdateWorkTaskInputChecks.Update(model))
            {
                context.AddFailure(field, message);
            }
        });
}
