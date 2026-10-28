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
/// Changing a running subscription — an upgrade charged at once, a downgrade at period end (B-54, B-56) — is not on
/// this interface yet. It is AP.6.2's, which owns those transitions.
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
}
