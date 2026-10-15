using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Stops a run for good, keeping it whole and everything it linked as it is (13A.10b).</summary>
/// <remarks>Not a finish: nothing here says the work is done, and nothing the run linked is touched.</remarks>
public sealed class AbandonWorkflowRunViewModel
{
    [Description("Required, and must be true: the creator confirmed stopping the run. Anything else changes nothing.")]
    public bool? Confirmed { get; set; }

    [Description("Why, in the creator's own words. Optional; kept with the run as it was written, trimmed.")]
    public string? Reason { get; set; }

    [Description("Required. The run's `concurrencyToken` as last read. A run already stopped answers as it stands, whatever the token.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class AbandonWorkflowRunViewModelValidator : AbstractValidator<AbandonWorkflowRunViewModel>
{
    public AbandonWorkflowRunViewModelValidator()
    {
        RuleFor(model => model.Confirmed)
            .Equal(true)
            .WithMessage("Confirm that you want to stop this run.")
            .OverridePropertyName("confirmed");

        RuleFor(model => model.Reason)
            .Must(reason => reason is null || !string.IsNullOrWhiteSpace(reason))
            .WithMessage("Say why, or leave it out.")
            .Must(reason => reason is null || reason.Trim().Length <= WorkflowRunPolicy.AbandonReasonMaxLength)
            .WithMessage($"Keep it to {WorkflowRunPolicy.AbandonReasonMaxLength} characters.")
            .OverridePropertyName("reason");

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
    }
}
