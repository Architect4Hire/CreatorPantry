using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>Assembles an account's own view of what it has spent (USAGE-008).</summary>
internal interface IAiUsageReadBusiness
{
    Task<AccountAiUsageServiceModel> GetCurrentAsync(string accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> GetHistoryAsync(
        string accountId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReadBusiness"/>
internal sealed class AiUsageReadBusiness(
    IAiUsageReadDataLayer usage,
    IAiQuotaPeriodResolver periods,
    IWorkspaceFacade workspaces,
    IAccountPlanFacade plans,
    IAccountCreditSpendFacade credits,
    IClock clock,
    IOptions<AiQuotaOptions> options,
    IOptions<LimitsOptions> limits) : IAiUsageReadBusiness
{
    public async Task<AccountAiUsageServiceModel> GetCurrentAsync(
        string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var now = clock.UtcNow;
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        // Projected, never opened. A read that wrote a row would be a surprising thing for a GET to do, and an
        // account that has never run anything is entitled to see its allowance without spending a row to.
        var period = await periods.ProjectAsync(accountId, terms, now, cancellationToken);

        var byTask = await usage.SumByTaskAsync(
            accountId, period.Id, period.StartsAt, period.EndsAt, cancellationToken);

        var byWorkspace = await usage.SumByWorkspaceAsync(
            accountId, period.Id, period.StartsAt, period.EndsAt, cancellationToken);

        var described = Describe(period, terms.PeriodLength);

        var packRemaining = period.Unit is AiQuotaUnit.Credits
            ? await credits.BalanceAsync(accountId, cancellationToken)
            : 0m;

        // Pictures against everything else, from the same rows the per-task list shows, so the two figures
        // and the list can never disagree about what a picture cost (AP.2.6).
        var pictures = byTask.Where(row => row.TaskType is AiTaskType.ImageGeneration).Sum(row => row.Amount);
        var writing = byTask.Where(row => row.TaskType is not AiTaskType.ImageGeneration).Sum(row => row.Amount);

        // The plan in force now, which is the one admission judges a picture by — not the period's, which is the
        // plan at its own start (DL.8) and decides the allowance only.
        var plan = await plans.ResolveAsync(accountId, cancellationToken);
        var (today, trial) = await PictureCapsAsync(accountId, terms.IsSuspended, plan, now, cancellationToken);

        return new AccountAiUsageServiceModel(
            described,
            terms.IsSuspended,
            [.. byTask
                .Select(row => new AccountAiUsageTaskTotalServiceModel(row.TaskType, row.Amount, row.Requests))
                .OrderByDescending(row => row.Amount)
                .ThenByDescending(row => row.Requests)
                .ThenBy(row => row.TaskType)],
            await NameAsync(accountId, byWorkspace, cancellationToken),
            IsNearlySpent: !terms.IsSuspended
                && described.Remaining > 0m
                && AiQuotaPeriodCalculator.IsNearlySpent(
                    described.Allowance, described.Remaining, options.Value.NearlySpentFraction),
            Pictures: pictures,
            Writing: writing,

            // The balance admission spends after the allowance, read where admission reads it, so the page and the
            // next refusal cannot disagree. A period in tokens never spends packs, so it reports none (AP.5.2).
            PackRemaining: packRemaining,
            Plan: DescribePlan(terms.Plan ?? plan, terms),
            LowCredit: new AccountAiLowCreditServiceModel(
                IsNoticed: period.LowCreditNoticedAt is not null,
                IsDismissed: period.LowCreditNoticeDismissedAt is not null,
                NoticeEnabled: await usage.IsLowCreditNoticeEnabledAsync(accountId, cancellationToken),

                // Summed here and nowhere else, so the notice's figure is the one admission judged it by: the
                // allowance left plus every pack's balance (B-59).
                Spendable: described.Remaining + packRemaining))
        {
            PicturesToday = today,
            TrialPictures = trial,
        };
    }

    /// <summary>
    /// The account's picture caps and what is left of each (DLIM-008, DL.9), read as
    /// <c>GeneratedImageGenerationBusiness</c> hands them to admission and counted by admission's own query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan's day only while limits are on, because it is a limit and switches off with them (B-56); a trial's
    /// pictures in all whatever limits say, because they are the trial's own term (AP.7.2). Both are counted by the
    /// repository composition admission and the quote count with; the day's cap, count and reset are held to the
    /// quote's through the gateway by
    /// <c>ImagesJourneyEndToEndTests.Todays_pictures_are_the_accounts_across_its_workspaces_and_agree_with_the_quote</c>.
    /// </para>
    /// <para>
    /// Neither for an account that can make nothing — suspended, or a trial that has ended. Admission refuses those
    /// before it counts, and "5 left today" beside a refusal would promise pictures that will not come.
    /// </para>
    /// </remarks>
    private async Task<(AccountAiUsagePicturesTodayServiceModel?, AccountAiUsageTrialPicturesServiceModel?)>
        PictureCapsAsync(
            string accountId,
            bool isSuspended,
            AccountPlanServiceModel? plan,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        if (plan is null || isSuspended || plan.TrialHasEnded(now))
        {
            return (null, null);
        }

        int? dayCap = limits.Value.Enabled ? plan.DailyImageCap : null;
        var trialCap = plan.TrialImageCap;

        if (dayCap is null && trialCap is null)
        {
            return (null, null);
        }

        var counts = await usage.CountImagesAsync(
            accountId,
            new AccountImageWindows(LimitDay.StartOf(now), InAll: trialCap is not null),
            cancellationToken);

        return (
            dayCap is { } day
                ? new AccountAiUsagePicturesTodayServiceModel(
                    day, counts.Today, Math.Max(0, day - counts.Today), LimitDay.NextAfter(now))
                : null,
            trialCap is { } all
                ? new AccountAiUsageTrialPicturesServiceModel(all, counts.InAll, Math.Max(0, all - counts.InAll))
                : null);
    }

    /// <summary>The plan the account is on, and whether the allowance it is spending comes back at the reset.</summary>
    /// <remarks>
    /// The terms carry the plan only when it decides the allowance. When an ops-set allowance or other terms do,
    /// the account is still on exactly one plan (PRICE-007) and is told its name — but that allowance renews as
    /// those terms say, so only a plan that grants once and is deciding makes it not come back.
    /// </remarks>
    private static AccountAiUsagePlanServiceModel? DescribePlan(AccountPlanServiceModel? plan, AiQuotaTerms terms)
    {
        return plan is null
            ? null
            : new AccountAiUsagePlanServiceModel(
                plan.Key, plan.Name, AllowanceRenews: terms.Plan?.Period is not PlanAllowancePeriod.Once);
    }

    /// <remarks>
    /// Closed periods only: the running one is what <see cref="GetCurrentAsync"/> is for, and repeating it here
    /// would show a creator the same period twice with two different totals a moment apart.
    /// </remarks>
    public async Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> GetHistoryAsync(
        string accountId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        // The running period is excluded by the query rather than here, so the limit buys the caller that many
        // closed periods rather than that many rows one of which they cannot see.
        var rows = await usage.FindClosedPeriodsAsync(
            accountId,
            clock.UtcNow,
            Math.Clamp(limit, 1, AiUsagePolicy.HistoryMaxPeriods),
            cancellationToken);

        // The length comes from the terms in force rather than from each row, because a period stores the
        // boundaries it was opened with and not the rule that produced them. An account whose length changed
        // therefore reads its old periods under today's label -- which is wrong in a way that only a stored
        // length could fix, and is noted here rather than papered over.
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        return [.. rows.Select(period => Describe(period, terms.PeriodLength))];
    }

    /// <summary>
    /// Attaches a name to each workspace the account still belongs to, and withholds it from the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The total stays and the name goes</strong> (USAGE-008). The spend is the creator's own history
    /// and removing it would misreport what they used; the name is a fact about the workspace rather than
    /// about their usage, and somebody who has left is no longer entitled to it.
    /// </para>
    /// <para>
    /// <strong>Active membership, not merely a membership row.</strong> A row whose status is <c>Removed</c>
    /// is exactly the revoked case this rule is about, and <c>Invited</c> is somebody who has not accepted —
    /// neither is current access, so neither discloses a name.
    /// </para>
    /// <para>
    /// The membership set comes from the tenancy module's own facade, which is the same call
    /// <c>GET /api/v1/me</c> makes. This module therefore adds no cross-workspace query of its own: it asks who
    /// the caller belongs to and withholds everything else.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<AccountAiUsageWorkspaceTotalServiceModel>> NameAsync(
        string accountId,
        IReadOnlyList<AiUsageWorkspaceTotal> totals,
        CancellationToken cancellationToken)
    {
        if (totals.Count == 0)
        {
            return [];
        }

        var visible = (await workspaces.GetMyMembershipsAsync(accountId, cancellationToken))
            .Where(membership => membership.Status is WorkspaceMembershipStatus.Active)
            .ToDictionary(membership => membership.WorkspaceId, membership => membership.WorkspaceName);

        return
        [
            .. totals
                .Select(row => new AccountAiUsageWorkspaceTotalServiceModel(
                    row.WorkspaceId,
                    row.WorkspaceId is { } id && visible.TryGetValue(id, out var name) ? name : null,
                    row.Amount,
                    row.Requests))
                .OrderByDescending(row => row.Amount)
                .ThenByDescending(row => row.Requests)
                .ThenBy(row => row.WorkspaceId),
        ];
    }

    private static AccountAiUsagePeriodServiceModel Describe(
        AccountAiQuotaPeriod period, AiQuotaPeriodLength length) =>
        new(period.Unit,

            // The spendable total, which is what a quota refusal publishes under this name too. One word, one
            // number, across every payload about the same period.
            period.Allowance + period.UpgradeGranted + period.CarriedOver,
            period.CarriedOver,
            period.Consumed,
            period.Reserved,

            // From the shared calculator, so the balance a creator is shown is the one an admission would then
            // judge their next request against.
            AiQuotaPeriodCalculator.Remaining(
                period.Allowance + period.UpgradeGranted, period.CarriedOver, period.Consumed, period.Reserved),
            length,
            period.TimeZoneId,
            period.StartsAt,
            period.EndsAt);
}
