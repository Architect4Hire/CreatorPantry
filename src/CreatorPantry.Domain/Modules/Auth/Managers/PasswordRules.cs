using FluentValidation;

namespace CreatorPantry.Domain.Modules.Auth.Managers;

internal static class PasswordRules
{
    /// <summary>The <see cref="AccountPolicy"/> length range; no composition rules.</summary>
    public static IRuleBuilderOptions<T, string> NewPassword<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter a password.")
            .MinimumLength(AccountPolicy.PasswordMinLength)
            .WithMessage($"Use at least {AccountPolicy.PasswordMinLength} characters.")
            .MaximumLength(AccountPolicy.PasswordMaxLength)
            .WithMessage($"Use no more than {AccountPolicy.PasswordMaxLength} characters.");

    public static IRuleBuilderOptions<T, string> AccountEmail<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter an email address.")
            .MaximumLength(AccountPolicy.EmailMaxLength)
            .EmailAddress().WithMessage("Enter a valid email address.");
}

public sealed class RequestPasswordResetViewModelValidator : AbstractValidator<RequestPasswordResetViewModel>
{
    public RequestPasswordResetViewModelValidator()
    {
        RuleFor(model => (model.Email ?? string.Empty).Trim())
            .AccountEmail()
            .OverridePropertyName(nameof(RequestPasswordResetViewModel.Email));
    }
}

public sealed class CompletePasswordResetViewModelValidator : AbstractValidator<CompletePasswordResetViewModel>
{
    /// <summary>Well above the length of Identity's encoded data-protection tokens.</summary>
    public const int TokenMaxLength = 2048;

    public CompletePasswordResetViewModelValidator()
    {
        RuleFor(model => (model.Email ?? string.Empty).Trim())
            .AccountEmail()
            .OverridePropertyName(nameof(CompletePasswordResetViewModel.Email));

        RuleFor(model => model.Token)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("The reset link is incomplete.")
            .MaximumLength(TokenMaxLength).WithMessage("The reset link is incomplete.");

        RuleFor(model => model.NewPassword).NewPassword();
    }
}

public sealed class VerifyCredentialsViewModelValidator : AbstractValidator<VerifyCredentialsViewModel>
{
    public VerifyCredentialsViewModelValidator()
    {
        // Shape only. No password-policy checks at sign-in: accounts may predate the current policy.
        RuleFor(model => (model.Email ?? string.Empty).Trim())
            .NotEmpty().WithMessage("Enter your email address.")
            .MaximumLength(AccountPolicy.EmailMaxLength)
            .OverridePropertyName(nameof(VerifyCredentialsViewModel.Email));

        RuleFor(model => model.Password)
            .NotEmpty().WithMessage("Enter your password.")
            .MaximumLength(AccountPolicy.PasswordMaxLength);
    }
}

public sealed class ValidateSessionViewModelValidator : AbstractValidator<ValidateSessionViewModel>
{
    public ValidateSessionViewModelValidator()
    {
        RuleFor(model => model.UserId).NotEmpty();
        RuleFor(model => model.SecurityStamp).NotEmpty();
    }
}

public sealed class ChangePasswordViewModelValidator : AbstractValidator<ChangePasswordViewModel>
{
    public ChangePasswordViewModelValidator()
    {
        RuleFor(model => model.CurrentPassword)
            .NotEmpty().WithMessage("Enter your current password.");

        RuleFor(model => model.NewPassword).NewPassword();
    }
}

public sealed class ConfirmEmailViewModelValidator : AbstractValidator<ConfirmEmailViewModel>
{
    public ConfirmEmailViewModelValidator()
    {
        RuleFor(model => (model.Email ?? string.Empty).Trim())
            .AccountEmail()
            .OverridePropertyName(nameof(ConfirmEmailViewModel.Email));

        RuleFor(model => model.Token)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("The confirmation link is incomplete.")
            .MaximumLength(CompletePasswordResetViewModelValidator.TokenMaxLength).WithMessage("The confirmation link is incomplete.");
    }
}
