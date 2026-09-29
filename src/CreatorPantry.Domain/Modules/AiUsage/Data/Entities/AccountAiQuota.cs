using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

/// <summary>
/// The AI allowance terms in force for one Identity account, effective-dated. Platform-scoped and never
/// filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A quota belongs to an account, never to a workspace and never to a membership</strong>
/// (USAGE-003). A creator does not earn a fresh allowance by joining another workspace, and the one balance
/// they have is spent from whichever workspace they happen to be working in. That is the same reason
/// <see cref="AccountAiUsageEntry"/> is keyed by account, and it is why nothing in this module is
/// <see cref="IWorkspaceOwned"/>.
/// </para>
/// <para>
/// <strong>This row holds terms, not balances.</strong> What has been spent lives on
/// <see cref="AccountAiQuotaPeriod"/>, which freezes the terms it opened under — unit, allowance, zone — so
/// that changing a quota takes effect at the next roll rather than retroactively re-pricing a period a
/// creator has already been spending against.
/// </para>
/// <para>
/// <strong>Effective-dated rather than edited.</strong> A change closes the current row with
/// <see cref="EffectiveTo"/> and inserts a new one, so "what were this account's terms in March?" is a query
/// rather than an archaeology exercise. At most one row per account may be open at a time, which is a
/// filtered unique index rather than a rule the write path is trusted to follow. Not an
/// <see cref="IImmutableRecord"/>: closing a row is an update, and that is the one update this entity takes.
/// </para>
/// <para>
/// No foreign key to <c>ApplicationUser</c>, on the same reasoning as the usage ledger: account erasure is a
/// documented path (9A.3), not a cascade default.
/// </para>
/// </remarks>
public class AccountAiQuota
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Identity account these terms apply to. Never a workspace id and never a membership id.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>What new periods opened under these terms will be denominated in.</summary>
    public AiQuotaUnit Unit { get; set; }

    /// <summary>
    /// The allowance per period, or null to resolve the configured platform default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nullable, and that nullability is the mechanism rather than a convenience.</strong> USAGE-003
    /// forbids inventing a default allowance in domain code; a column that can say "the number is not here"
    /// makes that structural, because there is nothing for domain code to read and fall back from. The
    /// resolved number is written onto <see cref="AccountAiQuotaPeriod.Allowance"/> when a period opens, which
    /// is where it stops being a default and becomes a fact.
    /// </para>
    /// <para>
    /// It also lets suspension exist without an allowance. Suspending an account that has no explicit quota
    /// would otherwise mean inventing one; instead it is a row reading "platform default, currently switched
    /// off".
    /// </para>
    /// </remarks>
    public decimal? Allowance { get; set; }

    public AiQuotaPeriodLength PeriodLength { get; set; }

    /// <summary>
    /// Which local day a period starts on. Its valid range depends on <see cref="PeriodLength"/> — unused and
    /// zero for daily, a <see cref="DayOfWeek"/> for weekly, a day of the month up to
    /// <see cref="AiUsagePolicy.MonthlyAnchorMax"/> for monthly — and the pairing is a check constraint rather
    /// than a convention.
    /// </summary>
    public int PeriodAnchor { get; set; }

    /// <summary>
    /// The IANA zone the period boundaries are computed in.
    /// </summary>
    /// <remarks>
    /// Stored on the account, not inferred from a server, a request, or a workspace — a creator working from
    /// two workspaces in different zones still has one allowance on one calendar. Validated through
    /// <c>ITimeZoneConverter.IsValidZone</c> before it is written. A change here does not move an open
    /// period's boundaries, because the period recorded the zone it was computed in.
    /// </remarks>
    public string TimeZoneId { get; set; } = string.Empty;

    public AiQuotaCarryOver CarryOver { get; set; }

    /// <summary>
    /// The most that may be carried into one period. Required when <see cref="CarryOver"/> is
    /// <see cref="AiQuotaCarryOver.Unused"/> and forbidden otherwise.
    /// </summary>
    public decimal? CarryOverCap { get; set; }

    /// <summary>
    /// Whether AI access is switched off for this account regardless of remaining allowance.
    /// </summary>
    /// <remarks>
    /// Distinct from an exhausted period, and distinguishable to the caller: USAGE-007 requires a refused
    /// request to say what is exhausted and when it resets, and "suspended" has no reset time to offer. The
    /// two get separate error codes in 9A.6.
    /// </remarks>
    public bool IsSuspended { get; set; }

    /// <summary>When these terms came into force.</summary>
    public DateTimeOffset EffectiveFrom { get; set; }

    /// <summary>When they stopped, or null while they are the terms in force.</summary>
    public DateTimeOffset? EffectiveTo { get; set; }

    /// <summary>
    /// The platform administrator who last wrote this row, or null when the platform itself did.
    /// </summary>
    /// <remarks>
    /// An Identity account id, read the same way <c>AuditLog.ActorUserId</c> is: null means system rather than
    /// unknown. It records authorship of the row and is <em>not</em> an audit trail — USAGE-009 requires an
    /// audit event naming actor, before and after values, and a reason, which this single column cannot carry.
    /// </remarks>
    public string? LastChangedByUserId { get; set; }

    public DateTimeOffset LastChangedAt { get; set; }
}
