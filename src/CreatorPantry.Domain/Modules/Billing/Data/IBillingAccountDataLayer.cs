using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>
/// What the signed-in account's billing page reads, and the provider calls behind its buttons (AP.6.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reads only what the webhooks wrote.</strong> A subscription and a pack purchase are recorded when the
/// provider's verified event arrives (AP.6.2, AP.6.3), and nothing here writes either: starting a Checkout changes
/// nothing on this side until that event lands, which is what the page's pending state waits for.
/// </para>
/// <para>
/// In the payment mode this host runs in, as every billing read is: a test-mode record says nothing about live.
/// </para>
/// </remarks>
internal interface IBillingAccountDataLayer
{
    /// <summary>Whether, and how, this host takes payments.</summary>
    BillingPaymentMode Mode { get; }

    /// <summary>The account's live subscription in this mode, if it has one (<see cref="IsLive"/>).</summary>
    Task<BillingSubscription?> FindLiveSubscriptionAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>The pack this account bought in that Checkout session; null when there is none yet, or it is another's.</summary>
    Task<BillingPackPurchase?> FindPackPurchaseAsync(
        string accountId, string externalSessionId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBillingPaymentGateway.StartSubscriptionCheckoutAsync"/>
    Task<BillingRedirect> StartSubscriptionCheckoutAsync(SubscriptionCheckoutRequest request, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBillingPaymentGateway.StartPackCheckoutAsync"/>
    Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBillingPaymentGateway.OpenPortalAsync"/>
    Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBillingPaymentGateway.StartPlanUpgradeAsync"/>
    Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBillingPaymentGateway.SchedulePlanChangeAsync"/>
    Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBillingPaymentGateway.ReleaseScheduleAsync"/>
    Task<BillingPaymentOutcome> ReleaseScheduleAsync(
        string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a subscription still holds the account: anything not ended, an unfinished first payment included —
    /// a second Checkout beside one still settling would make two subscriptions (B-57).
    /// </summary>
    static bool IsLive(BillingSubscription subscription) =>
        subscription.EndedAt is null
        && subscription.Status is BillingSubscriptionStatus.Trialing
            or BillingSubscriptionStatus.Active
            or BillingSubscriptionStatus.PastDue
            or BillingSubscriptionStatus.Incomplete;
}

internal sealed class BillingAccountDataLayer(
    IBillingWebhookRepository subscriptions,
    IBillingPackPurchaseRepository packs,
    IBillingPaymentGateway gateway) : IBillingAccountDataLayer
{
    public BillingPaymentMode Mode => gateway.Mode;

    public async Task<BillingSubscription?> FindLiveSubscriptionAsync(string accountId, CancellationToken cancellationToken)
    {
        if (gateway.Mode == BillingPaymentMode.NotConfigured)
        {
            return null;
        }

        var all = await subscriptions.ListSubscriptionsAsync(
            accountId, gateway.Mode == BillingPaymentMode.Live, cancellationToken);

        // The most recently changed, should the provider ever hold two: it is the one the plan follows (B-60).
        return all.Where(IBillingAccountDataLayer.IsLive).OrderByDescending(subscription => subscription.UpdatedAt).FirstOrDefault();
    }

    public async Task<BillingPackPurchase?> FindPackPurchaseAsync(
        string accountId, string externalSessionId, CancellationToken cancellationToken) =>
        await packs.FindBySessionAsync(externalSessionId, cancellationToken) is { } purchase
        && purchase.AccountId == accountId
        && purchase.IsLiveMode == (gateway.Mode == BillingPaymentMode.Live)
            ? purchase
            : null;

    public Task<BillingRedirect> StartSubscriptionCheckoutAsync(
        SubscriptionCheckoutRequest request, CancellationToken cancellationToken) =>
        gateway.StartSubscriptionCheckoutAsync(request, cancellationToken);

    public Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken) =>
        gateway.StartPackCheckoutAsync(request, cancellationToken);

    public Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken) =>
        gateway.OpenPortalAsync(request, cancellationToken);

    public Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken) =>
        gateway.StartPlanUpgradeAsync(request, cancellationToken);

    public Task<BillingPaymentOutcome> SchedulePlanChangeAsync(
        ScheduledPlanChangeRequest request, CancellationToken cancellationToken) =>
        gateway.SchedulePlanChangeAsync(request, cancellationToken);

    public Task<BillingPaymentOutcome> ReleaseScheduleAsync(
        string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken) =>
        gateway.ReleaseScheduleAsync(externalScheduleId, idempotencyKey, cancellationToken);
}
