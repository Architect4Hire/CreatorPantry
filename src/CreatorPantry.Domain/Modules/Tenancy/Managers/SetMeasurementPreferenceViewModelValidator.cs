using FluentValidation;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

public sealed class SetMeasurementPreferenceViewModelValidator : AbstractValidator<SetMeasurementPreferenceViewModel>
{
    public SetMeasurementPreferenceViewModelValidator()
    {
        RuleFor(model => model.DefaultMeasurementSystem)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Choose metric or US customary.")
            .Must(system => WorkspacePolicy.IsSelectableMeasurementSystem(system!.Value))
            .WithMessage("Choose metric or US customary.");
    }
}
