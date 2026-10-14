using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Starts one guided journey (13A.5).</summary>
/// <remarks>
/// Carries no version, owner, workspace or status: a run starts on the newest version of its workflow, belongs to
/// the member who starts it, and lives in the workspace the route resolved.
/// </remarks>
public sealed class StartWorkflowRunViewModel
{
    [Description("Which journey to start, by its key, e.g. `recipe.test-and-approve`.")]
    public string? DefinitionKey { get; set; }

    [Description("What to call this run. Omit it to use the journey's own goal.")]
    public string? Title { get; set; }

    [Description("A record to start from, when the journey takes one — the recipe to improve, the picture to organize.")]
    public WorkflowStartingResourceViewModel? StartingResource { get; set; }
}

public sealed class WorkflowStartingResourceViewModel
{
    [Description("What kind of record it is.")]
    public WorkflowResourceKind? Kind { get; set; }

    [Description("The record's id in this workspace.")]
    public Guid Id { get; set; }
}

public sealed class StartWorkflowRunViewModelValidator : AbstractValidator<StartWorkflowRunViewModel>
{
    public StartWorkflowRunViewModelValidator(IWorkflowCatalogue catalogue)
    {
        RuleFor(model => model.DefinitionKey)
            .Must(key => key is not null && catalogue.Latest().Any(definition => definition.Key.Value == key))
            .WithMessage("Choose one of the journeys on offer.")
            .OverridePropertyName("definitionKey");

        RuleFor(model => model.Title)
            .Must(title => title is null || !string.IsNullOrWhiteSpace(title))
            .WithMessage("Give the run a name, or leave it out to use the journey's own.")
            .Must(title => title is null || title.Trim().Length <= WorkflowRunPolicy.TitleMaxLength)
            .WithMessage($"Keep the name to {WorkflowRunPolicy.TitleMaxLength} characters.")
            .OverridePropertyName("title");

        When(model => model.StartingResource is not null, () =>
        {
            RuleFor(model => model.StartingResource!.Kind)
                .Must(kind => kind is { } value && Enum.IsDefined(value))
                .WithMessage("Say what kind of record to start from.")
                .OverridePropertyName("startingResource.kind");

            RuleFor(model => model.StartingResource!.Id)
                .NotEmpty()
                .WithMessage("Name the record to start from.")
                .OverridePropertyName("startingResource.id");

            // Only a kind the journey actually takes: a record nothing in it would ever use is not a start.
            RuleFor(model => model)
                .Must(model => model.StartingResource!.Kind is not { } kind
                    || catalogue.Latest().FirstOrDefault(definition => definition.Key.Value == model.DefinitionKey) is not { } definition
                    || definition.StartsFrom.Contains(kind))
                .WithMessage("This journey cannot start from that kind of record.")
                .OverridePropertyName("startingResource.kind");
        });
    }
}
