using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Models.ViewModels.Auth;
using FluentValidation;

namespace CreatorPantry.Domain.Validation.Auth;

public sealed class RegisterUserViewModelValidator : AbstractValidator<RegisterUserViewModel>
{
    public RegisterUserViewModelValidator()
    {
        // JSON null binds to these non-nullable strings, so guard before trimming.
        RuleFor(model => (model.Email ?? string.Empty).Trim())
            .AccountEmail()
            .OverridePropertyName(nameof(RegisterUserViewModel.Email));

        RuleFor(model => model.Password).NewPassword();

        RuleFor(model => (model.DisplayName ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter a display name.")
            .MaximumLength(AccountPolicy.DisplayNameMaxLength)
            .OverridePropertyName(nameof(RegisterUserViewModel.DisplayName));
    }
}
