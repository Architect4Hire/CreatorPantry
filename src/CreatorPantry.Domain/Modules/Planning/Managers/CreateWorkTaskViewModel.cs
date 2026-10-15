using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>
/// The body of <c>POST .../work-tasks</c> (13A.24, TASK-002): quick capture.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A title is the whole requirement.</strong> Everything else here is optional and everything not
/// here is the server's. The workspace comes from the route and the caller's membership, the owner is the
/// caller, the assignee is the owner, the status is always <see cref="WorkTaskStatus.Backlog"/>, the place in
/// the group is the bottom of it, and the week is the one the creator is planning in now.
/// </para>
/// <para>
/// <strong>What is deliberately not here.</strong> A due date, an estimate and labels belong to editing a
/// task rather than capturing one; a status, an assignee and a place in the group are never the client's; and
/// a recurrence pointer is the materializer's alone, because only one task may exist per occurrence. None of
/// them is required to capture work, which is the point: a thought reaches the list in one field.
/// </para>
/// <para>
/// Every link names a record this workspace already owns. Each is checked through the module that owns it
/// before anything is written, and one that cannot be found is refused the same way whether it never existed
/// or belongs to someone else.
/// </para>
/// </remarks>
public sealed record CreateWorkTaskViewModel
{
    /// <summary>The person's own words. Required; stored as typed apart from surrounding space.</summary>
    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>Left out, a task is <see cref="WorkTaskPriority.Normal"/>.</summary>
    public WorkTaskPriority? Priority { get; init; }

    /// <summary>
    /// The day it is planned for, as <c>yyyy-MM-dd</c>, in the workspace's own zone. Sent without
    /// <see cref="PlanningWeekStart"/>, the week holding it is used.
    /// </summary>
    public DateOnly? ScheduledDate { get; init; }

    /// <summary>
    /// The week it is planned for, named by its first day, which must be a
    /// <see cref="WorkTaskWeek.StartsOn"/>. Left out, the week holding today in the workspace's zone is used;
    /// where the workspace has set no zone there is no honest "today", and the task is captured with no week
    /// at all rather than one guessed for it.
    /// </summary>
    public DateOnly? PlanningWeekStart { get; init; }

    /// <summary>The recipe the work is about, as it stands. A task tracks work; it does not pin content.</summary>
    public Guid? RecipeId { get; init; }

    public Guid? ContentCardId { get; init; }

    public Guid? MediaAssetId { get; init; }

    public Guid? WorkflowRunId { get; init; }

    /// <summary>A step of <see cref="WorkflowRunId"/>. Not valid without it.</summary>
    public string? WorkflowStepKey { get; init; }
}

/// <summary>
/// The shape rules for capturing a task, as <c>(field, message)</c>. One place, read by the validator at the
/// edge and again by Business as the backstop for a worker or plugin that reaches it without the facade.
/// </summary>
public static class WorkTaskInputChecks
{
    /// <summary>Trimmed, and null when nothing is left. The form every optional text field is stored in.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static IEnumerable<(string Field, string Message)> Create(CreateWorkTaskViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Normalize(model.Title) is not { } title)
        {
            yield return ("title", "A task needs a title.");
        }
        else if (title.Length > WorkTaskPolicy.TitleMaxLength)
        {
            yield return ("title", $"A title can be at most {WorkTaskPolicy.TitleMaxLength} characters.");
        }

        if (Normalize(model.Description) is { Length: > WorkTaskPolicy.DescriptionMaxLength })
        {
            yield return ("description", $"A description can be at most {WorkTaskPolicy.DescriptionMaxLength} characters.");
        }

        if (model.Priority is { } priority && !Enum.IsDefined(priority))
        {
            yield return ("priority", "Name a priority: Low, Normal or High.");
        }

        // The week is named by its first day, so a day in the middle of one is a request that does not say
        // what it means — and storing it would put two tasks of the same week in different weeks.
        if (model.PlanningWeekStart is { } week && !WorkTaskWeek.IsStart(week))
        {
            yield return ("planningWeekStart", $"A planning week starts on a {WorkTaskWeek.StartsOn}.");
        }

        // Only worth saying when the week is a week; otherwise the message above is the one to fix first.
        if (model.ScheduledDate is { } day
            && model.PlanningWeekStart is { } start
            && WorkTaskWeek.IsStart(start)
            && !WorkTaskWeek.Holds(start, day))
        {
            yield return ("scheduledDate", "That day is not in that week.");
        }

        if (Normalize(model.WorkflowStepKey) is { } stepKey)
        {
            if (model.WorkflowRunId is null)
            {
                yield return ("workflowStepKey", "Name the workflow run this is a step of.");
            }
            else if (stepKey.Length > WorkTaskPolicy.WorkflowStepKeyMaxLength)
            {
                yield return ("workflowStepKey", $"A step key is at most {WorkTaskPolicy.WorkflowStepKeyMaxLength} characters.");
            }
        }

        foreach (var (field, id) in new[]
                 {
                     ("recipeId", model.RecipeId), ("contentCardId", model.ContentCardId),
                     ("mediaAssetId", model.MediaAssetId), ("workflowRunId", model.WorkflowRunId),
                 })
        {
            if (id == Guid.Empty)
            {
                yield return (field, "That is not an identifier.");
            }
        }
    }
}

public sealed class CreateWorkTaskViewModelValidator : AbstractValidator<CreateWorkTaskViewModel>
{
    public CreateWorkTaskViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in WorkTaskInputChecks.Create(model))
            {
                context.AddFailure(field, message);
            }
        });
}
