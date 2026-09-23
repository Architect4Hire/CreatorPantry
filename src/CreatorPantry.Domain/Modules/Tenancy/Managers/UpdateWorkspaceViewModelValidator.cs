using FluentValidation;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

public sealed class UpdateWorkspaceViewModelValidator : AbstractValidator<UpdateWorkspaceViewModel>
{
    public UpdateWorkspaceViewModelValidator()
    {
        RuleFor(model => (model.Name ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter a workspace name.")
            .MaximumLength(WorkspacePolicy.NameMaxLength)
            .OverridePropertyName(nameof(UpdateWorkspaceViewModel.Name));
    }
}
