using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Passes over the run's current step, which its workflow must mark optional (13A.9a).</summary>
public sealed class SkipWorkflowStepViewModel
{
    [Description("Why, in the creator's own words. Optional; kept with the step as it was written, trimmed.")]
    public string? Reason { get; set; }

    [Description("Required. The run's `concurrencyToken` as last read. A skip that has already landed answers as the run stands, whatever the token.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class SkipWorkflowStepViewModelValidator : AbstractValidator<SkipWorkflowStepViewModel>
{
    public SkipWorkflowStepViewModelValidator()
    {
        RuleFor(model => model.Reason)
            .Must(reason => reason is null || !string.IsNullOrWhiteSpace(reason))
            .WithMessage("Say why, or leave it out.")
            .Must(reason => reason is null || reason.Trim().Length <= WorkflowRunPolicy.SkipReasonMaxLength)
            .WithMessage($"Keep it to {WorkflowRunPolicy.SkipReasonMaxLength} characters.")
            .OverridePropertyName("reason");

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
    }
}
