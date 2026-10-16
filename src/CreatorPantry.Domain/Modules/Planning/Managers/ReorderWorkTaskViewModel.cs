using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>A new place for a task in the list it is already in (13A.28a).</summary>
/// <remarks>
/// Only a place: no status, day, week or anything else, which is a move or an edit. <see cref="BeforeTaskId"/>
/// names the task it goes immediately before, which must be in the same list; left out, it goes to the bottom.
/// </remarks>
public sealed record ReorderWorkTaskViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the read this reorder was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    public Guid? BeforeTaskId { get; init; }
}

/// <summary>The checks a reorder can fail on its own, before the task it reorders has been read.</summary>
public static class ReorderWorkTaskInputChecks
{
    public static IEnumerable<(string Field, string Message)> Reorder(ReorderWorkTaskViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!WorkTaskConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            yield return ("expectedConcurrencyToken", "Send the concurrencyToken of the task you are reordering.");
        }

        if (model.BeforeTaskId == Guid.Empty)
        {
            yield return ("beforeTaskId", "That is not an identifier.");
        }
    }
}

public sealed class ReorderWorkTaskViewModelValidator : AbstractValidator<ReorderWorkTaskViewModel>
{
    public ReorderWorkTaskViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in ReorderWorkTaskInputChecks.Reorder(model))
            {
                context.AddFailure(field, message);
            }
        });
}
