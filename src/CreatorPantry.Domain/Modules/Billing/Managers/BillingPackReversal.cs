namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>What took a pack's payment back (PRICE-008, B-50, AP.6.3).</summary>
/// <remarks>
/// Why the money came back, in the application's own words: the provider's event type is the adapter's to read, and
/// nothing outside it branches on that string. Both reversals do the same thing to the credits — the unspent part of
/// the grant that payment made is taken back, once — and are told apart so an operator answering "where did this
/// pack go" can see which it was.
/// </remarks>
public enum BillingPackReversal
{
    /// <summary>Nothing has been taken back; the purchase stands.</summary>
    None = 0,

    /// <summary>The payment was refunded, in whole or in part.</summary>
    Refund = 1,

    /// <summary>The payment was disputed with the card network. Winning one does not re-grant (AP.6.3).</summary>
    Dispute = 2,
}
