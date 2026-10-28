using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// One event the payment provider delivered, verified and stored before anything acts on it (PRICE-010, AP.6.2,
/// external.md: "store a provider event id and make processing idempotent"). Platform-scoped and never filtered by
/// workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The event id is unique, and that is the replay guard.</strong> A second delivery of one event — Stripe's
/// own retry, a manual resend, a replayed request inside the signature's tolerance — meets the index, and the route
/// answers 200 without storing or queuing anything again.
/// </para>
/// <para>
/// <strong>No payload.</strong> Only what names the event and the object it is about. Processing reads the
/// subscription again from the provider rather than trusting what an event said about it (B-57: order is never
/// assumed), so the body has no further use, and keeping it would be keeping a stranger's bytes for nothing. AP.6.3
/// finds a pack's checkout or charge again from <see cref="ObjectReference"/> the same way.
/// </para>
/// <para>
/// <strong>An integration record</strong>, so the provider's ids live here (external.md), never on a plan or a grant.
/// Nothing about the creator is stored but the account it resolved to, once it has; no foreign key to
/// <c>ApplicationUser</c>, for the reason <see cref="AccountPlan"/> gives.
/// </para>
/// </remarks>
public class BillingProviderEvent
{
    public Guid Id { get; set; }

    /// <summary>The provider's event id (<c>evt_…</c>). Unique.</summary>
    public string ExternalEventId { get; set; } = string.Empty;

    /// <summary>The provider's type, as sent (<c>customer.subscription.updated</c>).</summary>
    public string EventType { get; set; } = string.Empty;

    public bool IsLiveMode { get; set; }

    /// <summary>When the provider made the event. Recorded for an operator; never used to order (B-57).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The object the event is about (<c>sub_…</c>, <c>in_…</c>, <c>cs_…</c>, <c>ch_…</c>), when it names one.</summary>
    public string? ObjectReference { get; set; }

    /// <summary>The subscription the event is about, when it is about one.</summary>
    public string? SubscriptionReference { get; set; }

    public BillingProviderEventStatus Status { get; set; }

    /// <summary>The account the event resolved to, once processing has tied it to one.</summary>
    public string? AccountId { get; set; }

    /// <summary>A word from <see cref="BillingEventDetails"/>: why the row is where it is. Never provider text.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>
    /// How many times the Worker's sweep has queued this event again after it was left unprocessed — its message
    /// poisoned while the provider could not be read (AP.6.2). Bounded by <see cref="BillingWebhookPolicy.MaxRedrives"/>.
    /// </summary>
    public int Redrives { get; set; }

    /// <summary>When the sweep last queued it again, or null when it never has.</summary>
    public DateTimeOffset? RedrivenAt { get; set; }
}
