using FluentValidation;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

public sealed class ResolveWorkspaceViewModelValidator : AbstractValidator<ResolveWorkspaceViewModel>
{
    public ResolveWorkspaceViewModelValidator()
    {
        RuleFor(model => (model.WorkspaceSlug ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("A workspace is required.")
            .MaximumLength(WorkspacePolicy.SlugMaxLength)
            .Matches(WorkspacePolicy.SlugPattern).WithMessage("Enter a valid workspace address.")
            .OverridePropertyName(nameof(ResolveWorkspaceViewModel.WorkspaceSlug));
    }
}
