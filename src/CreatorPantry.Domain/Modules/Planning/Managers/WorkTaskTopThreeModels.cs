using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>The caller's top three for today, in order, replacing whatever they chose before (13A.32).</summary>
/// <remarks>
/// The whole set, not a change to it: an empty list clears the choice, and sending the same list twice is the same
/// as sending it once. The day is the server's — today in the workspace's scheduling zone — never the client's.
/// </remarks>
public sealed record SetWorkTaskTopThreeViewModel
{
    /// <summary>At most three tasks, each once, first the one that matters most. Required; may be empty.</summary>
    public IReadOnlyList<Guid>? TaskIds { get; init; }
}

/// <summary>The checks a top three can fail on its own, before any task it names has been read.</summary>
public static class WorkTaskTopThreeInputChecks
{
    public static IEnumerable<(string Field, string Message)> Set(SetWorkTaskTopThreeViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.TaskIds is not { } ids)
        {
            yield return ("taskIds", "Send the tasks you chose, or an empty list to clear them.");
            yield break;
        }

        if (ids.Count > WorkTaskPolicy.TopThreeMax)
        {
            yield return ("taskIds", $"Choose at most {WorkTaskPolicy.TopThreeMax}.");
        }

        if (ids.Any(id => id == Guid.Empty))
        {
            yield return ("taskIds", "One of those is not an identifier.");
        }
        else if (ids.Distinct().Count() != ids.Count)
        {
            yield return ("taskIds", "Name each task once.");
        }
    }
}

public sealed class SetWorkTaskTopThreeViewModelValidator : AbstractValidator<SetWorkTaskTopThreeViewModel>
{
    public SetWorkTaskTopThreeViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in WorkTaskTopThreeInputChecks.Set(model))
            {
                context.AddFailure(field, message);
            }
        });
}

/// <summary>The caller's top three for one day, as it now stands (13A.32).</summary>
public sealed record WorkTaskTopThreeServiceModel
{
    /// <summary>The local day the choice is for, in the workspace's scheduling zone.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>The chosen tasks, by id, in the order they were put in. Empty when none are chosen.</summary>
    public required IReadOnlyList<Guid> TaskIds { get; init; }
}
