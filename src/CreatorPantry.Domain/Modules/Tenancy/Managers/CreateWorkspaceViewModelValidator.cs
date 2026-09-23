using FluentValidation;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

public sealed class CreateWorkspaceViewModelValidator : AbstractValidator<CreateWorkspaceViewModel>
{
    public CreateWorkspaceViewModelValidator()
    {
        RuleFor(model => (model.Name ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter a workspace name.")
            .MaximumLength(WorkspacePolicy.NameMaxLength)
            .OverridePropertyName(nameof(CreateWorkspaceViewModel.Name));
    }
}
