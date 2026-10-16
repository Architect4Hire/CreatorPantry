using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>Where a task should go: another of its person's lists, and a place in it (13A.28).</summary>
/// <remarks>
/// <para>
/// <see cref="Status"/> names the list. For <see cref="WorkTaskStatus.Backlog"/>, a <see cref="ScheduledDate"/>
/// names a day's list and brings its week; without one the task joins the backlog of no day, in
/// <see cref="PlanningWeekStart"/> when that is sent and in the week it has when not. In progress and waiting
/// keep the task's day and week, so sending either with them is refused: changing the plan is an edit.
/// </para>
/// <para>
/// <see cref="BeforeTaskId"/> names the task it goes immediately before, which must be in the target list; left
/// out, the task goes to the bottom. Done is not a list a task is moved to — it is completed.
/// </para>
/// </remarks>
public sealed record MoveWorkTaskViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the read this move was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    public WorkTaskStatus? Status { get; init; }

    public DateOnly? ScheduledDate { get; init; }

    public DateOnly? PlanningWeekStart { get; init; }

    public Guid? BeforeTaskId { get; init; }
}

/// <summary>The checks a move can fail on its own, before the task it moves has been read.</summary>
public static class MoveWorkTaskInputChecks
{
    public static IEnumerable<(string Field, string Message)> Move(MoveWorkTaskViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!WorkTaskConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            yield return ("expectedConcurrencyToken", "Send the concurrencyToken of the task you are moving.");
        }

        switch (model.Status)
        {
            case null:
                yield return ("status", "Name where the task should go: Backlog, InProgress or Waiting.");
                break;
            case WorkTaskStatus.Done:
                yield return ("status", "A task is not moved to done. Complete it instead.");
                break;
            case { } status when !Enum.IsDefined(status):
                yield return ("status", "Name where the task should go: Backlog, InProgress or Waiting.");
                break;
            case WorkTaskStatus.InProgress or WorkTaskStatus.Waiting
                when model.ScheduledDate is not null || model.PlanningWeekStart is not null:
                yield return ("status", "A move to in progress or waiting keeps the task's day and week. Edit the task to change them.");
                break;
        }

        if (model.PlanningWeekStart is { } week && !WorkTaskWeek.IsStart(week))
        {
            yield return ("planningWeekStart", $"A planning week starts on a {WorkTaskWeek.StartsOn}.");
        }
        else if (model.PlanningWeekStart is { } start && model.ScheduledDate is { } day && !WorkTaskWeek.Holds(start, day))
        {
            yield return ("scheduledDate", "That day is not in that week.");
        }

        if (model.BeforeTaskId == Guid.Empty)
        {
            yield return ("beforeTaskId", "That is not an identifier.");
        }
    }
}

public sealed class MoveWorkTaskViewModelValidator : AbstractValidator<MoveWorkTaskViewModel>
{
    public MoveWorkTaskViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in MoveWorkTaskInputChecks.Move(model))
            {
                context.AddFailure(field, message);
            }
        });
}
