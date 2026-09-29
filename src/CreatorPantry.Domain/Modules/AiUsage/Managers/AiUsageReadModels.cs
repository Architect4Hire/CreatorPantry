using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// One allowance period's totals, in the unit the period was denominated in.
/// </summary>
/// <param name="Unit">What the four amounts below are counted in.</param>
/// <param name="Allowance">
/// The period's spendable total: its own allowance plus anything carried into it.
/// </param>
/// <param name="CarriedOver">
/// How much of <paramref name="Allowance"/> rolled in from the period before, so a roll is visible rather than
/// implied. The period's own allowance is the difference.
/// </param>
/// <param name="Consumed">What settled runs actually charged.</param>
/// <param name="Reserved">What runs in flight are holding right now. Zero on a period that has closed.</param>
/// <param name="Remaining">
/// <paramref name="Allowance"/> less <paramref name="Consumed"/> and <paramref name="Reserved"/> — the figure
/// an admission would judge the next request against.
/// </param>
/// <param name="PeriodLength">How long the period runs, so a caller can say "this month" rather than infer it.</param>
/// <param name="TimeZoneId">
/// The IANA zone the two boundaries below were computed in.
/// </param>
/// <param name="StartsAt">When the period opened. Inclusive.</param>
/// <param name="ResetsAt">When it ends and the allowance comes back. Exclusive.</param>
/// <remarks>
/// <para>
/// <strong><paramref name="Allowance"/> is the spendable total, matching what a quota refusal reports under
/// the same name.</strong> Two payloads about the same period at the same instant using one word for two
/// different numbers is how a usage panel and a refusal toast end up disagreeing on screen.
/// </para>
/// <para>
/// <strong><paramref name="TimeZoneId"/> travels with the boundaries because they are local midnights.</strong>
/// api-contract.md asks for an explicit local-zone field wherever a schedule depends on one, and this is such
/// a schedule: rendering <paramref name="ResetsAt"/> in the browser's zone shows the wrong calendar day for
/// any creator whose quota zone is not their own.
/// </para>
/// </remarks>
public sealed record AccountAiUsagePeriodServiceModel(
    AiQuotaUnit Unit,
    decimal Allowance,
    decimal CarriedOver,
    decimal Consumed,
    decimal Reserved,
    decimal Remaining,
    AiQuotaPeriodLength PeriodLength,
    string TimeZoneId,
    DateTimeOffset StartsAt,
    DateTimeOffset ResetsAt);

/// <summary>What one capability charged the account this period.</summary>
/// <param name="TaskType">Which capability.</param>
/// <param name="Amount">Credits charged by its settled runs.</param>
/// <param name="Requests">How many provider attempts it made, billable or not.</param>
public sealed record AccountAiUsageTaskTotalServiceModel(
    AiTaskType TaskType, decimal Amount, int Requests);

/// <summary>
/// What one workspace's work charged the account this period.
/// </summary>
/// <param name="WorkspaceId">
/// Where the work was done. Null only for spend this module cannot place in a workspace at all, which nothing
/// produces in practice and which is carried rather than dropped so the parts still sum to the whole.
/// </param>
/// <param name="WorkspaceName">
/// The workspace's name, <strong>or null when the account no longer holds active membership there</strong>.
/// </param>
/// <param name="Amount">Credits charged by settled runs done there.</param>
/// <param name="Requests">How many provider attempts were made there.</param>
/// <remarks>
/// <strong>A revoked membership withholds the name and keeps the total</strong> (USAGE-008). The spend happened
/// and it is the creator's own history, so removing the row would misreport what they used; the name is a fact
/// about the workspace rather than about their usage, and they are no longer entitled to it. The id stays
/// because they demonstrably worked there and every workspace route is membership-gated, so it opens nothing.
/// </remarks>
public sealed record AccountAiUsageWorkspaceTotalServiceModel(
    Guid? WorkspaceId, string? WorkspaceName, decimal Amount, int Requests);

/// <summary>
/// The signed-in account's AI allowance right now, and what it has gone on (USAGE-008).
/// </summary>
/// <param name="Period">The running period's totals.</param>
/// <param name="IsSuspended">
/// Whether AI access is switched off for this account regardless of what <see cref="Period"/> says is left.
/// </param>
/// <param name="ByTask">Spend per capability, largest first.</param>
/// <param name="ByWorkspace">Spend per workspace, largest first.</param>
/// <remarks>
/// <para>
/// <strong>Both breakdowns are in the period's unit and both sum to <c>Consumed</c>.</strong> They are built
/// from settled reservations rather than from the usage ledger, because the ledger counts tokens and the
/// allowance counts credits — a breakdown in a different unit from the total it explains is a breakdown
/// nobody can check.
/// </para>
/// <para>
/// <strong>The consequence, stated rather than discovered:</strong> attempts made before reservations existed
/// were never charged against an allowance, so a period from before that shows real
/// <see cref="AccountAiUsageTaskTotalServiceModel.Requests"/> against a zero amount. That is accurate — those
/// runs genuinely cost the account nothing — and it is why the request counts travel beside the amounts.
/// </para>
/// <para>
/// Counts and amounts only. No recipe title, no operation id, no proposal detail: the tables behind this hold
/// none, which is what makes an account-scoped read of them safe rather than careful.
/// </para>
/// </remarks>
public sealed record AccountAiUsageServiceModel(
    AccountAiUsagePeriodServiceModel Period,
    bool IsSuspended,
    IReadOnlyList<AccountAiUsageTaskTotalServiceModel> ByTask,
    IReadOnlyList<AccountAiUsageWorkspaceTotalServiceModel> ByWorkspace);
