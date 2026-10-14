using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Points a step at the record its unfinished work is kept in (13A.7).</summary>
/// <remarks>
/// A pointer, never the work: every step's work is saved by the screen it happens on, in a record that module owns,
/// so the run keeps only where to find it. Nothing here carries a body.
/// </remarks>
public sealed class SaveWorkflowStepDraftViewModel
{
    [Description("Required. What kind of record holds the step's unfinished work.")]
    public WorkflowResourceKind? Kind { get; set; }

    [Description("Required. That record's id in this workspace.")]
    public Guid Id { get; set; }

    [Description("Required, on every save. The run's `concurrencyToken` as last read. A save that would change the draft against a newer one is refused; one that repeats the draft already held lands whatever the token.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class SaveWorkflowStepDraftViewModelValidator : AbstractValidator<SaveWorkflowStepDraftViewModel>
{
    public SaveWorkflowStepDraftViewModelValidator()
    {
        RuleFor(model => model.Kind)
            .Must(kind => kind is { } value && Enum.IsDefined(value))
            .WithMessage("Say what kind of record holds the draft.")
            .OverridePropertyName("kind");

        RuleFor(model => model.Id)
            .NotEmpty()
            .WithMessage("Name the record that holds the draft.")
            .OverridePropertyName("id");

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
    }
}
