using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// One subscription at the payment provider and the account it bills, as last read from the provider (PRICE-010,
/// AP.6.2). Platform-scoped and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The integration record a running subscription needs</strong> (external.md): the subscription, its one
/// item and the schedule managing it are what a plan change names (AP.6.5), and they live here rather than on
/// <see cref="AccountPlan"/>, which carries no provider reference at all.
/// </para>
/// <para>
/// <strong>A reading, not a source of truth.</strong> Every event about the subscription reads it again from the
/// provider and overwrites this row with what it found, so the row is never ahead of the provider and an out-of-order
/// event cannot leave it behind for long. The plan is decided from these rows — every subscription an account has,
/// so a cancelled one cannot lapse an account another one is paying for.
/// </para>
/// <para>
/// <strong>One row per subscription</strong>, keyed by the provider's id, so two Workers reading the same event write
/// one row; the row version decides between them, and the loser's message is retried against what won.
/// </para>
/// </remarks>
public class BillingSubscription
{
    public Guid Id { get; set; }

    /// <summary>The Identity account it bills, resolved through its customer's <see cref="BillingCustomer"/>.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The provider's subscription id (<c>sub_…</c>). Unique.</summary>
    public string ExternalSubscriptionId { get; set; } = string.Empty;

    /// <summary>The provider's customer (<c>cus_…</c>).</summary>
    public string ExternalCustomerId { get; set; } = string.Empty;

    public bool IsLiveMode { get; set; }

    public BillingSubscriptionStatus Status { get; set; }

    /// <summary>Its one item (<c>si_…</c>), which an upgrade changes the price of.</summary>
    public string? ItemId { get; set; }

    /// <summary>The catalogue key its current price sells; null when no configured price is it.</summary>
    public string? PlanKey { get; set; }

    public BillingCycle Cycle { get; set; }

    /// <summary>When the period being paid for ends.</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary>When it is set to end, if it is.</summary>
    public DateTimeOffset? CancelAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The schedule managing it (<c>sub_sched_…</c>), if one is.</summary>
    public string? ScheduleId { get; set; }

    /// <summary>The plan its schedule moves it to next, if one is waiting.</summary>
    public string? NextPlanKey { get; set; }

    public BillingCycle NextCycle { get; set; }

    public DateTimeOffset? NextPhaseStartsAt { get; set; }

    /// <summary>Whether its latest invoice is paid: what lets an upgrade land (B-56, B-60).</summary>
    public bool LatestInvoicePaid { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was last read from the provider.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
