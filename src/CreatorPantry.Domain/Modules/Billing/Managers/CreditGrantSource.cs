namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>Where an <c>AccountCreditGrant</c>'s credits came from (PRICE-008, AP.5.1).</summary>
/// <remarks>
/// Who granted the credits, not a provider reference: what Stripe calls the purchase is the grant's
/// <c>ExternalReferenceId</c>, and anything more about it lives in Phase 6's integration records.
/// </remarks>
public enum CreditGrantSource
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>
    /// A credit pack paid for through a verified Stripe checkout (AP.6.3). Always carries the checkout session as
    /// its external reference, and is never granted by hand.
    /// </summary>
    Purchase = 1,

    /// <summary>A platform administrator, through an ops route: a goodwill credit or a correction (AP.5.1).</summary>
    Ops = 2,

    /// <summary>A promotion, granted by an operator through the same ops route and told apart from goodwill.</summary>
    Promo = 3,
}
