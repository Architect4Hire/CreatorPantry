using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>The terms an account is spending under, from wherever they came.</summary>
/// <param name="Allowance">
/// What a period opened now would be granted before any plan rule applies: the ops-set allowance, the plan's
/// credits, or the platform default, in that order.
/// </param>
/// <param name="Plan">
/// The plan in force now, when it is what decides the allowance (AP.4.2); null when an ops-set quota names an
/// allowance, when no plans are configured, or when the terms in force are not a monthly allowance in credits.
/// </param>
internal sealed record AiQuotaTerms(
    AiQuotaUnit Unit,
    decimal Allowance,
    AiQuotaPeriodLength PeriodLength,
    int PeriodAnchor,
    string TimeZoneId,
    AiQuotaCarryOver CarryOver,
    decimal? CarryOverCap,
    bool IsSuspended,
    AccountPlanServiceModel? Plan = null);

/// <summary>
/// Where an account's allowance period begins and ends, what it opened with, and what it has left.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Extracted so there is one copy of the roll.</strong> Two seams need it — admission, which opens the
/// period it is about to spend from, and the read seam, which must show the same period without writing one.
/// A second implementation would drift, and the day it did, a creator would be shown a balance that the
/// admission they then attempt does not agree with.
/// </para>
/// <para>
/// The distinction the two callers actually need is one word: <see cref="OpenAsync"/> persists,
/// <see cref="ProjectAsync"/> does not. Everything else about them is identical, which is the point.
/// </para>
/// <para>
/// <strong>Where the allowance comes from (AP.4.2).</strong> An ops-set <c>AccountAiQuota.Allowance</c> wins
/// outright (USAGE-009). Otherwise, when plans are configured, the account's plan decides: a period opens with the
/// credits of the plan in force at its own start, a trial's credits are granted once in the account's life
/// (B-48), and an upgrade during the period adds the difference at once. A downgrade waits for the next roll
/// (B-54). With no plans configured, the platform default, as before.
/// </para>
/// </remarks>
internal interface IAiQuotaPeriodResolver
{
    /// <summary>
    /// The terms in force for this account: its own row, its plan, or the platform default.
    /// </summary>
    /// <remarks>
    /// USAGE-003 forbids inventing a default allowance in domain code, and this is where that holds: there is
    /// no fallback constant here to read, only configuration. <c>AccountAiQuota.Allowance</c> being nullable is
    /// what makes "the number is not here" expressible at all — and under plans, a row with no allowance (one
    /// that only suspends, say) leaves the number to the plan.
    /// </remarks>
    Task<AiQuotaTerms> ResolveTermsAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// What the account is granted a period when no ops-set allowance says otherwise, under terms in
    /// <paramref name="unit"/> and <paramref name="length"/>: its plan's credits, or the platform default.
    /// </summary>
    /// <remarks>
    /// For describing terms an ops write is about to put in force, so the audit row records the allowance the
    /// account will actually have rather than the platform default it would have had before plans.
    /// </remarks>
    Task<decimal> FallbackAllowanceAsync(
        string accountId, AiQuotaUnit unit, AiQuotaPeriodLength length, CancellationToken cancellationToken);

    /// <summary>
    /// The period covering <paramref name="now"/>, opened and persisted if it did not exist, and topped up if the
    /// account has upgraded since it opened.
    /// </summary>
    Task<AccountAiQuotaPeriod> OpenAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The period covering <paramref name="now"/>, or the one that <em>would</em> be opened for it — built
    /// from the same terms, the same carry-over rule and the same top-up, and then thrown away.
    /// </summary>
    /// <remarks>
    /// For every caller that must answer "what does this account have?" without spending anything to find out:
    /// the request-time refusal, and the creator's own read of their usage. A GET that opened a row would be a
    /// surprising thing for a GET to do.
    /// </remarks>
    Task<AccountAiQuotaPeriod> ProjectAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiQuotaPeriodResolver"/>
internal sealed class AiQuotaPeriodResolver(
    IAiQuotaDataLayer dataLayer,
    IAccountPlanFacade plans,
    ITimeZoneConverter zones,
    IOptions<AiQuotaOptions> options) : IAiQuotaPeriodResolver
{
    private readonly AiQuotaOptions _options = options.Value;

    public async Task<AiQuotaTerms> ResolveTermsAsync(string accountId, CancellationToken cancellationToken)
    {
        var quota = await dataLayer.FindCurrentQuotaAsync(accountId, cancellationToken);

        var unit = quota?.Unit ?? _options.DefaultUnit;
        var length = quota?.PeriodLength ?? _options.DefaultPeriodLength;

        // The plan decides only what nobody else did: an ops-set allowance wins outright (USAGE-009).
        var plan = quota?.Allowance is null ? await PlanAsync(accountId, unit, length, cancellationToken) : null;

        var allowance = quota?.Allowance ?? plan?.Credits ?? _options.DefaultAllowance;

        if (quota is null)
        {
            return new AiQuotaTerms(
                _options.DefaultUnit,
                allowance,
                _options.DefaultPeriodLength,
                _options.DefaultPeriodAnchor,
                ZoneOr(_options.DefaultTimeZoneId),
                _options.DefaultCarryOver,
                _options.DefaultCarryOverCap,
                IsSuspended: false,
                plan);
        }

        return new AiQuotaTerms(
            quota.Unit,
            allowance,
            quota.PeriodLength,
            quota.PeriodAnchor,
            ZoneOr(quota.TimeZoneId),
            quota.CarryOver,
            quota.CarryOverCap,
            quota.IsSuspended,
            plan);
    }

    public async Task<decimal> FallbackAllowanceAsync(
        string accountId, AiQuotaUnit unit, AiQuotaPeriodLength length, CancellationToken cancellationToken) =>
        (await PlanAsync(accountId, unit, length, cancellationToken))?.Credits ?? _options.DefaultAllowance;

    /// <summary>
    /// The account's plan, when it is what decides an allowance under these terms; null otherwise.
    /// </summary>
    /// <remarks>
    /// A plan's credits are a monthly allowance in credits, so terms in another unit or length keep the platform
    /// default — startup refuses default terms that are not those whenever plans exist, so only an ops-set quota
    /// row can get here with other terms.
    /// </remarks>
    private Task<AccountPlanServiceModel?> PlanAsync(
        string accountId, AiQuotaUnit unit, AiQuotaPeriodLength length, CancellationToken cancellationToken) =>
        unit is AiQuotaUnit.Credits && length is AiQuotaPeriodLength.Monthly
            ? plans.ResolveAsync(accountId, cancellationToken)
            : Task.FromResult<AccountPlanServiceModel?>(null);

    public async Task<AccountAiQuotaPeriod> OpenAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = await dataLayer.ResolvePeriodAsync(
            accountId,
            now,
            (previous, outstanding, token) => BuildAsync(accountId, terms, now, previous, outstanding, token),
            cancellationToken);

        if (await UpgradeTargetAsync(accountId, terms, period, cancellationToken) is not { } target)
        {
            return period;
        }

        // Persisted under the period's row version, and re-judged against a fresh read on every retry, so two
        // admissions racing to apply one upgrade grant the difference once.
        return await dataLayer.RaisePeriodAsync(
            period.Id, current => TopUp(current, target), cancellationToken) ?? period;
    }

    public async Task<AccountAiQuotaPeriod> ProjectAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = await dataLayer.ProjectPeriodAsync(
            accountId,
            now,
            (previous, outstanding, token) => BuildAsync(accountId, terms, now, previous, outstanding, token),
            cancellationToken);

        if (await UpgradeTargetAsync(accountId, terms, period, cancellationToken) is not { } target)
        {
            return period;
        }

        // On a copy: the period read back may be the tracked row, and a top-up staged on it would be written by
        // whatever saves next in this scope — a read that spent nothing would have changed the balance.
        var projected = Copy(period);
        TopUp(projected, target);

        return projected;
    }

    /// <summary>
    /// The period covering <paramref name="now"/>, built from the terms in force and the one before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only the current period is built, never the ones an idle account skipped.</strong> An account
    /// that has not run anything for a year would otherwise have twelve empty rows materialized to compute a
    /// carry-over the cap bounds anyway. The consequence is stated rather than hidden: carry-over comes from
    /// the preceding period only when it genuinely abuts this one, and is zero across a gap.
    /// </para>
    /// <para>
    /// The start instant is copied from that abutting period's stored <c>EndsAt</c> rather than re-derived,
    /// which is what <c>AccountAiQuotaPeriod</c> asks for: consecutive periods abut exactly, and a daylight
    /// saving transition can neither open a gap nor double-count an hour.
    /// </para>
    /// <para>
    /// <strong>The plan read is the one in force at the period's start, not now.</strong> A row is written by the
    /// first request after the roll, which for an idle account can be weeks later; reading the plan then would let
    /// a downgrade made in between take effect early, against B-54. An upgrade made in between is not lost: the
    /// top-up that follows every open grants it.
    /// </para>
    /// </remarks>
    private async Task<AccountAiQuotaPeriod> BuildAsync(
        string accountId,
        AiQuotaTerms terms,
        DateTimeOffset now,
        AccountAiQuotaPeriod? previous,
        decimal outstanding,
        CancellationToken cancellationToken)
    {
        var localToday = zones.LocalDate(now, terms.TimeZoneId);
        var localStart = AiQuotaPeriodCalculator.StartOf(localToday, terms.PeriodLength, terms.PeriodAnchor);
        var localEnd = AiQuotaPeriodCalculator.EndOf(localStart, terms.PeriodLength);

        var startsAt = zones.ToUtc(localStart, TimeOnly.MinValue, terms.TimeZoneId).Utc;
        var endsAt = zones.ToUtc(localEnd, TimeOnly.MinValue, terms.TimeZoneId).Utc;

        var abuts = previous is not null && previous.EndsAt == startsAt;
        var start = abuts ? previous!.EndsAt : startsAt;

        var allowance = terms.Allowance;
        string? planKey = null;

        if (terms.Plan is not null)
        {
            var atStart = await plans.ResolveAsync(accountId, start, cancellationToken) ?? terms.Plan;

            allowance = await GrantAsync(accountId, atStart, cancellationToken);
            planKey = atStart.Key;
        }

        return new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StartsAt = start,
            EndsAt = endsAt,
            TimeZoneId = terms.TimeZoneId,
            LocalStartDate = localStart,
            Unit = terms.Unit,
            Allowance = allowance,
            PlanKey = planKey,
            UpgradeGranted = 0m,
            CarriedOver = abuts ? CarriedOver(terms, previous!, outstanding) : 0m,
            Consumed = 0m,
            Reserved = 0m,
        };
    }

    /// <summary>
    /// What a period is owed under <paramref name="plan"/>: its credits, or nothing for a plan granted once that
    /// this account has already had a period under (B-48: a trial's credits are for the trial).
    /// </summary>
    private async Task<decimal> GrantAsync(
        string accountId, AccountPlanServiceModel plan, CancellationToken cancellationToken) =>
        plan.Period is PlanAllowancePeriod.Once
        && await dataLayer.HasPeriodUnderPlanAsync(accountId, plan.Key, cancellationToken)
            ? 0m
            : plan.Credits;

    /// <summary>
    /// What the period should have been granted in all, when the plan in force now grants more than it has; null
    /// when there is nothing to add.
    /// </summary>
    /// <remarks>
    /// Only a period a plan opened is topped up. One an ops-set quota or the platform default decided stays as it
    /// opened, because what was set there was a number rather than a plan.
    /// </remarks>
    private async Task<decimal?> UpgradeTargetAsync(
        string accountId, AiQuotaTerms terms, AccountAiQuotaPeriod period, CancellationToken cancellationToken)
    {
        if (terms.Plan is null || period.PlanKey is null || terms.Plan.Credits <= Granted(period))
        {
            return null;
        }

        var target = await GrantAsync(accountId, terms.Plan, cancellationToken);

        return target > Granted(period) ? target : null;
    }

    /// <summary>
    /// Raises the period's grant to <paramref name="target"/> by the difference, never by the whole plan
    /// (owner, 2026-10-27). False when it already has as much.
    /// </summary>
    private static bool TopUp(AccountAiQuotaPeriod period, decimal target)
    {
        if (target <= Granted(period))
        {
            return false;
        }

        period.UpgradeGranted = target - period.Allowance;

        return true;
    }

    private static decimal Granted(AccountAiQuotaPeriod period) => period.Allowance + period.UpgradeGranted;

    private static AccountAiQuotaPeriod Copy(AccountAiQuotaPeriod period) => new()
    {
        Id = period.Id,
        AccountId = period.AccountId,
        StartsAt = period.StartsAt,
        EndsAt = period.EndsAt,
        TimeZoneId = period.TimeZoneId,
        LocalStartDate = period.LocalStartDate,
        Unit = period.Unit,
        Allowance = period.Allowance,
        PlanKey = period.PlanKey,
        UpgradeGranted = period.UpgradeGranted,
        CarriedOver = period.CarriedOver,
        Consumed = period.Consumed,
        Reserved = period.Reserved,
        SettledAt = period.SettledAt,
        RowVersion = period.RowVersion,
    };

    /// <remarks>
    /// Capped, and the cap is required for this rule rather than optional: without one an account that spends
    /// nothing accumulates an unbounded balance and is effectively unmetered the first period it does spend.
    /// </remarks>
    private static decimal CarriedOver(
        AiQuotaTerms terms, AccountAiQuotaPeriod previous, decimal outstanding)
    {
        if (terms.CarryOver is not AiQuotaCarryOver.Unused)
        {
            return 0m;
        }

        var unused = AiQuotaPeriodCalculator.Unused(
            Granted(previous), previous.CarriedOver, previous.Consumed, outstanding);

        return Math.Min(unused, terms.CarryOverCap ?? 0m);
    }

    /// <summary>
    /// The zone, or UTC when the stored identifier is not one this platform knows.
    /// </summary>
    /// <remarks>
    /// A zone the tzdb has dropped must not make an account's allowance unspendable. Falling back is the lesser
    /// of the two wrongs: the boundaries move once, visibly, and the period records the zone they were actually
    /// computed in — which is why that column exists.
    /// </remarks>
    private string ZoneOr(string zoneId) =>
        !string.IsNullOrWhiteSpace(zoneId) && zones.IsValidZone(zoneId) ? zoneId : "Etc/UTC";
}
