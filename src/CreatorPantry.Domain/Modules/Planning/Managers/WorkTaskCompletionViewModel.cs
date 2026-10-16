using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>A request to complete or reopen a task (13A.28b): only the read it was composed against.</summary>
/// <remarks>
/// Everything else is the server's — when, by whom, and what the task goes back to — so there is nothing else
/// to send. Which of the two it is, the route says.
/// </remarks>
public sealed record WorkTaskCompletionViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the read this request was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }
}

public sealed class WorkTaskCompletionViewModelValidator : AbstractValidator<WorkTaskCompletionViewModel>
{
    public WorkTaskCompletionViewModelValidator() =>
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkTaskConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the task.")
            .OverridePropertyName("expectedConcurrencyToken");
}
