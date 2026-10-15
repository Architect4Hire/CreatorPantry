using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Finishes a run, once the server finds every required step's rule met (13A.10a).</summary>
/// <remarks>
/// Carries no progress, no ticks and no results: whether the run is done is the server's to find out from the
/// modules that own the work, and nothing the client says counts towards it.
/// </remarks>
public sealed class CompleteWorkflowRunViewModel
{
    [Description("Required. The run's `concurrencyToken` as last read. A run already completed answers as it stands, whatever the token.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class CompleteWorkflowRunViewModelValidator : AbstractValidator<CompleteWorkflowRunViewModel>
{
    public CompleteWorkflowRunViewModelValidator() =>
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
}
