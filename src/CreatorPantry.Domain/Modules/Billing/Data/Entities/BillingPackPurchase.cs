using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// One credit pack a creator paid for, and what became of that payment (PRICE-008/010, B-50, AP.6.3): the payment's
/// record beside the grant it made. Platform-scoped and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the grant alone is not enough.</strong> A grant answers to its checkout session, which is what makes
/// one session grant once (<see cref="AccountCreditGrant.ExternalReferenceId"/>). A refund or a dispute, though,
/// arrives about a <em>payment</em> and names no session, so without a record tying the two together there would be
/// no way back from a reversed payment to the credits it bought. That record is this row — and it is the same record
/// <see cref="AccountCreditGrant"/> points at when it says what a pack cost is the payment's business, not the
/// grant's.
/// </para>
/// <para>
/// <strong>An integration record</strong> (external.md), so the provider's ids live here and not on a grant: the
/// checkout session, the payment, and the refund or dispute that reversed it. Each is opaque — stored and compared,
/// never interpreted. Nothing about the creator is kept but the account, and no foreign key to
/// <c>ApplicationUser</c>, for the reason <see cref="AccountPlan"/> gives: erasure (9A.3) is a documented path, and
/// this row is one of the rows it will have to name.
/// </para>
/// <para>
/// <strong>One row per checkout session, and one per payment.</strong> The session is unique outright, so a webhook
/// delivered twice records one purchase; the payment is unique where it is known, because that is what a reversal
/// looks the purchase up by, and two purchases sharing a payment would leave which grant to claw back undecided.
/// </para>
/// <para>
/// <strong>A reversal is a state on the row, not the row's removal</strong> (<see cref="IRetainedRecord"/>), as it
/// is on the grant: it records which reversal it was, when, what it took, and the provider's reference for it.
/// Taking credits back happens once — the grant has nothing left after the first — so a refund followed by a
/// dispute on one payment records the first and leaves the second with nothing to do.
/// </para>
/// <para>
/// <strong>A row version, because the reversal is contended</strong>: two reversal events about one payment can be
/// processed at once, and the loser is told rather than allowed to write a second reversal over the first.
/// </para>
/// </remarks>
public class BillingPackPurchase : IRetainedRecord
{
    public Guid Id { get; set; }

    /// <summary>The Identity account that bought the pack. Never a workspace id and never a membership id.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The provider's checkout session (<c>cs_…</c>), which the grant also answers to. Unique.</summary>
    public string ExternalSessionId { get; set; } = string.Empty;

    /// <summary>
    /// The provider's payment the session took (<c>pi_…</c>), which a refund or a dispute names. Unique where it is
    /// known, and null only for a paid session the provider reported without one.
    /// </summary>
    public string? ExternalPaymentReference { get; set; }

    /// <summary>Which of the provider's modes the payment was made in.</summary>
    public bool IsLiveMode { get; set; }

    /// <summary>
    /// The catalogue key of the pack bought (<c>Billing:Packs</c>), read from the price the provider charged rather
    /// than from what the session's metadata claimed.
    /// </summary>
    public string PackKey { get; set; } = string.Empty;

    /// <summary>How many credits the grant gave. Above nought, and never changed after.</summary>
    public decimal CreditsGranted { get; set; }

    /// <summary>The grant the payment made.</summary>
    public Guid GrantId { get; set; }

    /// <inheritdoc cref="GrantId"/>
    public AccountCreditGrant? Grant { get; set; }

    public DateTimeOffset PurchasedAt { get; set; }

    /// <summary>What took the payment back, or <see cref="BillingPackReversal.None"/> while the purchase stands.</summary>
    public BillingPackReversal Reversal { get; set; }

    /// <summary>The provider's refund or dispute (<c>ch_…</c>, <c>du_…</c>), when one reversed the payment.</summary>
    public string? ExternalReversalReference { get; set; }

    /// <summary>
    /// The unspent credits the claw-back took from the grant; nought while the purchase stands. Spent credits are not
    /// recovered (B-50), so this is what was left at that moment and no more.
    /// </summary>
    public decimal CreditsClawedBack { get; set; }

    /// <summary>When the payment was reversed, or null while the purchase stands.</summary>
    public DateTimeOffset? ReversedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>
    /// What may change on a stored purchase: its reversal, once. Who bought, which session and payment, which pack,
    /// how many credits, which grant and when are fixed when it is recorded.
    /// </summary>
    public string? RefuseChange(Func<string, object?> original)
    {
        if (!Equals(original(nameof(AccountId)), AccountId)
            || !Equals(original(nameof(ExternalSessionId)), ExternalSessionId)
            || !Equals(original(nameof(ExternalPaymentReference)), ExternalPaymentReference)
            || !Equals(original(nameof(IsLiveMode)), IsLiveMode)
            || !Equals(original(nameof(PackKey)), PackKey)
            || !Equals(original(nameof(CreditsGranted)), CreditsGranted)
            || !Equals(original(nameof(GrantId)), GrantId)
            || !Equals(original(nameof(PurchasedAt)), PurchasedAt))
        {
            return "who bought a pack, which payment paid for it, which pack, how many credits, which grant and when are fixed once it is recorded.";
        }

        return original(nameof(ReversedAt)) is null
            ? null
            : "a purchase that has been reversed is final.";
    }
}
