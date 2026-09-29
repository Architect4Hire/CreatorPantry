using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>The terms an account is spending under, from wherever they came.</summary>
internal sealed record AiQuotaTerms(
    AiQuotaUnit Unit,
    decimal Allowance,
    AiQuotaPeriodLength PeriodLength,
    int PeriodAnchor,
    string TimeZoneId,
    AiQuotaCarryOver CarryOver,
    decimal? CarryOverCap,
    bool IsSuspended);

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
/// </remarks>
internal interface IAiQuotaPeriodResolver
{
    /// <summary>
    /// The terms in force for this account: its own row, or the platform default.
    /// </summary>
    /// <remarks>
    /// USAGE-003 forbids inventing a default allowance in domain code, and this is where that holds: there is
    /// no fallback constant here to read, only configuration. <c>AccountAiQuota.Allowance</c> being nullable is
    /// what makes "the number is not here" expressible at all.
    /// </remarks>
    Task<AiQuotaTerms> ResolveTermsAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>The period covering <paramref name="now"/>, opened and persisted if it did not exist.</summary>
    Task<AccountAiQuotaPeriod> OpenAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The period covering <paramref name="now"/>, or the one that <em>would</em> be opened for it — built
    /// from the same terms and the same carry-over rule, and then thrown away.
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
    ITimeZoneConverter zones,
    IOptions<AiQuotaOptions> options) : IAiQuotaPeriodResolver
{
    private readonly AiQuotaOptions _options = options.Value;

    public async Task<AiQuotaTerms> ResolveTermsAsync(string accountId, CancellationToken cancellationToken)
    {
        var quota = await dataLayer.FindCurrentQuotaAsync(accountId, cancellationToken);

        if (quota is null)
        {
            return new AiQuotaTerms(
                _options.DefaultUnit,
                _options.DefaultAllowance,
                _options.DefaultPeriodLength,
                _options.DefaultPeriodAnchor,
                ZoneOr(_options.DefaultTimeZoneId),
                _options.DefaultCarryOver,
                _options.DefaultCarryOverCap,
                IsSuspended: false);
        }

        return new AiQuotaTerms(
            quota.Unit,
            quota.Allowance ?? _options.DefaultAllowance,
            quota.PeriodLength,
            quota.PeriodAnchor,
            ZoneOr(quota.TimeZoneId),
            quota.CarryOver,
            quota.CarryOverCap,
            quota.IsSuspended);
    }

    public Task<AccountAiQuotaPeriod> OpenAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken) =>
        dataLayer.ResolvePeriodAsync(
            accountId, now, (previous, outstanding) => Build(accountId, terms, now, previous, outstanding),
            cancellationToken);

    public Task<AccountAiQuotaPeriod> ProjectAsync(
        string accountId, AiQuotaTerms terms, DateTimeOffset now, CancellationToken cancellationToken) =>
        dataLayer.ProjectPeriodAsync(
            accountId, now, (previous, outstanding) => Build(accountId, terms, now, previous, outstanding),
            cancellationToken);

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
    /// </remarks>
    private AccountAiQuotaPeriod Build(
        string accountId,
        AiQuotaTerms terms,
        DateTimeOffset now,
        AccountAiQuotaPeriod? previous,
        decimal outstanding)
    {
        var localToday = zones.LocalDate(now, terms.TimeZoneId);
        var localStart = AiQuotaPeriodCalculator.StartOf(localToday, terms.PeriodLength, terms.PeriodAnchor);
        var localEnd = AiQuotaPeriodCalculator.EndOf(localStart, terms.PeriodLength);

        var startsAt = zones.ToUtc(localStart, TimeOnly.MinValue, terms.TimeZoneId).Utc;
        var endsAt = zones.ToUtc(localEnd, TimeOnly.MinValue, terms.TimeZoneId).Utc;

        var abuts = previous is not null && previous.EndsAt == startsAt;

        return new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StartsAt = abuts ? previous!.EndsAt : startsAt,
            EndsAt = endsAt,
            TimeZoneId = terms.TimeZoneId,
            LocalStartDate = localStart,
            Unit = terms.Unit,
            Allowance = terms.Allowance,
            CarriedOver = abuts ? CarriedOver(terms, previous!, outstanding) : 0m,
            Consumed = 0m,
            Reserved = 0m,
        };
    }

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
            previous.Allowance, previous.CarriedOver, previous.Consumed, outstanding);

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
