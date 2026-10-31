using FluentValidation;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>The refusals the low-credit notice's own routes can produce (B-59).</summary>
public static class AiLowCreditNoticeErrors
{
    /// <summary>
    /// The body did not say whether the notice should be on or off.
    /// </summary>
    /// <remarks>
    /// 400, from the default in <c>ProblemResults.StatusFor</c>: a body that names no setting is a malformed
    /// request rather than one the server understood and declined.
    /// </remarks>
    public const string SettingInvalid = "ai.quota.notice_invalid";
}

/// <summary>
/// Whether the signed-in account wants to be told when its credits run low (PRICE-011 as amended, B-59).
/// </summary>
/// <param name="Enabled">
/// Nullable, and that nullability is the mechanism rather than a convenience: a non-nullable
/// <see langword="bool"/> that a body simply left out would bind as <see langword="false"/> and quietly switch
/// the notice off. A body that says nothing is refused instead.
/// </param>
/// <remarks>
/// There is deliberately nowhere here to name an account. The route carries none either; the account comes
/// from the validated session.
/// </remarks>
public sealed record LowCreditNoticeViewModel(bool? Enabled);

/// <inheritdoc cref="LowCreditNoticeViewModel"/>
public sealed class LowCreditNoticeViewModelValidator : AbstractValidator<LowCreditNoticeViewModel>
{
    public LowCreditNoticeViewModelValidator() =>
        RuleFor(model => model.Enabled)
            .NotNull()
            .WithMessage("Say whether the low-credit notice should be on or off.");
}
