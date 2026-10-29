using CreatorPantry.Domain.Modules.Billing.Gateways;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The signed-in account's billing, as its billing page shows it (AP.6.5): the plan in force, the subscription behind
/// it, the pack balance, and what the account may buy now.
/// </summary>
/// <param name="PaymentsAvailable">Whether this host can take a payment at all; false shows the page read-only.</param>
/// <param name="IsTestMode">Whether payments go to the provider's test mode, which the page says so nobody is fooled.</param>
/// <param name="Plan">The plan in force; null when no plans are configured.</param>
/// <param name="Subscription">The account's live subscription; null when it has none.</param>
/// <param name="PackBalance">Unspent pack credits, which never reset (B-50).</param>
/// <param name="CanSubscribe">Whether a first subscription may be started: payments on and no live subscription.</param>
/// <param name="CanBuyPacks">Whether a pack may be bought: payments on, and not a trial that has ended (B-61).</param>
/// <param name="UpgradePlanKeys">
/// The paid plans a change to would land at once, paying the difference now (B-56); a change to any other waits for
/// the end of the paid period (B-54). Empty without a subscription. The server's rule, sent so the page names each
/// button by what it will do rather than working it out.
/// </param>
public sealed record BillingAccountServiceModel(
    bool PaymentsAvailable,
    bool IsTestMode,
    BillingAccountPlanServiceModel? Plan,
    BillingAccountSubscriptionServiceModel? Subscription,
    decimal PackBalance,
    bool CanSubscribe,
    bool CanBuyPacks,
    IReadOnlyList<string> UpgradePlanKeys);

/// <summary>The plan in force, in the terms the billing page states it.</summary>
/// <param name="IsTrial">Whether it is a trial.</param>
/// <param name="TrialEndsAt">When the trial ends, or ended; null on a paid plan (B-61).</param>
/// <param name="TrialEnded">Whether that has passed while the account is still on the trial.</param>
public sealed record BillingAccountPlanServiceModel(
    string Key,
    string Name,
    int Credits,
    PlanAllowancePeriod Period,
    int DailyImageCap,
    bool IsTrial,
    DateTimeOffset? TrialEndsAt,
    bool TrialEnded);

/// <summary>The account's live subscription, as the provider last reported it through a webhook (AP.6.2).</summary>
/// <param name="NextPlanKey">The plan a change already scheduled moves it to; null when nothing is scheduled.</param>
/// <param name="NextPlanName">That plan's name.</param>
/// <param name="NextChangeAt">When that change lands — the end of the paid period (B-54).</param>
public sealed record BillingAccountSubscriptionServiceModel(
    BillingSubscriptionStatus Status,
    string? PlanKey,
    BillingCycle Cycle,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? CancelAt,
    string? NextPlanKey,
    string? NextPlanName,
    BillingCycle NextCycle,
    DateTimeOffset? NextChangeAt);

/// <summary>
/// Where the creator goes next: a provider page to pay or manage on, or — for a change at period end — nowhere, because
/// it is already scheduled.
/// </summary>
/// <param name="Url">The provider's page; null when <paramref name="Scheduled"/>.</param>
/// <param name="Scheduled">True when a plan change was scheduled for the end of the period and nothing is to be paid.</param>
public sealed record BillingRedirectServiceModel(Uri? Url, bool Scheduled = false);

/// <summary>Whether what a Checkout bought has landed yet.</summary>
public enum BillingCheckoutState
{
    Unspecified = 0,

    /// <summary>The provider has not told this host yet; the page waits.</summary>
    Pending = 1,

    /// <summary>The plan is in force, or the pack's credits are granted.</summary>
    Complete = 2,
}

/// <summary>What a Checkout session the creator came back from has done so far (AP.6.5).</summary>
/// <param name="PlanName">The plan now in force, for a completed subscription.</param>
/// <param name="Credits">The credits granted, for a completed pack.</param>
public sealed record BillingCheckoutStatusServiceModel(BillingCheckoutState State, string? PlanName, decimal? Credits);

/// <summary>What a Checkout session was for, as the return URL says.</summary>
public enum BillingCheckoutKind
{
    Unspecified = 0,
    Plan = 1,
    Pack = 2,
}

/// <summary>A first subscription, through the provider's hosted Checkout (B-57).</summary>
public sealed class StartPlanCheckoutViewModel
{
    public string? PlanKey { get; set; }

    public BillingCycle? Cycle { get; set; }

    /// <summary>The page's own origin, to come back to. Accepted only when <c>Billing:ReturnOrigins</c> lists it.</summary>
    public string? ReturnOrigin { get; set; }
}

/// <summary>A pack, through the provider's hosted Checkout (B-57).</summary>
public sealed class StartPackCheckoutViewModel
{
    public string? PackKey { get; set; }

    /// <inheritdoc cref="StartPlanCheckoutViewModel.ReturnOrigin"/>
    public string? ReturnOrigin { get; set; }
}

/// <summary>Another plan for an account already paying: up at once, down at period end (B-54, B-56).</summary>
public sealed class ChangePlanViewModel
{
    public string? PlanKey { get; set; }

    public BillingCycle? Cycle { get; set; }

    /// <inheritdoc cref="StartPlanCheckoutViewModel.ReturnOrigin"/>
    public string? ReturnOrigin { get; set; }
}

/// <summary>The provider's portal: payment methods, invoices, cancelling at period end.</summary>
public sealed class OpenBillingPortalViewModel
{
    /// <inheritdoc cref="StartPlanCheckoutViewModel.ReturnOrigin"/>
    public string? ReturnOrigin { get; set; }
}

internal sealed class StartPlanCheckoutViewModelValidator : AbstractValidator<StartPlanCheckoutViewModel>
{
    public StartPlanCheckoutViewModelValidator()
    {
        RuleFor(model => model.PlanKey).NotEmpty().MaximumLength(BillingPolicy.PlanKeyMaxLength);
        RuleFor(model => model.Cycle).NotNull().Must(BillingAccountPolicy.IsCycle).WithMessage("Choose monthly or yearly.");
        RuleFor(model => model.ReturnOrigin).MaximumLength(BillingAccountPolicy.ReturnOriginMaxLength);
    }
}

internal sealed class StartPackCheckoutViewModelValidator : AbstractValidator<StartPackCheckoutViewModel>
{
    public StartPackCheckoutViewModelValidator()
    {
        RuleFor(model => model.PackKey).NotEmpty().MaximumLength(BillingPolicy.PackKeyMaxLength);
        RuleFor(model => model.ReturnOrigin).MaximumLength(BillingAccountPolicy.ReturnOriginMaxLength);
    }
}

internal sealed class ChangePlanViewModelValidator : AbstractValidator<ChangePlanViewModel>
{
    public ChangePlanViewModelValidator()
    {
        RuleFor(model => model.PlanKey).NotEmpty().MaximumLength(BillingPolicy.PlanKeyMaxLength);
        RuleFor(model => model.Cycle).NotNull().Must(BillingAccountPolicy.IsCycle).WithMessage("Choose monthly or yearly.");
        RuleFor(model => model.ReturnOrigin).MaximumLength(BillingAccountPolicy.ReturnOriginMaxLength);
    }
}

internal sealed class OpenBillingPortalViewModelValidator : AbstractValidator<OpenBillingPortalViewModel>
{
    public OpenBillingPortalViewModelValidator() =>
        RuleFor(model => model.ReturnOrigin).MaximumLength(BillingAccountPolicy.ReturnOriginMaxLength);
}

/// <summary>The billing page's fixed figures (AP.6.5).</summary>
public static class BillingAccountPolicy
{
    /// <summary>A browser origin is a scheme, a host and a port; anything longer is not one.</summary>
    public const int ReturnOriginMaxLength = 2048;

    /// <summary>The billing page, which every provider page comes back to.</summary>
    public const string PagePath = "/app/billing";

    /// <summary>A provider Checkout session id, as the success URL carries it back.</summary>
    public const int SessionIdMaxLength = 255;

    internal static bool IsCycle(BillingCycle? cycle) => cycle is BillingCycle.Monthly or BillingCycle.Annual;
}

/// <summary>Stable error codes for the billing routes (AP.6.5).</summary>
public static class BillingAccountErrors
{
    /// <summary>The request is not the right shape. 400.</summary>
    public const string Invalid = "billing.request.invalid";

    /// <summary>The plan is not a paid plan this catalogue offers. 422.</summary>
    public const string UnknownPlan = "billing.plan.unprocessable";

    /// <summary>The pack is not one this catalogue offers. 422.</summary>
    public const string UnknownPack = "billing.pack.unprocessable";

    /// <summary>
    /// The account already has a live subscription, so a second Checkout would make a second subscription (B-57).
    /// 409, <c>retry: reread</c>: the page reads the account again and offers a plan change instead.
    /// </summary>
    public const string AlreadySubscribed = "billing.checkout.subscribed.conflict";

    /// <summary>A plan change was asked of an account with no live subscription. 409, reread: Checkout is the way in.</summary>
    public const string SubscriptionRequired = "billing.plan_change.no_subscription.conflict";

    /// <summary>The account is already on that plan and cycle. 409, reread.</summary>
    public const string Unchanged = "billing.plan_change.unchanged.conflict";

    /// <summary>Nothing is scheduled to withdraw. 404.</summary>
    public const string NothingScheduled = "billing.plan_change.not_found";

    /// <summary>
    /// The account's trial has ended and it is on no plan, so a pack would be credits it could not spend (B-61). 403,
    /// <c>retry: none</c>: choosing a plan is the remedy.
    /// </summary>
    public const string PlanRequired = "billing.pack.plan_required.forbidden";

    /// <summary>The account has never paid, so there is no billing account at the provider to manage. 404.</summary>
    public const string NoBillingAccount = "billing.portal.not_found";

    /// <summary>
    /// Payments are not configured here, or the provider could not be reached or would not answer. 503,
    /// <c>retry: later</c>.
    /// </summary>
    public const string PaymentsUnavailable = "billing.payments.unavailable";

    /// <summary>The provider refused the request, or has no price for it. 422.</summary>
    public const string Refused = "billing.payments.refused.unprocessable";
}
