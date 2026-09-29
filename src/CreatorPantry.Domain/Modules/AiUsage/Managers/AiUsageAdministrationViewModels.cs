using FluentValidation;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Puts explicit quota terms in force for an account (USAGE-009).
/// </summary>
/// <param name="Allowance">
/// Null leaves the account on the configured platform allowance while still giving it a terms row of its own
/// — how an operator changes a period or a zone without also pinning a number that platform configuration
/// should keep setting.
/// </param>
/// <param name="Reason">
/// Why. Required, recorded verbatim in the platform audit row, and the reason this is a ViewModel rather than
/// four query parameters.
/// </param>
/// <remarks>
/// <strong>No account id.</strong> It comes from the route, never the body — the same rule the creator-facing
/// usage routes follow from the other direction, so a payload can never disagree with the resource it was
/// sent to.
/// </remarks>
public sealed record SetAccountAiQuotaViewModel(
    AiQuotaUnit Unit,
    decimal? Allowance,
    AiQuotaPeriodLength PeriodLength,
    int PeriodAnchor,
    string TimeZoneId,
    AiQuotaCarryOver CarryOver,
    decimal? CarryOverCap,
    string Reason);

/// <summary>Clears a quota, suspends AI access, or restores it. The reason is the whole payload.</summary>
public sealed record AccountAiQuotaReasonViewModel(string Reason);

/// <summary>
/// Shape validation only. The combinations the quota table's check constraints forbid are decided in Business,
/// where the platform defaults a null allowance falls back to are also known.
/// </summary>
public sealed class SetAccountAiQuotaViewModelValidator : AbstractValidator<SetAccountAiQuotaViewModel>
{
    public SetAccountAiQuotaViewModelValidator()
    {
        RuleFor(model => model.Reason).ApplyReasonRules(nameof(SetAccountAiQuotaViewModel.Reason));

        RuleFor(model => (model.TimeZoneId ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter an IANA time zone identifier, such as Europe/London.")
            .MaximumLength(AiUsagePolicy.TimeZoneIdMaxLength)
            .OverridePropertyName(nameof(SetAccountAiQuotaViewModel.TimeZoneId));

        RuleFor(model => model.Allowance)
            .GreaterThanOrEqualTo(0m).WithMessage("An allowance cannot be negative.")
            .When(model => model.Allowance is not null);

        RuleFor(model => model.CarryOverCap)
            .GreaterThanOrEqualTo(0m).WithMessage("A carry-over cap cannot be negative.")
            .When(model => model.CarryOverCap is not null);
    }
}

/// <inheritdoc cref="SetAccountAiQuotaViewModelValidator"/>
public sealed class AccountAiQuotaReasonViewModelValidator : AbstractValidator<AccountAiQuotaReasonViewModel>
{
    public AccountAiQuotaReasonViewModelValidator() =>
        RuleFor(model => model.Reason).ApplyReasonRules(nameof(AccountAiQuotaReasonViewModel.Reason));
}

/// <summary>One definition of a valid reason, so the four commands cannot drift apart on it.</summary>
internal static class AccountAiQuotaReasonRules
{
    public static IRuleBuilderOptions<T, string> ApplyReasonRules<T>(
        this IRuleBuilderInitial<T, string> rule, string propertyName) =>
        rule.Cascade(CascadeMode.Stop)
            .Must(reason => !string.IsNullOrWhiteSpace(reason))
            .WithMessage("Say why this change is being made; it is recorded in the audit trail.")
            .Must(reason => reason.Trim().Length >= AiUsageAdministrationPolicy.ReasonMinLength)
            .WithMessage($"A reason needs at least {AiUsageAdministrationPolicy.ReasonMinLength} characters.")
            .Must(reason => reason.Trim().Length <= AiUsageAdministrationPolicy.ReasonMaxLength)
            .WithMessage($"A reason may be at most {AiUsageAdministrationPolicy.ReasonMaxLength} characters.")
            .OverridePropertyName(propertyName);
}
