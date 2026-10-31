using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Gateways;

/// <summary>Whether this host can take payment, and whether a payment it takes is real money.</summary>
public enum BillingPaymentMode
{
    /// <summary>No payment provider. Every call answers <see cref="BillingPaymentOutcome.NotConfigured"/>.</summary>
    NotConfigured = 0,

    /// <summary>The provider's test mode: nothing charged is money (AP.6.1, test mode until the owner says otherwise).</summary>
    Test = 1,

    /// <summary>The provider's live mode. A host reaches it only when a deployment has allowed it.</summary>
    Live = 2,
}

/// <summary>How a plan is paid for. Each is its own price at the provider, with the same credits (B-51).</summary>
public enum BillingCycle
{
    /// <summary>Not declared. Never a price.</summary>
    Unspecified = 0,

    Monthly = 1,

    /// <summary>A year paid up front, at <c>PlanOption.AnnualMonthlyPrice</c> a month.</summary>
    Annual = 2,
}

/// <summary>How one call to the payment provider ended.</summary>
public enum BillingPaymentOutcome
{
    Succeeded = 1,

    /// <summary>This host has no payment provider. Terminal until it is configured.</summary>
    NotConfigured = 2,

    /// <summary>
    /// The plan or pack is not for sale on this host: a trial (B-52 — no card), a cycle a plan has no price for, or
    /// a key the catalogue does not have. Nothing was sent.
    /// </summary>
    NotOffered = 3,

    /// <summary>The provider refused the credential, or the credential may not do this. Terminal until replaced.</summary>
    CredentialRejected = 4,

    /// <summary>
    /// The provider understood the request and declined it — a customer or price it does not have, a parameter
    /// it will not take. Not worth repeating unchanged.
    /// </summary>
    Refused = 5,

    /// <summary>The provider asked for calls to slow down, and went on asking after the retries allowed.</summary>
    Throttled = 6,

    /// <summary>
    /// Unreachable, timed out, failed on its side, or not sent because the client's breaker is open. <strong>The
    /// outcome is unknown</strong>: the provider may have acted, so what it did is reconciled from its webhooks, never
    /// assumed from this answer (AP.6.2). A call the caller cancelled is unknown in the same way, and throws instead.
    /// </summary>
    Unavailable = 7,

    /// <summary>Answered with something this contract cannot read, or for the other mode. Not worth another attempt.</summary>
    Unreadable = 8,
}

/// <summary>The provider's customer for one account, as the provider created it.</summary>
/// <param name="ExternalCustomerId">The provider's id. Present exactly when the outcome is a success.</param>
/// <param name="IsLiveMode">Which of the provider's modes the customer lives in.</param>
public sealed record BillingCustomerCreation(
    BillingPaymentOutcome Outcome,
    string? ExternalCustomerId = null,
    bool IsLiveMode = false)
{
    public static BillingCustomerCreation Of(BillingPaymentOutcome outcome) => new(outcome);
}

/// <summary>A page at the provider the creator is sent to — a checkout, or the customer portal.</summary>
/// <param name="Url">Where to send the creator. Short-lived, and present exactly when the outcome is a success.</param>
/// <param name="SessionReference">
/// The provider's id for a checkout session, which its webhook carries back (AP.6.2, AP.6.3). Null for the portal.
/// </param>
/// <param name="ExpiresAt">When a checkout stops accepting payment, when the provider says.</param>
public sealed record BillingRedirect(
    BillingPaymentOutcome Outcome,
    Uri? Url = null,
    string? SessionReference = null,
    DateTimeOffset? ExpiresAt = null)
{
    public static BillingRedirect Of(BillingPaymentOutcome outcome) => new(outcome);
}

/// <summary>A first subscription: an account on the trial or with nothing running buys a plan (AP.6.1 decision).</summary>
/// <param name="AccountId">The Identity account buying. Travels to the provider only as a reference to reconcile by.</param>
/// <param name="ExternalCustomerId">The account's provider customer, from its <c>BillingCustomer</c> record.</param>
/// <param name="PlanKey">A catalogue key (<c>Billing:Plans</c>). The provider's price is looked up from it, never sent in.</param>
/// <param name="SuccessUrl">Where the provider returns the creator after paying. The session id is added to it.</param>
/// <param name="CancelUrl">Where the provider returns the creator who backs out.</param>
/// <param name="IdempotencyKey">
/// The caller's key for this attempt at a purchase. Sent again with the same request within a day — a retry after
/// <see cref="BillingPaymentOutcome.Unavailable"/> or a cancelled call — it is the same checkout, never a second one.
/// The provider also keeps a failure against a key, so a new attempt by the creator after a refusal or a provider
/// failure takes a new key; reusing it would only replay the failure.
/// </param>
public sealed record SubscriptionCheckoutRequest(
    string AccountId,
    string ExternalCustomerId,
    string PlanKey,
    BillingCycle Cycle,
    Uri SuccessUrl,
    Uri CancelUrl,
    string IdempotencyKey);

/// <summary>A credit pack, bought once (PRICE-008). The parameters mean what they do on a subscription checkout.</summary>
/// <param name="AccountId">The Identity account buying.</param>
/// <param name="ExternalCustomerId">The account's provider customer, from its <c>BillingCustomer</c> record.</param>
/// <param name="PackKey">A catalogue key (<c>Billing:Packs</c>).</param>
/// <param name="SuccessUrl">Where the provider returns the creator after paying. The session id is added to it.</param>
/// <param name="CancelUrl">Where the provider returns the creator who backs out.</param>
/// <param name="IdempotencyKey">The caller's key for this purchase.</param>
public sealed record PackCheckoutRequest(
    string AccountId,
    string ExternalCustomerId,
    string PackKey,
    Uri SuccessUrl,
    Uri CancelUrl,
    string IdempotencyKey);

/// <summary>The provider's customer portal, for the account behind <paramref name="ExternalCustomerId"/>.</summary>
/// <param name="ReturnUrl">Where the portal's "return" link goes.</param>
public sealed record BillingPortalRequest(string ExternalCustomerId, Uri ReturnUrl);

/// <summary>
/// The change a subscription's schedule has waiting for its next phase (B-54): a lower plan, or another cycle, from
/// <paramref name="StartsAt"/>.
/// </summary>
/// <param name="PlanKey">The catalogue key the next phase's price sells; null when no configured price is it.</param>
/// <param name="Cycle"><see cref="BillingCycle.Unspecified"/> when the price is not configured.</param>
public sealed record BillingScheduledPhase(string? PlanKey, BillingCycle Cycle, DateTimeOffset StartsAt);

/// <summary>
/// A subscription as the provider holds it now, read again rather than taken from an event (AP.6.2). Only what the
/// plan follows from, in the application's terms: the provider's price is already its catalogue key.
/// </summary>
/// <param name="ExternalSubscriptionId">The provider's id (<c>sub_…</c>).</param>
/// <param name="ExternalCustomerId">The customer it bills (<c>cus_…</c>), which a <c>BillingCustomer</c> maps to an account.</param>
/// <param name="AccountId">
/// The account the subscription's metadata names, as the checkout wrote it (AP.6.1); null when it names none. Never
/// trusted alone: it must agree with the customer's account.
/// </param>
/// <param name="IsLiveMode">Which of the provider's modes it lives in.</param>
/// <param name="ItemId">The subscription's one item (<c>si_…</c>), which an upgrade changes the price of.</param>
/// <param name="PlanKey">The catalogue key its current price sells; null when no configured price is it.</param>
/// <param name="CurrentPeriodEnd">When the period being paid for ends: where a downgrade or a cancellation lands.</param>
/// <param name="CancelAt">When it is set to end, if it is — a cancellation at period end included.</param>
/// <param name="EndedAt">When it ended, if it has.</param>
/// <param name="ScheduleId">The schedule managing it (<c>sub_sched_…</c>), if one is.</param>
/// <param name="NextPhase">What that schedule changes next, if anything still to come.</param>
/// <param name="LatestInvoicePaid">
/// Whether the subscription's latest invoice is paid. An upgrade invoices its proration at once (B-57), so a higher
/// plan whose invoice is still open has not been paid for, and does not land.
/// </param>
public sealed record BillingSubscriptionSnapshot(
    string ExternalSubscriptionId,
    string ExternalCustomerId,
    string? AccountId,
    bool IsLiveMode,
    BillingSubscriptionStatus Status,
    string? ItemId,
    string? PlanKey,
    BillingCycle Cycle,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? CancelAt,
    DateTimeOffset? EndedAt,
    string? ScheduleId,
    BillingScheduledPhase? NextPhase,
    bool LatestInvoicePaid = false);

/// <param name="Subscription">Present exactly when the outcome is a success.</param>
public sealed record BillingSubscriptionRead(BillingPaymentOutcome Outcome, BillingSubscriptionSnapshot? Subscription = null)
{
    public static BillingSubscriptionRead Of(BillingPaymentOutcome outcome) => new(outcome);
}

/// <summary>
/// A pack's checkout as the provider holds it now, read again rather than taken from an event (AP.6.3). Only what
/// deciding whether to grant follows from; what the pack is worth is the catalogue's, never the amount paid.
/// </summary>
/// <param name="ExternalSessionId">The provider's id (<c>cs_…</c>), which the grant answers to.</param>
/// <param name="ExternalCustomerId">The customer it billed (<c>cus_…</c>); null when the session named none.</param>
/// <param name="AccountId">
/// The account the session's metadata names, as the checkout wrote it (AP.6.1); null when it names none. Never
/// trusted alone: it must agree with the customer's account.
/// </param>
/// <param name="IsLiveMode">Which of the provider's modes it lives in.</param>
/// <param name="IsPackPurchase">
/// Whether this is a pack this host sold: a one-off payment carrying the pack marker the checkout wrote. False for a
/// subscription's checkout and for a session made in the provider's dashboard.
/// </param>
/// <param name="IsPaid">
/// Whether the provider has the money. False for a delayed payment method still settling, and for one that failed;
/// either way nothing is granted, and the payment's own event grants when it succeeds.
/// </param>
/// <param name="PackKey">
/// The catalogue key the price the provider charged sells; null when no configured price is it. Read from the price
/// rather than from the metadata, so a pack key edited in the provider's dashboard grants nothing.
/// </param>
/// <param name="ExternalPaymentReference">
/// The payment the session took (<c>pi_…</c>), which a refund or a dispute arrives about; null when the provider
/// reported none.
/// </param>
public sealed record BillingPackCheckoutSnapshot(
    string ExternalSessionId,
    string? ExternalCustomerId,
    string? AccountId,
    bool IsLiveMode,
    bool IsPackPurchase,
    bool IsPaid,
    string? PackKey,
    string? ExternalPaymentReference);

/// <param name="Checkout">Present exactly when the outcome is a success.</param>
public sealed record BillingPackCheckoutRead(BillingPaymentOutcome Outcome, BillingPackCheckoutSnapshot? Checkout = null)
{
    public static BillingPackCheckoutRead Of(BillingPaymentOutcome outcome) => new(outcome);
}

/// <summary>
/// A refund or a dispute as the provider holds it now (AP.6.3): which payment it reverses, and nothing about the
/// money. How many credits come back is this host's arithmetic over its own grant, never a figure from the provider.
/// </summary>
/// <param name="ExternalReference">The refund's charge or the dispute, as it was asked for.</param>
/// <param name="IsLiveMode">Which of the provider's modes it lives in.</param>
/// <param name="Kind">Which reversal it is, as the event's type said.</param>
/// <param name="ExternalPaymentReference">
/// The payment reversed (<c>pi_…</c>), which is what a purchase is found by; null when the provider reports none,
/// which no pack's payment ever does.
/// </param>
/// <param name="AccountId">The account the payment's metadata names, as the checkout wrote it; null when none.</param>
/// <param name="IsPackPurchase">
/// Whether the payment carries this host's pack marker. What tells a pack whose checkout has not been recorded yet
/// from something else the provider charged for — a subscription invoice, which takes no credits back.
/// </param>
/// <param name="IsReversed">
/// Whether the provider confirms money went back: a charge with something refunded, or a dispute that exists. False
/// takes nothing.
/// </param>
public sealed record BillingPaymentReversalSnapshot(
    string ExternalReference,
    bool IsLiveMode,
    BillingPackReversal Kind,
    string? ExternalPaymentReference,
    string? AccountId,
    bool IsPackPurchase,
    bool IsReversed);

/// <param name="Reversal">Present exactly when the outcome is a success.</param>
public sealed record BillingPaymentReversalRead(
    BillingPaymentOutcome Outcome, BillingPaymentReversalSnapshot? Reversal = null)
{
    public static BillingPaymentReversalRead Of(BillingPaymentOutcome outcome) => new(outcome);
}

/// <summary>
/// An upgrade on a running subscription, confirmed and paid by the creator at the provider (B-56, B-57): a portal page
/// that shows the proration, takes the payment and handles authentication.
/// </summary>
/// <param name="ExternalCustomerId">The account's provider customer.</param>
/// <param name="ExternalSubscriptionId">The subscription to change.</param>
/// <param name="SubscriptionItemId">Its one item, whose price changes.</param>
/// <param name="PlanKey">The catalogue key to move to. Its price is looked up, never sent in.</param>
/// <param name="ReturnUrl">Where the page's "back" link goes, if the creator does not confirm.</param>
/// <param name="CompletedUrl">Where the creator lands once they have confirmed.</param>
public sealed record PlanUpgradeRequest(
    string ExternalCustomerId,
    string ExternalSubscriptionId,
    string SubscriptionItemId,
    string PlanKey,
    BillingCycle Cycle,
    Uri ReturnUrl,
    Uri CompletedUrl);

/// <summary>
/// A change on a running subscription that waits for the end of the period being paid for (B-54): a downgrade, or
/// annual to monthly. Charged nothing now and refunded nothing.
/// </summary>
/// <param name="ExternalSubscriptionId">The subscription to change.</param>
/// <param name="ExistingScheduleId">The schedule already managing it, if one is; it is changed rather than another made.</param>
/// <param name="PlanKey">The catalogue key to move to from the period's end.</param>
/// <param name="IdempotencyKey">The caller's key for this change, as on a checkout.</param>
public sealed record ScheduledPlanChangeRequest(
    string ExternalSubscriptionId,
    string? ExistingScheduleId,
    string PlanKey,
    BillingCycle Cycle,
    string IdempotencyKey);

/// <summary>
/// The boundary to the payment provider, in the application's own terms (PRICE-010, AP.6.1).
/// </summary>
/// <remarks>
/// <para>
/// A gateway by backend.md's definition: it owns the provider call and nothing else. No provider type, field name,
/// status code or message crosses it — a plan is asked for by its catalogue key and a cycle, and the provider's
/// price for it is the adapter's to know. The one provider value that does cross is an opaque id: the customer a
/// <c>BillingCustomer</c> record maps an account to, and a checkout's session reference. Each is stored or compared,
/// never interpreted.
/// </para>
/// <para>
/// <strong>Nothing here changes what an account has.</strong> Starting a checkout charges nobody and grants
/// nothing; the plan and the credits move only when the provider's signed webhook says the payment happened
/// (AP.6.2, AP.6.3), because a creator can pay and never come back to the success page.
/// </para>
/// <para>
/// <strong>Cancelling a call does not undo it.</strong> A caller's token that fires mid-call throws, but the provider
/// may already have made the session; a retry keeps the same idempotency key so it gets that session back.
/// </para>
/// <para>
/// <strong>Changing a running subscription</strong> (AP.6.2) is two operations, because the portal keeps plan
/// switching off (B-57): an upgrade is a confirmation page the creator pays on, and a downgrade is a schedule whose
/// next phase starts when the paid period ends (B-54). Neither moves the plan either: the subscription's webhook does,
/// once the provider says the change happened.
/// </para>
/// </remarks>
public interface IBillingPaymentGateway
{
    /// <summary>Whether this host can take payment, and in which mode. Read without calling the provider.</summary>
    BillingPaymentMode Mode { get; }

    /// <summary>
    /// Creates the provider's customer for <paramref name="accountId"/>. The same account asked twice within a few
    /// minutes gets the same answer — the same customer, so two racing requests do not make two, and equally the same
    /// failure — and after that a fresh attempt, so one failure is not replayed for a day.
    /// </summary>
    Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken);

    Task<BillingRedirect> StartSubscriptionCheckoutAsync(SubscriptionCheckoutRequest request, CancellationToken cancellationToken);

    Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The portal: payment methods, invoices, billing details and cancelling at period end. Plan switching is off in
    /// it, because the provider can only defer a switch between prices of one product (AP.6.1 decision, B-54).
    /// </summary>
    Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The subscription as the provider holds it now (AP.6.2). A read: safe to repeat, and repeated within the call
    /// when the provider fails, which no write here is.
    /// </summary>
    Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// A pack's checkout as the provider holds it now (AP.6.3), with the price it charged resolved to a catalogue
    /// key. A read: safe to repeat.
    /// </summary>
    Task<BillingPackCheckoutRead> GetPackCheckoutAsync(string externalSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// The refund or dispute <paramref name="externalReference"/> names, and the payment it reverses (AP.6.3). A read:
    /// safe to repeat.
    /// </summary>
    /// <param name="kind">
    /// Which of the two it is, from the event's type. The provider holds a refund and a dispute as different objects,
    /// so this says which to ask for rather than leaving the adapter to guess from the reference.
    /// </param>
    Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
        string externalReference, BillingPackReversal kind, CancellationToken cancellationToken);

    /// <summary>
    /// A page at the provider where the creator confirms and pays for an upgrade (B-56): the proration invoiced at once,
    /// under the portal configuration that allows exactly that. The plan moves when the paid subscription's webhook
    /// arrives, never when this answers.
    /// </summary>
    /// <returns>
    /// <see cref="BillingPaymentOutcome.NotConfigured"/> when this host has no upgrade portal configuration;
    /// <see cref="BillingPaymentOutcome.NotOffered"/> when the plan and cycle have no price.
    /// </returns>
    Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Has the subscription move to another price when its paid period ends (B-54), through a schedule whose current
    /// phase runs to that end unchanged. Nothing is charged or refunded now.
    /// </summary>
    Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lets go of the schedule managing a subscription, so it carries on as it is with nothing waiting — a scheduled
    /// downgrade withdrawn, or cleared before an upgrade, which the portal will not make on a scheduled subscription.
    /// </summary>
    /// <param name="idempotencyKey">The caller's key for this release.</param>
    Task<BillingPaymentOutcome> ReleaseScheduleAsync(
        string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>The gateway on a host that was given no payment provider: the host starts, and every call says so.</summary>
public sealed class UnconfiguredBillingPaymentGateway : IBillingPaymentGateway
{
    public BillingPaymentMode Mode => BillingPaymentMode.NotConfigured;

    public Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken) =>
        Task.FromResult(BillingCustomerCreation.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingRedirect> StartSubscriptionCheckoutAsync(
        SubscriptionCheckoutRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(BillingRedirect.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(BillingRedirect.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(BillingRedirect.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(BillingSubscriptionRead.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingPackCheckoutRead> GetPackCheckoutAsync(string externalSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(BillingPackCheckoutRead.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
        string externalReference, BillingPackReversal kind, CancellationToken cancellationToken) =>
        Task.FromResult(BillingPaymentReversalRead.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(BillingRedirect.Of(BillingPaymentOutcome.NotConfigured));

    public Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(BillingPaymentOutcome.NotConfigured);

    public Task<BillingPaymentOutcome> ReleaseScheduleAsync(
        string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(BillingPaymentOutcome.NotConfigured);
}
