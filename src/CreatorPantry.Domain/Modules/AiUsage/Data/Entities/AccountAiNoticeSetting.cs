namespace CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

/// <summary>
/// One account's choices about the notices this module raises. Platform-scoped and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The creator's row, not an administrator's.</strong> It is deliberately not a column on
/// <see cref="AccountAiQuota"/>: a quota holds effective-dated terms that ops writes and a period freezes, and
/// a preference the creator changes whenever they like has no business in that history.
/// </para>
/// <para>
/// <strong>No row means on.</strong> A notice is not a charge, so it needs no opt-in (B-59), and an account
/// that has never had an opinion gets the default without a row being written for it. The row exists only once
/// somebody has chosen.
/// </para>
/// <para>
/// Account-scoped like everything else in this module (USAGE-003): no <c>WorkspaceId</c>, and no foreign key to
/// <c>ApplicationUser</c>, for the reason the usage ledger has none — account erasure is a documented path
/// (9A.3) rather than a cascade default.
/// </para>
/// </remarks>
public class AccountAiNoticeSetting
{
    public Guid Id { get; set; }

    /// <summary>The Identity account these choices belong to. Never a workspace id.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the account is told when its spendable credits run low (PRICE-011 as amended, B-59).
    /// </summary>
    /// <remarks>
    /// Switching it off hides the notice; it does not stop the period being marked. The mark is a fact about
    /// the account's balance, and turning the notice back on mid-period should not need the creator to spend
    /// again before they are told.
    /// </remarks>
    public bool LowCreditNoticeEnabled { get; set; } = true;

    public DateTimeOffset LastChangedAt { get; set; }
}
