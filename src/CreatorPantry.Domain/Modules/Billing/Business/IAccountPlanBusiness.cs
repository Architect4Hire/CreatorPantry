using System.Collections.Concurrent;
using System.Diagnostics;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>Which plan an account is on, and putting it on another (PRICE-007, AP.4.1).</summary>
internal interface IAccountPlanBusiness
{
    /// <summary>
    /// The account's plan at <paramref name="at"/>, with the catalogue's terms; null when no plans are configured.
    /// </summary>
    /// <param name="at">The instant to read, or null for now.</param>
    Task<AccountPlanServiceModel?> ResolveAsync(
        string accountId, DateTimeOffset? at, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="qualityClassNames"/> the account's plan allows, and the cheapest plan that would
    /// allow each of the rest; null when no plans are configured.
    /// </summary>
    Task<AccountPlanQualityServiceModel?> ResolveQualityAsync(
        string accountId, IReadOnlyCollection<string> qualityClassNames, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the account on <paramref name="planKey"/> from <paramref name="effectiveFrom"/> on, or on the default
    /// plan when the key is null.
    /// </summary>
    /// <param name="effectiveFrom">
    /// When the change takes effect; null for now. A future instant schedules it, as a downgrade at period end
    /// does (B-54).
    /// </param>
    /// <param name="audit">
    /// Who asked and why, for the platform audit row staged with the change (AP.4.4). Null writes no audit row,
    /// which only a caller with an auditable record of its own may do — AP.6.2's Stripe path.
    /// </param>
    Task<AccountPlanChangeResult> ChangeAsync(
        string accountId,
        string? planKey,
        AccountPlanSource source,
        DateTimeOffset? effectiveFrom,
        AccountPlanChangeAudit? audit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Accounts whose plan in force is <paramref name="planKey"/>, by account id, a page at a time (AP.4.4).
    /// </summary>
    Task<OperationResult<AccountsOnPlanServiceModel>> ListByPlanAsync(
        string planKey, string? cursor, int limit, CancellationToken cancellationToken);
}

/// <summary>Who made a plan change and why, for the audit row (AP.4.4).</summary>
public sealed record AccountPlanChangeAudit(PlatformActor Actor, string Reason);

/// <summary>
/// What a plan change did (AP.4.4).
/// </summary>
/// <param name="Scheduled">
/// True when the change takes effect later rather than now, so the account is still on its old plan. Decided
/// here, where the instant the row was written with is known — a caller comparing keys afterwards cannot tell a
/// scheduled change from one that landed, and a change to the plan the account was already on from neither.
/// </param>
internal sealed record AccountPlanChangeResult(AccountPlanChangeOutcome Outcome, bool Scheduled = false)
{
    public static readonly AccountPlanChangeResult Changed = new(AccountPlanChangeOutcome.Changed);

    public static AccountPlanChangeResult Refused(AccountPlanChangeOutcome outcome) => new(outcome);
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
    RemovedPlanWarnings warnings,
    ILogger<AccountPlanBusiness> logger) : IAccountPlanBusiness
{
    public async Task<AccountPlanServiceModel?> ResolveAsync(
        string accountId, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var billing = options.Value;

        // No catalogue, no plan: every deployment today. Not the default plan and not an error — whatever reads a
        // plan keeps the flat limits it had before plans existed. Asked before the database, which has nothing to
        // add to an answer it cannot change.
        if (billing.Plans.Count == 0)
        {
            return null;
        }

        var now = clock.UtcNow;
        var span = await data.FindSpanAsync(accountId, at ?? now, cancellationToken);
        var row = span.InForce;

        var plan = row?.PlanKey is { } key ? Find(billing, key) : null;

        // Throttled per row (AP.4.2): admission resolves a plan on every AI and picture request, and an
        // unthrottled warning would log once per request for as long as nobody moved the account.
        if (row?.PlanKey is not null && plan is null && warnings.ShouldWarn(row.Id, now))
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
            // The enum's spelling, as the published catalogue states it (AP.4.5), not the catalogue section's
            // own `high-2k`: two Billing reads of the same fact disagreeing about how to spell it is how a
            // caller ends up writing the comparison PictureQualityVocabulary exists to be.
            QualityClassNames(resolved),
            resolved.TrialDays,
            resolved.TrialImageCap,
            IsDefault: plan is null,
            row?.Source,
            row?.EffectiveFrom,
            span.NextStartsAt);
    }

    /// <remarks>
    /// <para>
    /// Resolved through <see cref="ResolveAsync"/>, so the plan judged here is the plan every other reader sees —
    /// a removed plan reads as the default, with that reader's throttled warning, rather than as a second answer.
    /// </para>
    /// <para>
    /// The cheapest plan that would allow a locked class is the first in catalogue order that lists it, because
    /// the catalogue is ordered cheapest first (it is what the landing page renders in order). None means no plan
    /// allows it: a class this host offers that no plan reaches, which is a catalogue nobody can buy their way
    /// into rather than an error — the caller says "locked" and names nothing.
    /// </para>
    /// </remarks>
    public async Task<AccountPlanQualityServiceModel?> ResolveQualityAsync(
        string accountId, IReadOnlyCollection<string> qualityClassNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(qualityClassNames);

        var plan = await ResolveAsync(accountId, null, cancellationToken);

        if (plan is null)
        {
            return null;
        }

        var billing = options.Value;
        var allowed = plan.QualityClasses
            .Select(PictureQualityVocabulary.Normalise)
            .ToHashSet(StringComparer.Ordinal);

        var classes = qualityClassNames
            .Select(name =>
            {
                var normalised = PictureQualityVocabulary.Normalise(name);

                if (allowed.Contains(normalised))
                {
                    return new PlanQualityClassServiceModel(name, Allowed: true, null, null);
                }

                var upgrade = billing.Plans.FirstOrDefault(option => Allows(option, normalised));

                return new PlanQualityClassServiceModel(name, Allowed: false, upgrade?.Key, upgrade?.Name);
            })
            .ToList();

        return new AccountPlanQualityServiceModel(plan.Key, plan.Name, classes);
    }

    /// <remarks>
    /// Accounts with no row at all are not listed, and cannot be: they are on the configured default by absence,
    /// and Billing has no table of accounts — Identity owns those. What this answers is "whose stored plan is
    /// this", which is the question an operator can act on, and the route says so.
    /// </remarks>
    public async Task<OperationResult<AccountsOnPlanServiceModel>> ListByPlanAsync(
        string planKey, string? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planKey);

        var billing = options.Value;

        if (billing.Plans.Count == 0)
        {
            return OperationResult<AccountsOnPlanServiceModel>.Failure(new OperationError(
                AccountPlanAdministrationErrors.NoCatalogue,
                "No plan catalogue is configured on this host.",
                new Dictionary<string, string[]>()));
        }

        var plan = Find(billing, planKey);

        if (plan is null)
        {
            return OperationResult<AccountsOnPlanServiceModel>.Failure(new OperationError(
                AccountPlanAdministrationErrors.Unprocessable,
                "That plan is not in the catalogue.",
                new Dictionary<string, string[]> { ["planKey"] = ["No plan in the catalogue has that key."] }));
        }

        var page = Math.Clamp(limit, 1, AccountPlanAdministrationPolicy.ListMaxLimit);

        // A row that says "the configured default" resolves to whichever plan that is, so asking who is on the
        // default has to find those rows too — otherwise the answer leaves out everybody who was moved there
        // explicitly. So does a row naming a plan the catalogue no longer has, which ResolveAsync also reads as
        // the default: leaving those out would make "who is on the default plan" quietly incomplete exactly
        // when it matters, after a plan was withdrawn.
        var isDefault = string.Equals(plan.Key, billing.DefaultPlan, StringComparison.OrdinalIgnoreCase);

        // One more than the page, to learn whether there is another without counting the table.
        var rows = await data.ListInForceByPlanAsync(
            plan.Key,
            isDefault,
            isDefault ? [.. billing.Plans.Select(option => option.Key)] : [],
            clock.UtcNow,
            cursor,
            page + 1,
            cancellationToken);

        var accounts = rows
            .Take(page)
            .Select(row => new AccountOnPlanServiceModel(
                row.AccountId, row.PlanKey, row.Source, row.EffectiveFrom))
            .ToList();

        return OperationResult<AccountsOnPlanServiceModel>.Success(new AccountsOnPlanServiceModel(
            plan.Key, accounts, rows.Count > page ? accounts[^1].AccountId : null));
    }

    public async Task<AccountPlanChangeResult> ChangeAsync(
        string accountId,
        string? planKey,
        AccountPlanSource source,
        DateTimeOffset? effectiveFrom,
        AccountPlanChangeAudit? audit,
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

        // No catalogue, no plan to move anybody to — said as its own answer, because "that key is not a plan
        // here" would send an operator looking for a typo on a host that has no plans at all.
        if (options.Value.Plans.Count == 0)
        {
            return AccountPlanChangeResult.Refused(AccountPlanChangeOutcome.NoCatalogue);
        }

        if (planKey is not null)
        {
            // Stored as the catalogue spells it, so a key that differs only in case cannot fork into two plans.
            var plan = Find(options.Value, planKey);
            if (plan is null)
            {
                return AccountPlanChangeResult.Refused(AccountPlanChangeOutcome.UnknownPlan);
            }

            storedKey = plan.Key;
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
            return AccountPlanChangeResult.Refused(AccountPlanChangeOutcome.InThePast);
        }

        // "Now" cannot go on the end of a chain whose end has not started yet: the row would land after the scheduled
        // one, so an upgrade asked for today would arrive at the old period's end and still say it had changed.
        // Replacing a scheduled change is AP.6.2's to design; until then it is refused and nothing is written.
        if (effectiveFrom is null && latest is not null && latest.EffectiveFrom > now)
        {
            return AccountPlanChangeResult.Refused(AccountPlanChangeOutcome.ChangeScheduled);
        }

        // Now, unless the end of the chain started on this very clock reading — two changes in one tick, or a frozen
        // test clock. A tick past it keeps the rows in order and inside CK_AccountPlans_After_Superseded, as the
        // quota's replacement does for its own range check.
        var at = effectiveFrom ?? (latest is not null && latest.EffectiveFrom == now
            ? latest.EffectiveFrom.AddTicks(1)
            : now);

        // The same command twice writes once (AP.4.4). A chain is append-only and this route takes no idempotency
        // key, so a retry — an operator's double submit, a lost answer — would otherwise append a second row and
        // a second audit entry recording a change that did not happen. Compared against the end of the chain
        // rather than the resolved plan, because putting an account with no row onto the plan it already
        // resolves to does do something: it pins that choice, and `by-plan` then lists it.
        if (latest is not null
            && string.Equals(latest.PlanKey, storedKey, StringComparison.OrdinalIgnoreCase)
            && (effectiveFrom is { } asked ? asked == latest.EffectiveFrom : latest.EffectiveFrom <= now))
        {
            return AccountPlanChangeResult.Refused(AccountPlanChangeOutcome.Unchanged);
        }

        // Asked of an explicit start only: one that lands at or before the end of the chain would sit inside a row
        // already written. The schema would refuse it as well; refusing it here says why.
        if (latest is not null && at <= latest.EffectiveFrom)
        {
            return AccountPlanChangeResult.Refused(AccountPlanChangeOutcome.NotAfterLatest);
        }

        // Named for what the change actually does rather than for what was asked: a start in the future is a
        // schedule, and an audit trail that called it a set would misreport a downgrade at period end (B-54).
        var entry = audit is null
            ? null
            : new PlatformAuditEntry(
                audit.Actor.Type,
                audit.Actor.Id,
                audit.Actor.Name,
                at > now ? AccountPlanAuditActions.Scheduled : AccountPlanAuditActions.Set,
                AccountPlanAuditActions.SubjectType,
                accountId,
                CorrelationId(),
                audit.Reason,
                AccountPlanAdministrationPolicy.AuditReference(latest?.PlanKey),
                AccountPlanAdministrationPolicy.AuditReference(storedKey, at > now ? at : null));

        var scheduled = at > now;
        var outcome = await data.AppendAsync(accountId, latest, storedKey, source, at, entry, cancellationToken);

        return new AccountPlanChangeResult(outcome, scheduled && outcome is AccountPlanChangeOutcome.Changed);
    }

    /// <summary>
    /// The request's trace, so an audit row can be tied to the request that wrote it; a new id when there is no
    /// ambient activity, which is better than a nought nobody can correlate.
    /// </summary>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    /// <summary>
    /// A plan's quality classes in the one spelling every Billing read publishes (AP.4.5). A name matching no
    /// class is left out, as the catalogue leaves it out; startup refuses such a plan.
    /// </summary>
    private static IReadOnlyList<string> QualityClassNames(PlanOption plan) =>
        PictureQualityVocabulary.CanonicalAll(plan.QualityClasses);

    /// <summary>Whether a plan's configured class list names the class <paramref name="normalised"/> is.</summary>
    private static bool Allows(PlanOption plan, string normalised) =>
        plan.QualityClasses.Any(listed => PictureQualityVocabulary.Normalise(listed) == normalised);

    private static PlanOption? Find(BillingOptions billing, string key) =>
        billing.Plans.FirstOrDefault(plan => string.Equals(plan.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Whether a row naming a removed plan has been warned about recently enough to say nothing (AP.4.2).
/// </summary>
/// <remarks>
/// A singleton, so the interval holds across requests; per process, so each instance says it once. Keyed by the
/// row, which is bounded by how many accounts are on removed plans rather than by traffic.
/// </remarks>
internal sealed class RemovedPlanWarnings
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _warnedAt = new();

    public bool ShouldWarn(Guid accountPlanId, DateTimeOffset now)
    {
        while (true)
        {
            if (!_warnedAt.TryGetValue(accountPlanId, out var last))
            {
                if (_warnedAt.TryAdd(accountPlanId, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < BillingPolicy.RemovedPlanWarningInterval)
            {
                return false;
            }

            // Only the caller that moves the stamp warns, so two requests at the boundary log once.
            if (_warnedAt.TryUpdate(accountPlanId, now, last))
            {
                return true;
            }
        }
    }
}
