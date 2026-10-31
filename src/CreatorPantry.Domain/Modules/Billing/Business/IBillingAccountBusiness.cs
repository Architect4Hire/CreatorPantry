using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>
/// The signed-in account's billing: what its page shows, and the purchases and changes its buttons start (AP.6.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here changes a plan or grants a credit.</strong> Every command sends the creator to the provider, or
/// asks the provider to schedule something, and what it bought arrives by the verified webhook (B-57, B-60). So a
/// command that answers is not a purchase that happened, and the page waits for the webhook before it says so.
/// </para>
/// <para>
/// <strong>Two rules are reused from their owners in this module</strong> rather than restated (backend.md's
/// Business-to-Business exception): which plan the account is on, with its trial's end, is
/// <see cref="IAccountPlanBusiness"/>'s, and what its packs have left is <see cref="IAccountCreditSpendBusiness"/>'s.
/// </para>
/// </remarks>
internal interface IBillingAccountBusiness
{
    Task<BillingAccountServiceModel> GetAsync(string accountId, CancellationToken cancellationToken);

    Task<OperationResult<BillingRedirectServiceModel>> StartPlanCheckoutAsync(
        string accountId, string planKey, BillingCycle cycle, string? returnOrigin, string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<BillingRedirectServiceModel>> StartPackCheckoutAsync(
        string accountId, string packKey, string? returnOrigin, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<BillingRedirectServiceModel>> ChangePlanAsync(
        string accountId, string planKey, BillingCycle cycle, string? returnOrigin, string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<bool>> WithdrawPlanChangeAsync(
        string accountId, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<BillingRedirectServiceModel>> OpenPortalAsync(
        string accountId, string? returnOrigin, CancellationToken cancellationToken);

    Task<BillingCheckoutStatusServiceModel> GetCheckoutAsync(
        string accountId, BillingCheckoutKind kind, string externalSessionId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingAccountBusiness"/>
internal sealed class BillingAccountBusiness(
    IBillingAccountDataLayer data,
    IBillingCustomerDataLayer customers,
    IAccountPlanBusiness plans,
    IAccountCreditSpendBusiness credits,
    IOptions<BillingOptions> options,
    IClock clock) : IBillingAccountBusiness
{
    public async Task<BillingAccountServiceModel> GetAsync(string accountId, CancellationToken cancellationToken)
    {
        var mode = data.Mode;
        var available = mode != BillingPaymentMode.NotConfigured && options.Value.ReturnOrigins.Count > 0;
        var plan = await plans.ResolveAsync(accountId, null, cancellationToken);
        var subscription = await data.FindLiveSubscriptionAsync(accountId, cancellationToken);
        var trialEnded = plan?.TrialHasEnded(clock.UtcNow) == true;

        return new BillingAccountServiceModel(
            available,
            mode == BillingPaymentMode.Test,
            plan is null
                ? null
                : new BillingAccountPlanServiceModel(
                    plan.Key,
                    plan.Name,
                    plan.Credits,
                    plan.Period,
                    plan.DailyImageCap,
                    plan.TrialDays is not null,
                    plan.TrialEndsAt,
                    trialEnded),
            subscription is null ? null : Describe(subscription),
            await credits.BalanceAsync(accountId, cancellationToken),
            CanSubscribe: available && subscription is null,
            CanBuyPacks: available && !trialEnded,
            UpgradePlanKeys:
            [
                .. options.Value.Plans
                    .Where(target => target.TrialDays is null && IsUpgrade(subscription?.PlanKey, target))
                    .Select(target => target.Key),
            ]);
    }

    /// <remarks>
    /// A first subscription only (B-57): an account already paying changes plan instead, because a second Checkout
    /// would make a second subscription. The trial is not sold, and a paid plan is one this catalogue offers.
    /// </remarks>
    public async Task<OperationResult<BillingRedirectServiceModel>> StartPlanCheckoutAsync(
        string accountId, string planKey, BillingCycle cycle, string? returnOrigin, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (data.Mode == BillingPaymentMode.NotConfigured)
        {
            return Unavailable();
        }

        if (PaidPlan(planKey) is not { } plan)
        {
            return Fail(BillingAccountErrors.UnknownPlan, "That is not a plan you can choose.", ("planKey", "Choose one of the plans shown."));
        }

        if (Origin(returnOrigin) is { Error: { } refused })
        {
            return refused;
        }

        var origin = Origin(returnOrigin).Uri!;

        if (await data.FindLiveSubscriptionAsync(accountId, cancellationToken) is not null)
        {
            return Fail(BillingAccountErrors.AlreadySubscribed, "You already have a plan. Change it from this page instead.");
        }

        if (await CustomerAsync(accountId, cancellationToken) is not { } customer)
        {
            return Unavailable();
        }

        var redirect = await data.StartSubscriptionCheckoutAsync(
            new SubscriptionCheckoutRequest(
                accountId,
                customer.ExternalCustomerId,
                plan.Key,
                cycle,
                BillingAccountOrigins.Page(origin, "checkout=plan"),
                BillingAccountOrigins.Page(origin, "checkout=cancelled"),
                ProviderKey(accountId, "checkout.plan", idempotencyKey)),
            cancellationToken);

        return Redirected(redirect);
    }

    /// <remarks>
    /// Refused to an account whose trial has ended while it is still on it (B-61): no AI work is admitted then, pack
    /// credits included, so a pack would be credits it could not spend.
    /// </remarks>
    public async Task<OperationResult<BillingRedirectServiceModel>> StartPackCheckoutAsync(
        string accountId, string packKey, string? returnOrigin, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (data.Mode == BillingPaymentMode.NotConfigured)
        {
            return Unavailable();
        }

        var pack = options.Value.Packs.FirstOrDefault(option =>
            string.Equals(option.Key, packKey, StringComparison.OrdinalIgnoreCase));

        if (pack is null)
        {
            return Fail(BillingAccountErrors.UnknownPack, "That is not a pack you can buy.", ("packKey", "Choose one of the packs shown."));
        }

        if (Origin(returnOrigin) is { Error: { } refused })
        {
            return refused;
        }

        var origin = Origin(returnOrigin).Uri!;

        if ((await plans.ResolveAsync(accountId, null, cancellationToken))?.TrialHasEnded(clock.UtcNow) == true)
        {
            return Fail(
                BillingAccountErrors.PlanRequired,
                "Your free trial has ended. Choose a plan first — credit packs are spent alongside one.");
        }

        if (await CustomerAsync(accountId, cancellationToken) is not { } customer)
        {
            return Unavailable();
        }

        var redirect = await data.StartPackCheckoutAsync(
            new PackCheckoutRequest(
                accountId,
                customer.ExternalCustomerId,
                pack.Key,
                BillingAccountOrigins.Page(origin, "checkout=pack"),
                BillingAccountOrigins.Page(origin, "checkout=cancelled"),
                ProviderKey(accountId, "checkout.pack", idempotencyKey)),
            cancellationToken);

        return Redirected(redirect);
    }

    /// <remarks>
    /// <para>
    /// <strong>Up is a plan with a higher monthly price, and lands at once</strong> (B-56): the provider's page takes
    /// the prorated payment, and the plan moves when its webhook arrives. A change already scheduled is let go first,
    /// because the provider will not upgrade a scheduled subscription.
    /// </para>
    /// <para>
    /// <strong>Anything else waits for the end of the paid period</strong> (B-54) — a lower plan, or the same plan on
    /// the other cycle. Nothing is charged now; the subscription's own event records what is scheduled.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<BillingRedirectServiceModel>> ChangePlanAsync(
        string accountId, string planKey, BillingCycle cycle, string? returnOrigin, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (data.Mode == BillingPaymentMode.NotConfigured)
        {
            return Unavailable();
        }

        if (PaidPlan(planKey) is not { } target)
        {
            return Fail(BillingAccountErrors.UnknownPlan, "That is not a plan you can choose.", ("planKey", "Choose one of the plans shown."));
        }

        if (Origin(returnOrigin) is { Error: { } refused })
        {
            return refused;
        }

        var origin = Origin(returnOrigin).Uri!;

        if (await data.FindLiveSubscriptionAsync(accountId, cancellationToken) is not { } subscription)
        {
            return Fail(BillingAccountErrors.SubscriptionRequired, "You have no plan to change yet. Choose one to start.");
        }

        if (string.Equals(subscription.PlanKey, target.Key, StringComparison.OrdinalIgnoreCase) && subscription.Cycle == cycle)
        {
            return Fail(BillingAccountErrors.Unchanged, "You are already on that plan.");
        }

        if (IsUpgrade(subscription.PlanKey, target))
        {
            if (subscription.ItemId is null || await CustomerAsync(accountId, cancellationToken) is not { } customer)
            {
                return Unavailable();
            }

            if (subscription.ScheduleId is { } schedule
                && await data.ReleaseScheduleAsync(schedule, ProviderKey(accountId, "plan.release", idempotencyKey), cancellationToken)
                    is not BillingPaymentOutcome.Succeeded and var released)
            {
                return FromOutcome(released);
            }

            var redirect = await data.StartPlanUpgradeAsync(
                new PlanUpgradeRequest(
                    customer.ExternalCustomerId,
                    subscription.ExternalSubscriptionId,
                    subscription.ItemId,
                    target.Key,
                    cycle,
                    BillingAccountOrigins.Page(origin),
                    BillingAccountOrigins.Page(origin, "checkout=upgrade")),
                cancellationToken);

            return Redirected(redirect);
        }

        var scheduled = await data.SchedulePlanChangeAsync(
            new ScheduledPlanChangeRequest(
                subscription.ExternalSubscriptionId,
                subscription.ScheduleId,
                target.Key,
                cycle,
                ProviderKey(accountId, "plan.schedule", idempotencyKey)),
            cancellationToken);

        return scheduled is BillingPaymentOutcome.Succeeded
            ? OperationResult<BillingRedirectServiceModel>.Success(new BillingRedirectServiceModel(null, Scheduled: true))
            : FromOutcome(scheduled);
    }

    public async Task<OperationResult<bool>> WithdrawPlanChangeAsync(
        string accountId, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (await data.FindLiveSubscriptionAsync(accountId, cancellationToken) is not { ScheduleId: { } schedule })
        {
            return OperationResult<bool>.Failure(new OperationError(
                BillingAccountErrors.NothingScheduled, "No plan change is waiting.", new Dictionary<string, string[]>()));
        }

        var released = await data.ReleaseScheduleAsync(
            schedule, ProviderKey(accountId, "plan.withdraw", idempotencyKey), cancellationToken);

        return released is BillingPaymentOutcome.Succeeded
            ? OperationResult<bool>.Success(true)
            : OperationResult<bool>.Failure(FromOutcome(released).Error!);
    }

    /// <remarks>Only for an account that has paid: one that never has has nothing at the provider to manage.</remarks>
    public async Task<OperationResult<BillingRedirectServiceModel>> OpenPortalAsync(
        string accountId, string? returnOrigin, CancellationToken cancellationToken)
    {
        if (data.Mode == BillingPaymentMode.NotConfigured)
        {
            return Unavailable();
        }

        if (Origin(returnOrigin) is { Error: { } refused })
        {
            return refused;
        }

        var origin = Origin(returnOrigin).Uri!;

        if (await customers.FindAsync(accountId, cancellationToken) is not { } customer)
        {
            return Fail(BillingAccountErrors.NoBillingAccount, "There are no payments to manage yet.");
        }

        return Redirected(await data.OpenPortalAsync(
            new BillingPortalRequest(customer.ExternalCustomerId, BillingAccountOrigins.Page(origin)), cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <strong>Pending until the webhook says otherwise</strong>, never a guess from the provider's redirect: a
    /// browser arriving at the success URL is not a payment.
    /// </para>
    /// <para>
    /// A pack is complete when this account's purchase for that session is recorded — another account's session reads
    /// as pending, so nothing is said about it. A plan is complete when the account has a live subscription whose
    /// first invoice is paid, which is when the webhook has put the plan in force (B-60).
    /// </para>
    /// </remarks>
    public async Task<BillingCheckoutStatusServiceModel> GetCheckoutAsync(
        string accountId, BillingCheckoutKind kind, string externalSessionId, CancellationToken cancellationToken)
    {
        if (kind is BillingCheckoutKind.Pack)
        {
            return await data.FindPackPurchaseAsync(accountId, externalSessionId, cancellationToken) is { } purchase
                ? new BillingCheckoutStatusServiceModel(BillingCheckoutState.Complete, null, purchase.CreditsGranted)
                : Pending;
        }

        if (await data.FindLiveSubscriptionAsync(accountId, cancellationToken)
            is not { Status: BillingSubscriptionStatus.Active or BillingSubscriptionStatus.Trialing, LatestInvoicePaid: true })
        {
            return Pending;
        }

        var plan = await plans.ResolveAsync(accountId, null, cancellationToken);

        return new BillingCheckoutStatusServiceModel(BillingCheckoutState.Complete, plan?.Name, null);
    }

    private static readonly BillingCheckoutStatusServiceModel Pending = new(BillingCheckoutState.Pending, null, null);

    private BillingAccountSubscriptionServiceModel Describe(BillingSubscription subscription) => new(
        subscription.Status,
        subscription.PlanKey,
        subscription.Cycle,
        subscription.CurrentPeriodEnd,
        subscription.CancelAt,
        subscription.NextPlanKey,
        subscription.NextPlanKey is { } next ? options.Value.Plans.FirstOrDefault(plan => plan.Key == next)?.Name : null,
        subscription.NextCycle,
        subscription.NextPhaseStartsAt);

    /// <summary>
    /// Whether moving from <paramref name="currentKey"/> to <paramref name="target"/> is an upgrade, which lands at once
    /// (B-56): a plan with a higher monthly price. The one copy of the rule — the command follows it and the read sends
    /// it, so the button the page shows and what pressing it does cannot disagree.
    /// </summary>
    private bool IsUpgrade(string? currentKey, PlanOption target) =>
        PaidPlan(currentKey) is { } current && target.MonthlyPrice > current.MonthlyPrice;

    /// <summary>A plan the catalogue sells for money; never the trial, which nobody buys.</summary>
    private PlanOption? PaidPlan(string? key) =>
        key is null
            ? null
            : options.Value.Plans.FirstOrDefault(plan =>
                plan.TrialDays is null && string.Equals(plan.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The origin to send the creator back to, or why there is none: a host with none configured can take no payment,
    /// and an origin the request names that is not configured is the request's mistake, never a place to send anyone.
    /// </summary>
    private (Uri? Uri, OperationResult<BillingRedirectServiceModel>? Error) Origin(string? asked)
    {
        if (options.Value.ReturnOrigins.Count == 0)
        {
            return (null, Unavailable());
        }

        return BillingAccountOrigins.Resolve(options.Value.ReturnOrigins, asked) is { } origin
            ? (origin, null)
            : (null, Fail(
                BillingAccountErrors.Invalid,
                "That request could not be read.",
                ("returnOrigin", "This page's address is not one payments can return to.")));
    }

    private async Task<BillingCustomer?> CustomerAsync(string accountId, CancellationToken cancellationToken) =>
        await customers.EnsureAsync(accountId, cancellationToken) is { Outcome: BillingPaymentOutcome.Succeeded, Customer: { } customer }
            ? customer
            : null;

    /// <summary>
    /// The provider's idempotency key for one of this account's commands: the client's key, bound to the account and
    /// the operation and hashed to a fixed width, so the provider dedupes a retry and one key cannot cross accounts.
    /// </summary>
    private static string ProviderKey(string accountId, string operation, string idempotencyKey) =>
        "cp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}\n{operation}\n{idempotencyKey}")));

    private static OperationResult<BillingRedirectServiceModel> Redirected(BillingRedirect redirect) =>
        redirect is { Outcome: BillingPaymentOutcome.Succeeded, Url: { } url }
            ? OperationResult<BillingRedirectServiceModel>.Success(new BillingRedirectServiceModel(url))
            : FromOutcome(redirect.Outcome is BillingPaymentOutcome.Succeeded ? BillingPaymentOutcome.Unreadable : redirect.Outcome);

    /// <summary>
    /// What a provider answer that is not a success means to a creator: its refusal or no price is the request's
    /// problem; everything else — not configured, credentials, throttling, an outage, an answer that would not read — is
    /// payments being unavailable for now, and nothing about the provider's body reaches the response.
    /// </summary>
    private static OperationResult<BillingRedirectServiceModel> FromOutcome(BillingPaymentOutcome outcome) =>
        outcome is BillingPaymentOutcome.Refused or BillingPaymentOutcome.NotOffered
            ? Fail(BillingAccountErrors.Refused, "That could not be bought right now. Nothing was charged.")
            : Unavailable();

    private static OperationResult<BillingRedirectServiceModel> Unavailable() =>
        Fail(BillingAccountErrors.PaymentsUnavailable, "Payments aren't available right now. Nothing was charged.");

    private static OperationResult<BillingRedirectServiceModel> Fail(
        string code, string message, params (string Field, string Error)[] fields) =>
        OperationResult<BillingRedirectServiceModel>.Failure(fields.Length == 0
            ? new OperationError(code, message, new Dictionary<string, string[]>())
            : OperationError.Validation(code, message, fields));
}
