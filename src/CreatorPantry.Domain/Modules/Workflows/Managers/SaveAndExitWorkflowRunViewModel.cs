using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Saves where the creator is on the run's current step, so they can leave and carry on later (13A.10).</summary>
/// <remarks>
/// Carries no address: the creator names which of the step's actions they were in, and the server fills that
/// action's route from the run's own links. Nothing the client sends becomes a route. Nor does it carry a status —
/// a run saved and left is still in progress.
/// </remarks>
public sealed class SaveAndExitWorkflowRunViewModel
{
    [Description("The record the step's unfinished work is kept in, when it has one the run may not hold yet — an autosave still on its way. Omit it to keep the draft the step already holds; it is never cleared.")]
    public WorkflowStepDraftPointerViewModel? Draft { get; set; }

    [Description("Which of the step's actions the creator was in, by its `capabilityKey` from `nextAction`. \"Continue\" goes back to it. Omit it to keep where the run last said.")]
    public string? CapabilityKey { get; set; }

    [Description("Required. The run's `concurrencyToken` as last read. A save and exit that has already landed answers as the run stands, whatever the token.")]
    public string? ExpectedConcurrencyToken { get; set; }
}

public sealed class WorkflowStepDraftPointerViewModel
{
    [Description("What kind of record holds the step's unfinished work.")]
    public WorkflowResourceKind? Kind { get; set; }

    [Description("That record's id in this workspace.")]
    public Guid Id { get; set; }
}

public sealed class SaveAndExitWorkflowRunViewModelValidator : AbstractValidator<SaveAndExitWorkflowRunViewModel>
{
    public SaveAndExitWorkflowRunViewModelValidator()
    {
        When(model => model.Draft is not null, () =>
        {
            RuleFor(model => model.Draft!.Kind)
                .Must(kind => kind is { } value && Enum.IsDefined(value))
                .WithMessage("Say what kind of record holds the draft.")
                .OverridePropertyName("draft.kind");

            RuleFor(model => model.Draft!.Id)
                .NotEmpty()
                .WithMessage("Name the record that holds the draft.")
                .OverridePropertyName("draft.id");
        });

        // Known to the registry here; whether it is one of this step's actions needs the run, and is the business's.
        RuleFor(model => model.CapabilityKey)
            .Must(key => key is null || WorkflowCapabilities.ByKey.ContainsKey(key))
            .WithMessage("Name one of the step's actions, or leave it out.")
            .OverridePropertyName("capabilityKey");

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(WorkflowRunConcurrencyToken.IsWellFormed)
            .WithMessage("Send the run's concurrencyToken as you last read it.")
            .OverridePropertyName("expectedConcurrencyToken");
    }
}
