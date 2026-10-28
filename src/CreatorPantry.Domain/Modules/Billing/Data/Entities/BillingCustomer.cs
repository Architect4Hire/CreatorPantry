using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// Which customer at the payment provider one Identity account is (PRICE-010, AP.6.1): the integration record that
/// maps an account to Stripe, and the only place a Stripe customer id is stored. Platform-scoped and never filtered
/// by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The account's, never a workspace's</strong> (B-47), as the plan and the grants are: a person pays once
/// for the account they work in every workspace with, so nothing here is <see cref="IWorkspaceOwned"/>.
/// </para>
/// <para>
/// <strong>An integration record, so the provider's id lives here and nowhere else</strong> (external.md): not on
/// <see cref="AccountPlan"/>, not on a grant, not in a ServiceModel. Whatever needs the customer reads it from this
/// row by account, and a webhook's customer finds its account by the reverse lookup.
/// </para>
/// <para>
/// <strong>One per account per mode.</strong> Stripe's test and live modes are separate worlds — a test customer
/// does not exist to a live key — so an account that bought in test mode gets a new customer the day a host goes
/// live, rather than a test id handed to the live API. A customer id is unique outright: two accounts can never be
/// one payer.
/// </para>
/// <para>
/// <strong>Write-once</strong> (<see cref="IImmutableRecord"/>). The mapping is made when an account first needs
/// to pay and never edited; a customer deleted at Stripe is a new row, not this one rewritten. No foreign key to
/// <c>ApplicationUser</c>, as on the plan: account erasure is a documented path (9A.3), which removes this row and
/// leaves the provider's own record to the provider, which as merchant of record keeps it (B-58).
/// </para>
/// </remarks>
public class BillingCustomer : IImmutableRecord
{
    public Guid Id { get; set; }

    /// <summary>The Identity account. Never a workspace id and never a membership id.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The provider's customer id (<c>cus_…</c>). Opaque: stored and compared, never interpreted.</summary>
    public string ExternalCustomerId { get; set; } = string.Empty;

    /// <summary>Whether the customer is in the provider's live mode rather than its test mode.</summary>
    public bool IsLiveMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
