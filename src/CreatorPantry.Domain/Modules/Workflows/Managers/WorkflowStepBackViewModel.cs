using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Goes back from the run's current step to the one before it (13A.9).</summary>
public sealed class WorkflowStepBackViewModel
{
    [Description("Required. The run's `concurrencyToken` as last read. A Back that has already landed answers as the run stands, whatever the token.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class WorkflowStepBackViewModelValidator : AbstractValidator<WorkflowStepBackViewModel>
{
    public WorkflowStepBackViewModelValidator() =>
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
}
