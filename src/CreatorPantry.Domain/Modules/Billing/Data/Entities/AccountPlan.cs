using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Data.Entities;

/// <summary>
/// From one instant on, the plan one Identity account is on (PRICE-007, AP.4.1). Platform-scoped and never
/// filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A plan belongs to an account, never to a workspace</strong> (B-47). The same person working in two
/// workspaces is on one plan, which is why nothing here is <see cref="IWorkspaceOwned"/> — the reasoning
/// <c>AccountAiQuota</c> gives, and the same shape: platform-scoped, keyed by account, no <c>WorkspaceId</c>.
/// </para>
/// <para>
/// <strong>An append-only chain, so that two plans at once cannot be stored.</strong> A row says only "from
/// <see cref="EffectiveFrom"/>, this account is on <see cref="PlanKey"/>", and names the start of the row it
/// supersedes in <see cref="SupersedesFrom"/>. The schema then holds every account's rows to one chain whose
/// starts strictly increase: a foreign key makes <see cref="SupersedesFrom"/> name a row that exists, a unique
/// index lets only one row supersede it, another lets only one row per account supersede nothing, and a check
/// puts each row after the one it supersedes. A row is in force until the next one starts, so "effective to" is
/// the next row's start rather than a column that could disagree with it.
/// </para>
/// <para>
/// A stored end date was the alternative and was turned down (AP.4.1, owner, 2026-10-27): SQL Server has neither
/// exclusion nor deferred constraints, so a table of from/to rows can be held to one open row but not kept free of
/// two closed rows that overlap. Here an overlap is unrepresentable rather than merely unwritten.
/// </para>
/// <para>
/// <strong>Immutable once written, so it carries no row version.</strong> Concurrency is decided by the indexes
/// instead: two writers who both read the same latest row both try to supersede it, and the unique index on
/// <see cref="SupersedesFrom"/> lets exactly one of them. The only other write a row takes is its deletion while
/// it is still in the future and the end of the chain — a scheduled change cancelled (AP.6.2) — which no other
/// row depends on.
/// </para>
/// <para>
/// <strong>No price, no figure and no provider reference.</strong> What a plan grants is read from the catalogue
/// (<c>Billing:Plans</c>) by key when the plan is resolved, so the owner changing a figure changes it for everyone
/// on that plan. A Stripe subscription or customer id belongs in Phase 6's integration records.
/// </para>
/// <para>
/// No foreign key to <c>ApplicationUser</c>, on the same reasoning as the quota and the usage ledger: account
/// erasure is a documented path (9A.3), not a cascade default.
/// </para>
/// </remarks>
public class AccountPlan
{
    public Guid Id { get; set; }

    /// <summary>The Identity account this plan applies to. Never a workspace id and never a membership id.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// The catalogue key of the plan, or null for "the configured default plan from here on".
    /// </summary>
    /// <remarks>
    /// Null rather than the default's key copied in, for the reason <c>AccountAiQuota.Allowance</c> is nullable:
    /// a row that lapses back to the default — a cancellation, a trial's end — should follow
    /// <c>Billing:DefaultPlan</c> if the owner changes it, not freeze whatever it named on the day.
    /// </remarks>
    public string? PlanKey { get; set; }

    public AccountPlanSource Source { get; set; }

    /// <summary>When this plan takes effect. Inclusive, and possibly in the future (B-54).</summary>
    public DateTimeOffset EffectiveFrom { get; set; }

    /// <summary>
    /// The <see cref="EffectiveFrom"/> of the row this one supersedes, or null for the account's first row.
    /// </summary>
    /// <remarks>
    /// The link that makes the rows a chain. An instant rather than the other row's <see cref="Id"/> because the
    /// foreign key runs to <c>(AccountId, EffectiveFrom)</c>: that keeps it inside one account, and lets the check
    /// constraint compare it with this row's own start without a join.
    /// </remarks>
    public DateTimeOffset? SupersedesFrom { get; set; }
}
