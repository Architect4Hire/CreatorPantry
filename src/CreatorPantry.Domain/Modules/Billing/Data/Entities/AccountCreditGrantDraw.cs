using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// What one hold on an account's AI allowance took from one of its grants, and what became of it (PRICE-008,
/// AP.5.2). Platform-scoped, counts only, and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The record that lets credits go back where they came from.</strong> Admission spends the period's
/// allowance first and then the account's grants, oldest first; a hold that settles below what it took, is released
/// or lapses gives the difference back — to this grant, not to whichever grant happens to be oldest by then. One row
/// per grant a hold drew on, so a hold that spanned two packs returns to both.
/// </para>
/// <para>
/// <strong>The grant's arithmetic still closes.</strong> While a draw is open its credits are off
/// <see cref="AccountCreditGrant.CreditsRemaining"/> and not yet spent; once it resolves, <see cref="CreditsSpent"/>
/// is spent for good and the rest is back on the grant — or, when the grant was clawed back while the hold was open,
/// on its <see cref="AccountCreditGrant.CreditsClawedBack"/>, recorded here as <see cref="CreditsClawedBack"/>. A
/// clawed-back grant never has a balance again (B-50).
/// </para>
/// <para>
/// <strong>Billing's record, keyed by the hold rather than tied to it.</strong> <see cref="HoldId"/> is the
/// AiUsage module's reservation id, carried as an id and never a foreign key, on the reasoning
/// <c>AccountAiQuotaReservation.AiOperationId</c> gives: another module's row is not this module's to keep alive.
/// AiUsage reaches it only through <c>IAccountCreditSpendFacade</c>.
/// </para>
/// <para>
/// Never deleted (<see cref="IRetainedRecord"/>), for the reason the grant is not: it is part of the answer to "where
/// did this pack's credits go".
/// </para>
/// </remarks>
public class AccountCreditGrantDraw : IRetainedRecord
{
    public Guid Id { get; set; }

    /// <summary>The grant the credits were taken from.</summary>
    public Guid GrantId { get; set; }

    /// <inheritdoc cref="GrantId"/>
    public AccountCreditGrant? Grant { get; set; }

    /// <summary>The account the grant belongs to, repeated so every query here is keyed by the account it asks about.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The hold that took them: an AiUsage reservation id. An id, not a foreign key.</summary>
    public Guid HoldId { get; set; }

    /// <summary>What the hold took from the grant at admission. Above nought, and never changed after.</summary>
    public decimal CreditsHeld { get; set; }

    /// <summary>
    /// What of <see cref="CreditsHeld"/> was spent, once the hold resolved; null while it is open. The rest went
    /// back to the grant, or to its claw-back.
    /// </summary>
    public decimal? CreditsSpent { get; set; }

    /// <summary>
    /// What of the unspent part joined the grant's claw-back rather than its balance, because the grant was clawed
    /// back while the hold was open. Nought otherwise.
    /// </summary>
    public decimal CreditsClawedBack { get; set; }

    public DateTimeOffset HeldAt { get; set; }

    /// <summary>When the hold settled, was released or lapsed; null while it is open.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// What may change on a stored draw: its resolution, once. Which grant, which account, which hold, how much and
    /// when are fixed when it is taken, and a resolved draw is final.
    /// </summary>
    public string? RefuseChange(Func<string, object?> original)
    {
        if (!Equals(original(nameof(GrantId)), GrantId)
            || !Equals(original(nameof(AccountId)), AccountId)
            || !Equals(original(nameof(HoldId)), HoldId)
            || !Equals(original(nameof(CreditsHeld)), CreditsHeld)
            || !Equals(original(nameof(HeldAt)), HeldAt))
        {
            return "which grant a draw took from, for which hold, how much and when are fixed once it is taken.";
        }

        return original(nameof(ResolvedAt)) is null
            ? null
            : "a draw that has resolved is final.";
    }
}
