using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Integration.Stripe;

/// <summary>
/// Which Stripe price sells each plan and pack, and the rest of what Stripe needs that is not a secret (AP.6.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The catalogue's keys on one side, Stripe's ids on the other, and nothing else.</strong> What a plan costs
/// and grants stays in <c>Billing:Plans</c>, the published figure (B-51); the price id is only where Stripe is told
/// to charge it. A price id never reaches a plan row, a grant or a ServiceModel — provider identifiers live in
/// integration records and configuration (external.md) — and the gateway resolves it from a key at the call.
/// </para>
/// <para>
/// <strong>Account-specific, so empty in source control.</strong> A price id names an object in one Stripe account,
/// in one mode; the ids for a test account are set on the machine that uses it (<c>Stripe:Prices</c> on the
/// AppHost, forwarded here). They are not secrets, but they are no more shareable than a key.
/// </para>
/// <para>
/// Checked at startup against the catalogue whenever Stripe is configured (<c>StripeBillingRegistration</c>): a
/// paid plan with no price would be a plan nobody can buy, found by the first creator who tries.
/// </para>
/// </remarks>
internal sealed class StripeBillingSettings
{
    public const string SectionName = $"{BillingOptions.SectionName}:Stripe";

    /// <summary>
    /// Whether a live key may be used. Off: until the owner says otherwise, Stripe runs in test mode only (AP.6.1),
    /// and a host handed a live key refuses to start rather than take real money nobody decided to take.
    /// </summary>
    public bool AllowLiveMode { get; set; }

    /// <summary>
    /// The portal configuration (<c>bpc_…</c>) sessions open with, or null for the account's default one. Either
    /// way it is the one set up with plan switching off and cancellation at period end (AP.6.1 decision).
    /// </summary>
    public string? PortalConfiguration { get; set; }

    /// <summary>
    /// The second portal configuration (<c>bpc_…</c>), which an upgrade's confirmation page opens under (AP.6.2): plan
    /// switching on — Stripe's confirmation flow needs it — and the proration invoiced at once (<c>always_invoice</c>).
    /// A deep link hides the rest of the portal, so the switcher it enables is never shown. Null on a host that offers
    /// no upgrades, which answers "not configured" to one.
    /// </summary>
    public string? UpgradePortalConfiguration { get; set; }

    public StripePriceSettings Prices { get; set; } = new();

    /// <summary>
    /// The plan and cycle a configured price sells, or null for a price this host does not sell — a pack's, one made
    /// in the provider's dashboard, or one since removed from configuration (AP.6.2).
    /// </summary>
    public (string PlanKey, BillingCycle Cycle)? PlanFor(string priceId)
    {
        foreach (var (key, prices) in Prices.Plans)
        {
            if (string.Equals(prices.Monthly, priceId, StringComparison.Ordinal))
            {
                return (key, BillingCycle.Monthly);
            }

            if (string.Equals(prices.Annual, priceId, StringComparison.Ordinal))
            {
                return (key, BillingCycle.Annual);
            }
        }

        return null;
    }

    /// <summary>The price for <paramref name="planKey"/> on <paramref name="cycle"/>, or null when it has none.</summary>
    public string? PriceFor(string planKey, BillingCycle cycle) =>
        Prices.Plans.TryGetValue(planKey, out var prices)
            ? cycle switch
            {
                BillingCycle.Monthly => prices.Monthly,
                BillingCycle.Annual => prices.Annual,
                _ => null,
            }
            : null;

    /// <summary>The price for <paramref name="packKey"/>, or null when it has none.</summary>
    public string? PriceFor(string packKey) => Prices.Packs.GetValueOrDefault(packKey);

    /// <summary>Every price id configured, for the checks that each is one and none is used twice.</summary>
    public IEnumerable<string?> AllPriceIds() =>
        Prices.Plans.Values.SelectMany(prices => new[] { prices.Monthly, prices.Annual }).Concat(Prices.Packs.Values);
}

internal sealed class StripePriceSettings
{
    /// <summary>By plan key. A trial has no entry: it is not bought (B-52).</summary>
    public Dictionary<string, StripePlanPrices> Plans { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>By pack key: one one-time price each.</summary>
    public Dictionary<string, string?> Packs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A plan's two recurring prices, one per <see cref="BillingCycle"/>, under one Stripe product per plan.</summary>
internal sealed class StripePlanPrices
{
    public string? Monthly { get; set; }

    public string? Annual { get; set; }
}
