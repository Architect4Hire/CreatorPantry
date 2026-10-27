using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>Which plan an account is on, and putting it on another (PRICE-007, AP.4.1).</summary>
internal interface IAccountPlanBusiness
{
    /// <summary>The account's plan now, with the catalogue's terms; null when no plans are configured.</summary>
    Task<AccountPlanServiceModel?> ResolveAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the account on <paramref name="planKey"/> from <paramref name="effectiveFrom"/> on, or on the default
    /// plan when the key is null.
    /// </summary>
    /// <param name="effectiveFrom">
    /// When the change takes effect; null for now. A future instant schedules it, as a downgrade at period end
    /// does (B-54).
    /// </param>
    /// <remarks>
    /// Not on the facade yet. AP.4.4 exposes it to ops with an actor, a reason and a platform audit event, and
    /// AP.6.2 to Stripe; a write with no audit of its own has no business being reachable before then.
    /// </remarks>
    Task<AccountPlanChangeOutcome> ChangeAsync(
        string accountId,
        string? planKey,
        AccountPlanSource source,
        DateTimeOffset? effectiveFrom,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountPlanBusiness"/>
/// <remarks>
/// <see cref="IOptions{TOptions}"/> rather than a monitor, for the reason <c>PlanCatalogueBusiness</c> gives: the
/// catalogue read here is the one startup validated.
/// </remarks>
internal sealed class AccountPlanBusiness(
    IAccountPlanDataLayer data,
    IOptions<BillingOptions> options,
    IClock clock,
    ILogger<AccountPlanBusiness> logger) : IAccountPlanBusiness
{
    public async Task<AccountPlanServiceModel?> ResolveAsync(string accountId, CancellationToken cancellationToken)
    {
        var billing = options.Value;

        // No catalogue, no plan: every deployment today. Not the default plan and not an error — whatever reads a
        // plan keeps the flat limits it had before plans existed. Asked before the database, which has nothing to
        // add to an answer it cannot change.
        if (billing.Plans.Count == 0)
        {
            return null;
        }

        var span = await data.FindSpanAsync(accountId, clock.UtcNow, cancellationToken);
        var row = span.InForce;

        var plan = row?.PlanKey is { } key ? Find(billing, key) : null;

        if (row?.PlanKey is not null && plan is null)
        {
            // A plan the owner took out of the catalogue while accounts were still on it. Never a throw on a
            // creator's request (DL.2): the account reads as the default until somebody moves it, and this says
            // so. The row id rather than the account, so the log names the record to look at and no person.
            logger.LogWarning(
                "AccountPlan {AccountPlanId} names plan {PlanKey}, which the catalogue no longer has; it resolves to the default plan.",
                row.Id,
                row.PlanKey);
        }

        // Startup guarantees DefaultPlan names a plan whenever there are plans, so this cannot miss.
        var resolved = plan ?? Find(billing, billing.DefaultPlan!)!;

        return new AccountPlanServiceModel(
            resolved.Key,
            resolved.Name,
            resolved.Credits,
            resolved.Period,
            resolved.DailyImageCap,
            resolved.RunningImages,
            [.. resolved.QualityClasses],
            resolved.TrialDays,
            resolved.TrialImageCap,
            IsDefault: plan is null,
            row?.Source,
            row?.EffectiveFrom,
            span.NextStartsAt);
    }

    public async Task<AccountPlanChangeOutcome> ChangeAsync(
        string accountId,
        string? planKey,
        AccountPlanSource source,
        DateTimeOffset? effectiveFrom,
        CancellationToken cancellationToken)
    {
        // Refused here rather than left to the schema, because the data layer reads every refused write as a race
        // and a caller that re-reads and retries on that would retry these forever.
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountId.Length, BillingPolicy.AccountIdMaxLength, nameof(accountId));
        if (!Enum.IsDefined(source) || source == AccountPlanSource.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "A plan change names what made it.");
        }

        string? storedKey = null;

        if (planKey is not null)
        {
            // Stored as the catalogue spells it, so a key that differs only in case cannot fork into two plans.
            var plan = Find(options.Value, planKey);
            if (plan is null)
            {
                return AccountPlanChangeOutcome.UnknownPlan;
            }

            storedKey = plan.Key;
        }
        else if (options.Value.Plans.Count == 0)
        {
            // "The default" means nothing when there is no catalogue to have one.
            return AccountPlanChangeOutcome.UnknownPlan;
        }

        // Kept in UTC, like every other instant the application stores.
        effectiveFrom = effectiveFrom?.ToUniversalTime();

        // The end of the chain before the clock, not after. A row another writer has just appended was stamped with
        // their reading of the clock, which came before it was written, so it came before this one too: it can never
        // look like a change scheduled for later. Read the other way round, a lost race could.
        var latest = await data.FindLatestAsync(accountId, cancellationToken);
        var now = clock.UtcNow;

        if (effectiveFrom < now)
        {
            return AccountPlanChangeOutcome.InThePast;
        }

        // "Now" cannot go on the end of a chain whose end has not started yet: the row would land after the scheduled
        // one, so an upgrade asked for today would arrive at the old period's end and still say it had changed.
        // Replacing a scheduled change is AP.6.2's to design; until then it is refused and nothing is written.
        if (effectiveFrom is null && latest is not null && latest.EffectiveFrom > now)
        {
            return AccountPlanChangeOutcome.ChangeScheduled;
        }

        // Now, unless the end of the chain started on this very clock reading — two changes in one tick, or a frozen
        // test clock. A tick past it keeps the rows in order and inside CK_AccountPlans_After_Superseded, as the
        // quota's replacement does for its own range check.
        var at = effectiveFrom ?? (latest is not null && latest.EffectiveFrom == now
            ? latest.EffectiveFrom.AddTicks(1)
            : now);

        // Asked of an explicit start only: one that lands at or before the end of the chain would sit inside a row
        // already written. The schema would refuse it as well; refusing it here says why.
        if (latest is not null && at <= latest.EffectiveFrom)
        {
            return AccountPlanChangeOutcome.NotAfterLatest;
        }

        return await data.AppendAsync(accountId, latest, storedKey, source, at, cancellationToken);
    }

    private static PlanOption? Find(BillingOptions billing, string key) =>
        billing.Plans.FirstOrDefault(plan => string.Equals(plan.Key, key, StringComparison.OrdinalIgnoreCase));
}
