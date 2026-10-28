using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Gateways;

/// <summary>How checking one delivery's signature ended (AP.6.2).</summary>
public enum BillingWebhookVerification
{
    /// <summary>Signed by the provider, recently, and readable as an event.</summary>
    Verified = 1,

    /// <summary>This host has no signing secret, so it can verify nothing.</summary>
    NotConfigured = 2,

    /// <summary>
    /// No signature, a malformed one, one made with another secret or over other bytes, or one older than the
    /// tolerance. Nothing in the body was read.
    /// </summary>
    SignatureInvalid = 3,

    /// <summary>The signature verified, and the body is not an event this contract can read.</summary>
    Unreadable = 4,
}

/// <summary>
/// The parts of a verified event the application keeps, in its own terms (AP.6.2). The event's object is not among
/// them: what an event says about a subscription is never acted on, because the subscription is read again first.
/// </summary>
/// <param name="ExternalEventId">The provider's event id (<c>evt_…</c>). What a replay is recognised by.</param>
/// <param name="EventType">The provider's type, as sent. Stored, never branched on outside the adapter.</param>
/// <param name="Kind">What the event is about.</param>
/// <param name="IsLiveMode">Which of the provider's modes the event is from.</param>
/// <param name="OccurredAt">When the provider made the event. Recorded, never used to order (Stripe: do not).</param>
/// <param name="ObjectReference">The id of the object the event is about (<c>sub_…</c>, <c>in_…</c>, <c>cs_…</c>).</param>
/// <param name="SubscriptionReference">
/// The subscription the event is about, when it is about one: the object itself, an invoice's subscription, or a
/// subscription checkout's. Null for a pack's checkout and for a charge.
/// </param>
public sealed record BillingProviderEventNotice(
    string ExternalEventId,
    string EventType,
    BillingEventKind Kind,
    bool IsLiveMode,
    DateTimeOffset OccurredAt,
    string? ObjectReference,
    string? SubscriptionReference);

/// <param name="Notice">Present exactly when the outcome is <see cref="BillingWebhookVerification.Verified"/>.</param>
public sealed record BillingWebhookVerificationResult(
    BillingWebhookVerification Outcome, BillingProviderEventNotice? Notice = null)
{
    public static BillingWebhookVerificationResult Of(BillingWebhookVerification outcome) => new(outcome);
}

/// <summary>
/// The provider's webhook signature, checked before a byte of the body is parsed (PRICE-010, AP.6.2, external.md).
/// </summary>
/// <remarks>
/// A gateway in the sense backend.md gives the word — the provider's concern, behind an application-owned interface
/// — though it makes no call: the signing scheme, the header's format and the event's shape are the provider's, and
/// nothing outside the adapter knows them. Synchronous because it is arithmetic over bytes already in hand.
/// </remarks>
public interface IBillingWebhookVerifier
{
    /// <param name="signatureHeader">The delivery's signature header as received, or null when it had none.</param>
    /// <param name="body">The raw body, exactly as received. Never re-serialised: one byte changed fails the check.</param>
    BillingWebhookVerificationResult Verify(string? signatureHeader, ReadOnlySpan<byte> body);
}

/// <summary>The verifier on a host given no signing secret: every delivery is refused as unverifiable.</summary>
public sealed class UnconfiguredBillingWebhookVerifier : IBillingWebhookVerifier
{
    public BillingWebhookVerificationResult Verify(string? signatureHeader, ReadOnlySpan<byte> body) =>
        BillingWebhookVerificationResult.Of(BillingWebhookVerification.NotConfigured);
}
