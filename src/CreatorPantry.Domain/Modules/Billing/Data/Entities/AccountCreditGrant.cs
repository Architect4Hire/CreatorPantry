using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// Credits granted to one Identity account beyond its period allowance — a pack bought, an operator's grant, a
/// promotion — and how many of them are left (PRICE-008, AP.5.1). Platform-scoped and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The account's, never a workspace's</strong> (B-47), on the reasoning <see cref="AccountPlan"/> gives
/// and in the same shape: keyed by account, no <c>WorkspaceId</c>, not <see cref="IWorkspaceOwned"/>. Credits an
/// account bought are spent wherever it works.
/// </para>
/// <para>
/// <strong>Kept until spent, and kept after.</strong> There is no expiry column, on purpose (B-50): a pack is
/// credits already paid for. Nor is a grant ever deleted (<see cref="IRetainedRecord"/>), because what it granted
/// and what became of it are the record a refund, a dispute or a creator's question is answered from.
/// </para>
/// <para>
/// <strong>A claw-back is a state on the row, not the row's removal.</strong> It takes what is unspent at that
/// moment into <see cref="CreditsClawedBack"/>, leaves <see cref="CreditsRemaining"/> at nought and stamps
/// <see cref="ClawedBackAt"/>. Spent credits are not recovered (B-50). It happens once: a clawed-back grant has
/// nothing left to take, and the check constraints keep it from being spent again.
/// </para>
/// <para>
/// <strong>What was spent is derived, never stored</strong>: granted − remaining − clawed back, which includes
/// credits an open hold has taken and not yet spent — <see cref="AccountCreditGrantDraw"/> says which. The schema holds
/// that difference at or above nought, so no write can spend credits a grant never had, and holds
/// <see cref="CreditsRemaining"/> at or above nought, so no write can make a balance negative.
/// </para>
/// <para>
/// <strong>A row version, because the balance is contended.</strong> Admission spends from it (AP.5.2) while a
/// refund may be clawing it back (AP.6.3); a writer that read a balance somebody has since moved is told rather
/// than allowed to write a figure computed from the old one.
/// </para>
/// <para>
/// No price, no pack key and no foreign key to <c>ApplicationUser</c>, as on the plan: what a pack cost is the
/// payment's record, and account erasure is a documented path (9A.3), not a cascade default.
/// </para>
/// </remarks>
public class AccountCreditGrant : IRetainedRecord
{
    public Guid Id { get; set; }

    /// <summary>The Identity account the credits belong to. Never a workspace id and never a membership id.</summary>
    public string AccountId { get; set; } = string.Empty;

    public CreditGrantSource Source { get; set; }

    /// <summary>How many credits the grant gave. Above nought, and never changed after the grant.</summary>
    public decimal CreditsGranted { get; set; }

    /// <summary>How many are left to spend. Starts at <see cref="CreditsGranted"/>; never below nought.</summary>
    public decimal CreditsRemaining { get; set; }

    /// <summary>The unspent credits a claw-back took; nought unless <see cref="ClawedBackAt"/> is set.</summary>
    public decimal CreditsClawedBack { get; set; }

    /// <summary>
    /// What the grant answers to outside this table: a Stripe checkout session for a purchase, an operator's
    /// ticket or campaign for the others. Unique within its source, which is what lets one checkout session
    /// grant once however often its webhook arrives (AP.6.3).
    /// </summary>
    public string? ExternalReferenceId { get; set; }

    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When the unspent credits were clawed back, or null while the grant stands.</summary>
    public DateTimeOffset? ClawedBackAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>
    /// What may change on a stored grant: its balance, and once, its claw-back. Who it is for, where it came from,
    /// how many it granted, what it answers to and when are fixed when it is made, and a clawed-back grant is final.
    /// </summary>
    /// <remarks>
    /// <see cref="CreditsRemaining"/> may rise as well as fall — a released hold returns credits to the grant they
    /// came from (AP.5.2) — so it is bounded by the check constraints rather than held to one direction here. A
    /// claw-back is the one change to <see cref="CreditsClawedBack"/> and <see cref="ClawedBackAt"/>, and the checks
    /// hold those two together.
    /// </remarks>
    public string? RefuseChange(Func<string, object?> original)
    {
        if (!Equals(original(nameof(AccountId)), AccountId)
            || !Equals(original(nameof(Source)), Source)
            || !Equals(original(nameof(CreditsGranted)), CreditsGranted)
            || !Equals(original(nameof(ExternalReferenceId)), ExternalReferenceId)
            || !Equals(original(nameof(GrantedAt)), GrantedAt))
        {
            return "who a grant is for, where its credits came from, how many it granted, its reference and when are fixed once it is made.";
        }

        if (original(nameof(ClawedBackAt)) is null)
        {
            return null;
        }

        // Final, with one exception: a hold that was open when the claw-back came hands its unspent credits to the
        // claw-back when it resolves (AP.5.2), so that figure may rise. Nothing else moves, and the checks keep the
        // balance at nought.
        return Equals(original(nameof(ClawedBackAt)), ClawedBackAt)
            && Equals(original(nameof(CreditsRemaining)), CreditsRemaining)
            && original(nameof(CreditsClawedBack)) is decimal before
            && CreditsClawedBack >= before
                ? null
                : "a grant that has been clawed back is final.";
    }
}
