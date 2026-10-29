namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>Where one provider event stands (AP.6.2).</summary>
public enum BillingProviderEventStatus
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Verified and stored, with its work queued on the outbox and not yet done.</summary>
    Received = 1,

    /// <summary>Its subscription was read again and the account's plan brought into line with it.</summary>
    Applied = 2,

    /// <summary>
    /// Nothing for this host to do: a type nobody handles, an event from the provider's other mode, or one that names
    /// no subscription. Stored so a replay of it is still a replay.
    /// </summary>
    Ignored = 3,

    /// <summary>
    /// Historic. A pack's checkout, refund or dispute, stored by AP.6.2 and left for AP.6.3 — which now acts on them
    /// as they arrive, so nothing is recorded in this state any more. Kept because rows already carry it, and they
    /// say truthfully that nothing was done with them.
    /// </summary>
    Deferred = 4,

    /// <summary>
    /// Could not be applied without an operator: an account the event cannot be tied to, a price no plan has, or an
    /// ops-scheduled change in the way. The row's <c>Detail</c> says which. Nothing was changed.
    /// </summary>
    NeedsAttention = 5,
}

/// <summary>What one provider event is about, in the application's terms (AP.6.2, B-57's event table).</summary>
/// <remarks>
/// The adapter reads the provider's type string into one of these; the type itself is stored as it came, because it
/// is the provider's record of what happened, but nothing outside the adapter branches on it. Stored since AP.6.3,
/// because processing happens after the delivery and a pack's checkout and a reversed charge are told apart by
/// this rather than by the provider's string — so the values are append-only, and <see cref="Other"/> is what a row
/// written before AP.6.3 carries.
/// </remarks>
public enum BillingEventKind
{
    /// <summary>A type nothing here handles.</summary>
    Other = 0,

    /// <summary>A subscription was created, changed or ended.</summary>
    SubscriptionChanged = 1,

    /// <summary>An invoice was paid: a renewal, a first payment, or a paid upgrade.</summary>
    InvoicePaid = 2,

    /// <summary>An invoice's payment failed, or needs the creator to authenticate it.</summary>
    InvoicePaymentFailed = 3,

    /// <summary>A checkout finished, or its delayed payment settled either way.</summary>
    CheckoutCompleted = 4,

    /// <summary>A charge was refunded, in whole or in part (AP.6.3).</summary>
    ChargeRefunded = 5,

    /// <summary>A charge was disputed with the card network (AP.6.3).</summary>
    ChargeDisputed = 6,
}

/// <summary>Where a provider subscription stands, in the application's terms (AP.6.2).</summary>
public enum BillingSubscriptionStatus
{
    /// <summary>A status this contract does not know. Read as needing attention, never guessed at.</summary>
    Unspecified = 0,

    /// <summary>Made, and its first payment not taken yet. No plan until it is.</summary>
    Incomplete = 1,

    /// <summary>Its first payment never came. Ended.</summary>
    IncompleteExpired = 2,

    /// <summary>A provider-side trial. CreatorPantry never sets one (B-52), so it is read as active.</summary>
    Trialing = 3,

    Active = 4,

    /// <summary>A renewal failed and the provider is retrying it. The plan holds where it is.</summary>
    PastDue = 5,

    /// <summary>The provider gave up retrying and left it open. Ended, for the plan's purposes.</summary>
    Unpaid = 6,

    Canceled = 7,

    /// <summary>Paused by the provider. Ended, for the plan's purposes.</summary>
    Paused = 8,
}

/// <summary>Stable error codes for the payment provider's webhook route (AP.6.2).</summary>
public static class BillingWebhookErrors
{
    /// <summary>
    /// The signature header is missing, malformed, from another secret, or older than the tolerance. Maps to 400,
    /// <c>retry: none</c>: the same bytes will never verify.
    /// </summary>
    public const string SignatureInvalid = "billing.webhook.signature_invalid";

    /// <summary>
    /// The signature verified, but the body is not an event this contract can read. Maps to 400: a payload the
    /// provider signed and this host cannot parse is a contract change, for an operator, not a retry.
    /// </summary>
    public const string Unreadable = "billing.webhook.unreadable";

    /// <summary>
    /// This host has no webhook signing secret, so it can verify nothing. Maps to 503, <c>retry: later</c>: the
    /// provider redelivers for days, so an event refused while a secret is being set arrives again once it is.
    /// </summary>
    public const string Unavailable = "billing.webhook.unavailable";

    /// <summary>
    /// The body is larger than the route accepts — a chunked one the action's own limit could not stop before it was
    /// read (AP.7.2). Maps to 413 from the suffix, <c>retry: none</c>: the same event is the same size every time.
    /// </summary>
    public const string PayloadTooLarge = "billing.webhook.payload_too_large";
}

/// <summary>The webhook route's fixed limits (AP.6.2).</summary>
public static class BillingWebhookPolicy
{
    /// <summary>
    /// The largest event body taken: on the API action and on the gateway route, which
    /// <c>UploadRouteLimitDriftTests</c> holds equal. A subscription or invoice event is a few kilobytes; this is
    /// headroom for a long invoice, and a bound on what an unauthenticated caller can make the edge read.
    /// </summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>How old a signature may be (Stripe's own libraries' default). Never zero: zero disables the check.</summary>
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>A provider event type as stored. Stripe's longest today is under 60 characters.</summary>
    public const int EventTypeMaxLength = 100;

    /// <summary>A short outcome word on an event row, from a fixed vocabulary.</summary>
    public const int DetailMaxLength = 64;

    /// <summary>The outbox routing key the Worker's handler is registered under.</summary>
    public const string OutboxMessageType = "billing.provider-event";

    /// <summary>
    /// How long an event may sit unprocessed before the Worker's sweep queues it again. Well past the outbox's own
    /// retries (about two and a half minutes), so a message still being retried is not doubled for nothing — and a
    /// doubled one would do no harm: processing is idempotent.
    /// </summary>
    public static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(15);

    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan RedriveInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times an event is queued again before it is left for an operator: a day of a provider not answering,
    /// at <see cref="StrandedAfter"/> apart. Stripe's own redelivery is spent once this host has answered 200.
    /// </summary>
    public const int MaxRedrives = 96;

    /// <summary>How many stranded events one sweep queues again.</summary>
    public const int RedriveBatchSize = 50;
}

/// <summary>What the webhook route did with one delivery (AP.6.2). The route answers 200 either way.</summary>
/// <param name="Replayed">True when the event was already stored: nothing was written or queued again.</param>
public sealed record BillingWebhookReceiptServiceModel(bool Replayed);

/// <summary>What processing one stored event did (AP.6.2), for the Worker's handler and the tests.</summary>
/// <param name="Detail">A word from <see cref="BillingEventDetails"/>, never provider text.</param>
public sealed record BillingProviderEventOutcome(BillingProviderEventStatus Status, string Detail);

/// <summary>
/// The fixed words an event row's <c>Detail</c> may hold, so a row says why without carrying anything the provider
/// sent or anything about the creator.
/// </summary>
public static class BillingEventDetails
{
    public const string AlreadyProcessed = "already_processed";
    public const string NotHandled = "not_handled";
    public const string OtherMode = "other_mode";
    public const string NoSubscription = "no_subscription";
    /// <summary>Historic: what AP.6.2 wrote on a pack's event before AP.6.3 acted on one. Kept for the rows that carry it.</summary>
    public const string PackOwnedByAp63 = "deferred_to_packs";
    public const string ProviderRefused = "provider_refused";
    public const string ProviderUnreadable = "provider_unreadable";
    public const string CustomerUnknown = "customer_unknown";
    public const string AccountMismatch = "account_mismatch";
    public const string UnknownPrice = "unknown_price";
    public const string UnknownStatus = "unknown_status";
    public const string NoCatalogue = "no_catalogue";
    public const string OpsChangeScheduled = "ops_change_scheduled";
    public const string Unchanged = "unchanged";
    public const string Changed = "changed";

    // A pack's purchase and its reversal (AP.6.3).

    /// <summary>A checkout event naming no session, so there is nothing to read again.</summary>
    public const string NoCheckout = "no_checkout";

    /// <summary>A reversal event naming no charge or dispute.</summary>
    public const string NoCharge = "no_charge";

    /// <summary>
    /// Not a pack this host sold: a session or payment with no pack marker on it, or a reversal of something else
    /// the provider charged for — a subscription invoice's refund, which takes no credits back (B-54).
    /// </summary>
    public const string NotAPack = "not_a_pack";

    /// <summary>A pack's checkout whose payment has not settled. Nothing granted; the payment's own event grants.</summary>
    public const string NotPaid = "not_paid";

    /// <summary>A reversal the provider does not confirm on re-reading. Nothing taken back.</summary>
    public const string NotReversed = "not_reversed";

    public const string Granted = "granted";

    public const string ClawedBack = "clawed_back";

    /// <summary>The purchase names a grant that is not there: an operator's, since nothing deletes one.</summary>
    public const string GrantMissing = "grant_missing";

    /// <summary>
    /// The checkout already granted credits to another account, or a different number of them — a pack's configured
    /// credits changed between two deliveries of one session. Nothing granted.
    /// </summary>
    public const string GrantMismatch = "grant_mismatch";
    public const string Held = "held_past_due";
    public const string Pending = "pending_first_payment";
    public const string UpgradeUnpaid = "upgrade_unpaid";
    public const string ProviderUnavailable = "provider_unavailable";
}
