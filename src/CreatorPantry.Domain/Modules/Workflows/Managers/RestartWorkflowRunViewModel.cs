using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Starts a run over: stops it, keeping it whole, and starts the same journey afresh (13A.9b).</summary>
public sealed class RestartWorkflowRunViewModel
{
    [Description("Required, and must be true: the creator confirmed starting over. Anything else changes nothing.")]
    public bool? Confirmed { get; set; }

    [Description("What to call the new run. Omit it to keep the old run's name.")]
    public string? Title { get; set; }

    [Description("Required. The run's `concurrencyToken` as last read.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class RestartWorkflowRunViewModelValidator : AbstractValidator<RestartWorkflowRunViewModel>
{
    public RestartWorkflowRunViewModelValidator()
    {
        RuleFor(model => model.Confirmed)
            .Equal(true)
            .WithMessage("Confirm that you want to start over.")
            .OverridePropertyName("confirmed");

        RuleFor(model => model.Title)
            .Must(title => title is null || !string.IsNullOrWhiteSpace(title))
            .WithMessage("Give the new run a name, or leave it out to keep this one's.")
            .Must(title => title is null || title.Trim().Length <= WorkflowRunPolicy.TitleMaxLength)
            .WithMessage($"Keep the name to {WorkflowRunPolicy.TitleMaxLength} characters.")
            .OverridePropertyName("title");

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
    }
}
