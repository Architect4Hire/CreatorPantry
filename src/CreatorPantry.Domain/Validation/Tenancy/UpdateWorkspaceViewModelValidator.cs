using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using FluentValidation;

namespace CreatorPantry.Domain.Validation.Tenancy;

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
