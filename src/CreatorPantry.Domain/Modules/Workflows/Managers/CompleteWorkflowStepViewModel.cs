using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Asks for the run's current step to be completed (13A.8).</summary>
/// <remarks>
/// Not a claim that it is done: the server checks the step's completion rule against the modules that own the
/// work, and completes it only if that rule is met. The only thing a client supplies is which records the step made,
/// and each is checked before it counts.
/// </remarks>
public sealed class CompleteWorkflowStepViewModel
{
    [Description("For a step whose job is to make a record — start a recipe, keep a picture — which record it made. Omit it to use the step's saved draft.")]
    public IReadOnlyList<WorkflowStepResultViewModel>? Results { get; set; }

    [Description("Required. The run's `concurrencyToken` as last read. A completion against a run that has since changed is refused; one for a step already complete answers as it stands.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class WorkflowStepResultViewModel
{
    [Description("Required. What kind of record the step made.")]
    public WorkflowResourceKind? Kind { get; set; }

    [Description("Required. That record's id in this workspace.")]
    public Guid Id { get; set; }
}

public sealed class CompleteWorkflowStepViewModelValidator : AbstractValidator<CompleteWorkflowStepViewModel>
{
    /// <summary>More than every kind there is would name something twice.</summary>
    public const int MaxResults = 8;

    public CompleteWorkflowStepViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");

        RuleFor(model => model.Results)
            .Must(results => results is null || results.Count <= MaxResults)
            .WithMessage($"Name at most {MaxResults} records.")
            .Must(results => results is null || results.All(result => result is not null))
            .WithMessage("Name each record by its kind and id.")
            .Must(results => results is null || results.Where(result => result?.Kind is not null).GroupBy(result => result!.Kind).All(group => group.Count() == 1))
            .WithMessage("Name each kind of record once.")
            .OverridePropertyName("results");

        RuleForEach(model => model.Results)
            .ChildRules(result =>
            {
                result.RuleFor(item => item.Kind)
                    .Must(kind => kind is { } value && Enum.IsDefined(value))
                    .WithMessage("Say what kind of record it is.")
                    .OverridePropertyName("kind");

                result.RuleFor(item => item.Id)
                    .NotEmpty()
                    .WithMessage("Name the record.")
                    .OverridePropertyName("id");
            })
            .When(model => model.Results is not null && model.Results.All(result => result is not null))
            .OverridePropertyName("results");
    }
}
