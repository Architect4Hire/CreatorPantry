using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// One allowance period's totals, in the unit the period was denominated in.
/// </summary>
/// <param name="Unit">What the four amounts below are counted in.</param>
/// <param name="Allowance">
/// The period's spendable total: its own allowance, anything an upgrade added during it (AP.4.2), and anything
/// carried into it.
/// </param>
/// <param name="CarriedOver">
/// How much of <paramref name="Allowance"/> rolled in from the period before, so a roll is visible rather than
/// implied. The period's own allowance, upgrades included, is the difference.
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
/// Where the account stands with the low-credit notice (PRICE-011 as amended, B-59).
/// </summary>
/// <param name="IsNoticed">
/// Whether an admission in this period has left the account at or below
/// <c>Ai:Quota:LowCreditNoticeFraction</c> of its allowance. Recorded on the period, so it is said once
/// however many admissions cross the line, and it is false again when the period rolls.
/// </param>
/// <param name="IsDismissed">Whether the creator has put this period's notice away.</param>
/// <param name="NoticeEnabled">Whether the account wants the notice at all. True until it says otherwise.</param>
/// <param name="Spendable">
/// What the account can actually spend right now: <see cref="AccountAiUsagePeriodServiceModel.Remaining"/>
/// plus <see cref="AccountAiUsageServiceModel.PackRemaining"/>. The figure the notice quotes, summed here so
/// that the notice and the thing that raised it cannot disagree about it. Nought for a period counted in
/// tokens, which never spends packs.
/// </param>
/// <remarks>
/// Facts, not a decision about what to draw. Whether a surface shows the notice is the surface's call — an
/// exhausted account, for one, is told something stronger — and the three flags are what it needs to make it.
/// </remarks>
public sealed record AccountAiLowCreditServiceModel(
    bool IsNoticed, bool IsDismissed, bool NoticeEnabled, decimal Spendable);

/// <summary>
/// The signed-in account's AI allowance right now, and what it has gone on (USAGE-008).
/// </summary>
/// <param name="Period">The running period's totals.</param>
/// <param name="IsSuspended">
/// Whether AI access is switched off for this account regardless of what <see cref="Period"/> says is left.
/// </param>
/// <param name="ByTask">Spend per capability, largest first.</param>
/// <param name="ByWorkspace">Spend per workspace, largest first.</param>
/// <param name="IsNearlySpent">
/// Whether what is left is at or below <c>Ai:Quota:NearlySpentFraction</c> of the spendable allowance, and above
/// nothing (USAGE-010). The server's call, so every surface that warns agrees (AP.2.6).
/// </param>
/// <param name="Pictures">What pictures charged this period: the <c>ImageGeneration</c> share of <see cref="ByTask"/>.</param>
/// <param name="Writing">What every other capability charged this period. With <see cref="Pictures"/>, sums to <c>Consumed</c>.</param>
/// <param name="PackRemaining">
/// The account's unspent pack credits — its credit packs, which never reset (B-50). Spent after the period's
/// allowance (AP.5.2), and reported beside <see cref="AccountAiUsagePeriodServiceModel.Remaining"/>, never folded
/// into it. The same name and the same number as a quote's and a refusal's <c>packRemaining</c>, so a usage page
/// and a refusal open at the same moment agree. Nought for a period counted in tokens, which never spends packs.
/// </param>
/// <param name="Plan">The plan the account is on (PRICE-007); null when this host configures no plan catalogue.</param>
/// <param name="LowCredit">Where the account stands with the low-credit notice (B-59).</param>
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
    IReadOnlyList<AccountAiUsageWorkspaceTotalServiceModel> ByWorkspace,
    bool IsNearlySpent,
    decimal Pictures,
    decimal Writing,
    decimal PackRemaining,
    AccountAiUsagePlanServiceModel? Plan,
    AccountAiLowCreditServiceModel LowCredit)
{
    /// <summary>
    /// The plan's pictures for the UTC day and what is left of them (DLIM-008, DL.9). Null when no daily cap is
    /// enforced — no plan catalogue, or limits switched off — and when nothing can be made at all: a suspension, or
    /// a trial that has ended.
    /// </summary>
    public AccountAiUsagePicturesTodayServiceModel? PicturesToday { get; init; }

    /// <summary>
    /// A trial's pictures in all and what is left of them (B-52, DL.9). Null on a paid plan, which has no such cap,
    /// and under the same conditions as <see cref="PicturesToday"/> otherwise.
    /// </summary>
    public AccountAiUsageTrialPicturesServiceModel? TrialPictures { get; init; }
}

/// <summary>The plan an account is on, as its own usage page names it (PRICE-007, AP.5.3).</summary>
/// <param name="Key">The catalogue key of the plan in force.</param>
/// <param name="Name">Its display name.</param>
/// <param name="AllowanceRenews">
/// Whether this period's allowance comes back when it resets. False only when the plan decides the allowance and
/// grants it once — a trial's credits (B-48) — so a page never promises credits back that will not come.
/// </param>
/// <remarks>
/// <strong>No credit figure.</strong> An ops-set allowance wins over the plan (USAGE-009), so the plan's credits
/// can disagree with what the account actually has; the period's figures are the only allowance numbers here.
/// </remarks>
public sealed record AccountAiUsagePlanServiceModel(string Key, string Name, bool AllowanceRenews);

/// <summary>The plan's pictures for the UTC day, counted as admission counts them (DLIM-008, DL.9).</summary>
/// <param name="Cap">The plan's pictures a UTC day — the figure admission is handed, and the plan card's.</param>
/// <param name="Used">
/// Pictures that count against today, across every workspace: requests still held count every picture asked for,
/// settled ones what was charged, released ones nothing.
/// </param>
/// <param name="Left">How many more today allows. Never below nought, even when a downgrade put the day over.</param>
/// <param name="ResetsAt">The next UTC midnight, when <paramref name="Used"/> starts again from nought.</param>
/// <remarks>
/// <strong>Every figure is the server's</strong>, so a page that shows "22 left" never subtracts. It is a snapshot:
/// admission counts again, under the period's row version, and decides.
/// </remarks>
public sealed record AccountAiUsagePicturesTodayServiceModel(int Cap, int Used, int Left, DateTimeOffset ResetsAt);

/// <summary>A trial's pictures in all, counted as admission counts them (B-52, DL.9).</summary>
/// <param name="Cap">The pictures the whole trial includes.</param>
/// <param name="Used">Pictures that count against it, ever, across every workspace.</param>
/// <param name="Left">How many more the trial allows. Never below nought. It never turns over, so there is no reset.</param>
public sealed record AccountAiUsageTrialPicturesServiceModel(int Cap, int Used, int Left);
