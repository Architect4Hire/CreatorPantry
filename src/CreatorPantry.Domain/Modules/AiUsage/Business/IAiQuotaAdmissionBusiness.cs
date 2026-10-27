using System.Diagnostics;
using System.Globalization;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>
/// Decides whether an account may spend, what to hold while it does, and what to charge afterwards.
/// </summary>
/// <remarks>
/// Every decision here is deterministic domain code over stored facts and configuration. A model never
/// participates in it and never sees a balance (ai.md), and nothing here reads an AI, recipe or membership
/// table — this module is told who to charge and never looks it up.
/// </remarks>
internal interface IAiQuotaAdmissionBusiness
{
    Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        AiQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken);

    Task<AiQuotaStateServiceModel> PeekAsync(
        string accountId, AiTaskType taskType, bool isMeterable, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IAiQuotaAdmissionFacade.QuoteImageAsync"/>
    Task<AiImageQuoteServiceModel> QuoteImageAsync(
        string accountId,
        string modelName,
        string? resolution,
        string? quality,
        int variantCount,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IAiQuotaAdmissionFacade.ImageCredits"/>
    decimal? ImageCredits(string modelName, string? resolution, string? quality);

    /// <inheritdoc cref="Facade.IAiQuotaAdmissionFacade.AdmitImageAsync"/>
    Task<AiQuotaAdmissionServiceModel> AdmitImageAsync(
        AiImageQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken);

    /// <summary>
    /// Settles an image generation's hold from what it was billed for, and gives back the account it is
    /// charged to; null when the hold was already settled (AP.2.5). Stages; does not save.
    /// </summary>
    Task<string?> StageImageSettlementAsync(
        AiImageUsageServiceModel usage, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IAiQuotaAdmissionFacade.IsImageHoldLiveAsync"/>
    Task<bool> IsImageHoldLiveAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IAiQuotaAdmissionFacade.ReleaseImageAsync"/>
    Task ReleaseImageAsync(Guid reservationId, CancellationToken cancellationToken);

    Task StageSettlementAsync(
        Guid reservationId,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken);

    Task PostAsync(Guid reservationId, CancellationToken cancellationToken);

    Task RenewAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<AiQuotaMaintenanceSummary> SweepAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiQuotaAdmissionBusiness"/>
internal sealed class AiQuotaAdmissionBusiness(
    IAiQuotaDataLayer dataLayer,
    IPlatformImageSpendDataLayer platformSpend,
    IAiQuotaPeriodResolver periods,
    IClock clock,
    IOptions<AiQuotaOptions> options,
    IOptions<AiImageListPriceOptions> listPrices,
    ILogger<AiQuotaAdmissionBusiness> logger) : IAiQuotaAdmissionBusiness
{
    /// <summary>The alerting event for a tripped breaker: one a day, raised by the refusal that tripped it.</summary>
    private static readonly EventId BreakerTripped = new(9701, "PlatformImageSpendCeilingTripped");

    private readonly AiQuotaOptions _options = options.Value;

    private readonly AiImageListPriceOptions _listPrices = listPrices.Value;

    public async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        AiQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refused rather than admitted against nobody. The caller resolves this from IWorkspaceContext, so an
        // empty value means the resolution path is broken, not that this run is unattributable.
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AccountId);

        if (request.TaskType is AiTaskType.Unspecified)
        {
            throw new ArgumentException("An admitted run must name the task it runs.", nameof(request));
        }

        // Nothing is read, nothing is held, and no period is even opened. A run that cannot reach a provider
        // cannot spend allowance, so making it queue behind one would be a refusal nothing earned.
        if (!request.IsMeterable)
        {
            return new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.NotMetered, null, 0m, 0m, AiQuotaUnit.Credits, null);
        }

        // A replayed admission for the same claim finds the hold it already took. The unique index is the
        // backstop for a genuine race; this read is what makes the ordinary redelivery cheap and quiet.
        var existing = await dataLayer.FindReservationAsync(
            request.AiOperationId, request.LeaseToken, cancellationToken);

        if (existing is not null)
        {
            return new AiQuotaAdmissionServiceModel(
                existing.Status is AiQuotaReservationStatus.Held
                    ? AiQuotaAdmissionOutcome.Admitted
                    : AiQuotaAdmissionOutcome.Busy,
                existing.Status is AiQuotaReservationStatus.Held ? existing.Id : null,
                existing.ReservedAmount,
                0m,
                existing.Unit,
                null);
        }

        var now = clock.UtcNow;
        var terms = await periods.ResolveTermsAsync(request.AccountId, cancellationToken);

        // Read from the terms in force now, never from the period's frozen copy: a suspension is "AI access is
        // switched off", which is a fact about today rather than a term the running period was opened under.
        if (terms.IsSuspended)
        {
            return new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Suspended, null, 0m, 0m, terms.Unit, null);
        }

        var period = await periods.OpenAsync(request.AccountId, terms, now, cancellationToken);

        var perAttempt = AiQuotaUnitConverter.Estimate(period.Unit, EstimateFor(request.TaskType), _options);
        var hold = AiQuotaUnitConverter.Round(perAttempt * CallsPerRun());

        var (outcome, reservation, written) = await dataLayer.ReserveAsync(
            request.AccountId,
            now,
            current => Covers(current, hold)
                ? new AccountAiQuotaReservation
                {
                    Id = Guid.NewGuid(),
                    AccountId = request.AccountId,
                    PeriodId = current.Id,
                    AiOperationId = request.AiOperationId,
                    LeaseToken = request.LeaseToken,
                    TaskType = request.TaskType,
                    Unit = current.Unit,
                    ReservedAmount = hold,
                    PerAttemptEstimate = AiQuotaUnitConverter.Round(perAttempt),
                    Status = AiQuotaReservationStatus.Held,
                    HeldAt = now,
                    ExpiresAt = request.ExpiresAt,
                }
                : null,
            platform: null,
            cancellationToken);

        // The period as the write left it, which after a retry is not the instance resolved above.
        var settled = written ?? period;

        return outcome switch
        {
            AiQuotaWriteOutcome.Applied => new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Admitted,
                reservation!.Id,
                hold,
                Remaining(settled),
                settled.Unit,
                settled.EndsAt),

            AiQuotaWriteOutcome.Refused => new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Exhausted,
                null,
                0m,
                Remaining(settled),
                settled.Unit,
                settled.EndsAt),

            // Contended, or a period that vanished between two reads, which nothing does. Neither is a
            // statement about the account's balance, so neither refuses the run: the caller requeues.
            _ => new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Busy, null, 0m, 0m, settled.Unit, settled.EndsAt),
        };
    }

    /// <remarks>
    /// <para>
    /// <strong>Reads, and writes nothing at all — including no period.</strong> USAGE-006 refuses a request
    /// before the operation runs and before a provider is called, and a refusal that opened a period would have
    /// spent something to say "you may not spend". For an account whose period has not been opened yet, the
    /// period is <em>built</em> from the same terms and the same carry-over rule an admission would open it
    /// with, and then thrown away — which is why <see cref="IAiQuotaPeriodResolver"/> is shared rather than approximated here. A
    /// peek that under-reported an account's carry-over would refuse requests the worker would then admit.
    /// </para>
    /// <para>
    /// <strong>An unmeterable task is always available.</strong> It cannot spend anything, so it is not held
    /// against a balance — not even an overspent one, where every other task is refused.
    /// </para>
    /// </remarks>
    public async Task<AiImageQuoteServiceModel> QuoteImageAsync(
        string accountId,
        string modelName,
        string? resolution,
        string? quality,
        int variantCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(variantCount);

        var now = clock.UtcNow;
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        if (terms.IsSuspended)
        {
            // No balance and no reset, for the reason PeekAsync gives: a suspension has nothing to wait for.
            return new AiImageQuoteServiceModel(
                AiImageQuoteOutcome.Suspended, terms.Unit, variantCount, null, null, 0m, 0m, null, 0, false, null, null);
        }

        // Projected, never opened: a quote is a GET, and asking a price must not spend a row (AiUsageReadBusiness).
        var period = await periods.ProjectAsync(accountId, terms, now, cancellationToken);
        var spendable = period.Allowance + period.CarriedOver;
        var remaining = Remaining(period);

        // The same price admission takes, from the same converter on the same inputs, so the figure a creator
        // is shown is the hold their request is then admitted against — never an approximation of it.
        var perImage = AiImageQuotaConverter.Charge(period.Unit, modelName, resolution, quality, _options);

        if (perImage is not > 0m)
        {
            return new AiImageQuoteServiceModel(
                AiImageQuoteOutcome.Unpriced, period.Unit, variantCount, null, null, spendable, remaining,
                null, 0, false, period.EndsAt, period.TimeZoneId);
        }

        var total = AiQuotaUnitConverter.Round(perImage.Value * variantCount);
        var affordable = Covers(period, total);

        // Counted down with admission's own predicate and rounding rather than divided, so "enough for two"
        // is a request for two that admission would then accept.
        var fits = variantCount;

        while (fits > 0 && !Covers(period, AiQuotaUnitConverter.Round(perImage.Value * fits)))
        {
            fits--;
        }

        decimal? after = affordable ? remaining - total : null;

        return new AiImageQuoteServiceModel(
            affordable ? AiImageQuoteOutcome.Affordable : AiImageQuoteOutcome.Exhausted,
            period.Unit,
            variantCount,
            AiQuotaUnitConverter.Round(perImage.Value),
            total,
            spendable,
            remaining,
            after,
            fits,
            after is { } left
                && AiQuotaPeriodCalculator.IsNearlySpent(spendable, left, _options.NearlySpentFraction),
            period.EndsAt,
            period.TimeZoneId);
    }

    /// <remarks>
    /// Rounded as a hold is, so the per-picture figure a picker shows is the one a quote for one picture shows.
    /// </remarks>
    public decimal? ImageCredits(string modelName, string? resolution, string? quality) =>
        AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, modelName, resolution, quality, _options) is > 0m and var credits
            ? AiQuotaUnitConverter.Round(credits)
            : null;

    public async Task<AiQuotaStateServiceModel> PeekAsync(
        string accountId, AiTaskType taskType, bool isMeterable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var now = clock.UtcNow;
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        if (terms.IsSuspended)
        {
            // No balance and no reset: a suspension has nothing to wait for, and reporting a remaining figure
            // for an account that cannot spend it would be an invitation to try again (USAGE-007).
            return new AiQuotaStateServiceModel(
                AiQuotaStateOutcome.Suspended, terms.Unit, 0m, 0m, 0m, null);
        }

        var period = await periods.ProjectAsync(accountId, terms, now, cancellationToken);

        var required = isMeterable
            ? AiQuotaUnitConverter.Round(
                AiQuotaUnitConverter.Estimate(period.Unit, EstimateFor(taskType), _options) * CallsPerRun())
            : 0m;

        // The same predicate admission applies, so the two cannot disagree about where the line is. At the
        // limit exactly is available: a hold the balance covers to the penny is one the balance covers.
        var covered = !isMeterable || Covers(period, required);

        return new AiQuotaStateServiceModel(
            covered ? AiQuotaStateOutcome.Available : AiQuotaStateOutcome.Exhausted,
            period.Unit,
            period.Allowance + period.CarriedOver,
            Remaining(period),
            required,
            period.EndsAt);
    }

    /// <remarks>
    /// <para>
    /// <strong>Every billable attempt is charged something.</strong> One that reported its tokens is charged
    /// what they convert to; one that did not is charged <c>PerAttemptEstimate</c>, because a call whose cost
    /// the provider declined to report is not a call that was free — the reading of USAGE-005 that the ledger's
    /// nullable counts already encode. Releasing it instead would make an unreported call cheaper than an
    /// admitted one, which is a thing worth doing on purpose.
    /// </para>
    /// <para>
    /// A run with nothing billable in it is released outright. It never reached a provider, which is a known
    /// zero rather than an unknown, and the two are different rows.
    /// </para>
    /// </remarks>
    public async Task StageSettlementAsync(
        Guid reservationId,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var now = clock.UtcNow;

        var outcome = await dataLayer.StageSettlementAsync(
            reservationId,
            reservation =>
            {
                // A replay. The recorded outcome stands and nothing is written a second time, which is what
                // makes a redelivered settlement post once (USAGE-004).
                if (reservation.Status is not AiQuotaReservationStatus.Held)
                {
                    return false;
                }

                var billable = attempts.Where(attempt => attempt.IsBillable).ToList();

                if (billable.Count == 0)
                {
                    reservation.Status = AiQuotaReservationStatus.Released;
                    reservation.SettledAmount = 0m;
                    reservation.UsageReported = false;
                    reservation.SettledAt = now;

                    return true;
                }

                var charges = billable
                    .Select(attempt => AiQuotaUnitConverter.Charge(
                        reservation.Unit,
                        attempt.ModelName,
                        attempt.InputTokens,
                        attempt.OutputTokens,
                        _options))
                    .ToList();

                reservation.Status = AiQuotaReservationStatus.Settled;
                reservation.UsageReported = charges.TrueForAll(charge => charge is not null);

                // Summed raw and rounded once, which is what AiUsagePolicy.AllowanceScale's remarks say the
                // scale exists for: rounding each attempt would accumulate the error across a long run.
                reservation.SettledAmount = AiQuotaUnitConverter.Round(
                    charges.Sum(charge => charge ?? reservation.PerAttemptEstimate));

                reservation.SettledAt = now;

                return true;
            },
            cancellationToken);

        if (outcome is AiQuotaWriteOutcome.NotFound)
        {
            // An invariant, not a branch: the only caller settles against the id admission handed it. It
            // throws rather than letting a run finish having spent nothing, because an unsettled hold strands
            // an account's allowance until the lease expires and no one would notice it had.
            throw new InvalidOperationException(
                $"AI quota reservation {reservationId} does not exist, so the attempts it admitted cannot be "
                    + "settled against it.");
        }
    }

    public async Task PostAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await dataLayer.PostAsync(reservationId, clock.UtcNow, cancellationToken);

    public async Task RenewAsync(
        Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        await dataLayer.RenewAsync(reservationId, expiresAt, cancellationToken);

    public async Task<AiQuotaMaintenanceSummary> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var posted = 0;
        var expired = 0;
        var finalized = 0;

        // Posting first: a hold that has already settled must land its charge rather than be expired out from
        // under it. Doing it in the other order would still be correct -- expiry declines to touch a settled
        // row -- but it would leave the charge unposted for another whole sweep.
        foreach (var id in await dataLayer.FindUnpostedReservationIdsAsync(cancellationToken))
        {
            if (await dataLayer.PostAsync(id, now, cancellationToken) is AiQuotaWriteOutcome.Applied)
            {
                posted++;
            }
        }

        foreach (var id in await dataLayer.FindLapsedReservationIdsAsync(now, cancellationToken))
        {
            if (await dataLayer.ExpireAsync(id, now, cancellationToken) is AiQuotaWriteOutcome.Applied)
            {
                expired++;
            }
        }

        foreach (var id in await dataLayer.FindFinalizablePeriodIdsAsync(now, cancellationToken))
        {
            if (await dataLayer.FinalizePeriodAsync(id, now, cancellationToken) is AiQuotaWriteOutcome.Applied)
            {
                finalized++;
            }
        }

        return new AiQuotaMaintenanceSummary(posted, expired, finalized);
    }


    /// <summary>
    /// What one provider call of this capability is estimated at, in credits.
    /// </summary>
    /// <remarks>
    /// Configured, never learned. A trailing average of the account's own history would make two identical
    /// requests answerable differently and a competing-worker result irreproducible. An unconfigured estimate
    /// is refused rather than treated as zero, on the same reasoning <c>ConfiguredAiCostEstimator</c> returns
    /// null instead of zero for a model it has no price for: an unknown cost is not a free one.
    /// </remarks>
    private decimal EstimateFor(AiTaskType taskType)
    {
        var estimate = _options.TaskEstimates.TryGetValue(taskType, out var configured)
            ? configured
            : _options.DefaultTaskEstimate;

        if (estimate <= 0m)
        {
            throw new InvalidOperationException(
                $"No positive AI quota estimate is configured for task '{taskType}', and none is assumed. Set "
                    + $"{AiQuotaOptions.SectionName}:TaskEstimates:{taskType} or "
                    + $"{AiQuotaOptions.SectionName}:DefaultTaskEstimate.");
        }

        return estimate;
    }

    /// <summary>
    /// How many provider calls one run is held for.
    /// </summary>
    /// <remarks>
    /// Below one is misconfiguration rather than a choice — it would hold less than the single call every run
    /// makes — and it is refused rather than clamped, because silently correcting a number an operator wrote
    /// means the allowance they think they configured is not the one in force.
    /// </remarks>
    private int CallsPerRun() =>
        _options.EstimatedCallsPerRun >= 1
            ? _options.EstimatedCallsPerRun
            : throw new InvalidOperationException(
                $"{AiQuotaOptions.SectionName}:{nameof(AiQuotaOptions.EstimatedCallsPerRun)} is "
                    + $"{_options.EstimatedCallsPerRun}; a run makes at least one provider call.");

    /// <summary>The admission predicate, and the only place a balance is read.</summary>
    /// <summary>
    /// The lease token an image reservation is filed under.
    /// </summary>
    /// <remarks>
    /// <strong>Empty, because there is no lease to name.</strong> A completion is admitted by the worker that
    /// already holds a claim, so its reservation is keyed by operation <em>and</em> claim: one claim, one hold.
    /// A picture is admitted at the request, before any worker has claimed it, and then settled once however
    /// many passes it takes — so the operation alone is the key, and the unique index on
    /// <c>(AiOperationId, LeaseToken)</c> enforces exactly that with this constant in the second column.
    /// </remarks>
    private static readonly Guid ImageLeaseToken = Guid.Empty;

    public async Task<AiQuotaAdmissionServiceModel> AdmitImageAsync(
        AiImageQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelName);

        if (request.VariantCount < 1)
        {
            throw new ArgumentException(
                "An admitted generation must ask for at least one picture.", nameof(request));
        }

        // A replayed admission for the same generation finds the hold it already took, exactly as a
        // redelivered claim does. The unique index is the backstop for a genuine race.
        var existing = await dataLayer.FindReservationAsync(
            request.GeneratedImageOperationId, ImageLeaseToken, cancellationToken);

        if (existing is not null)
        {
            return new AiQuotaAdmissionServiceModel(
                existing.Status is AiQuotaReservationStatus.Held
                    ? AiQuotaAdmissionOutcome.Admitted
                    : AiQuotaAdmissionOutcome.Busy,
                existing.Status is AiQuotaReservationStatus.Held ? existing.Id : null,
                existing.ReservedAmount,
                0m,
                existing.Unit,
                null);
        }

        var now = clock.UtcNow;
        var terms = await periods.ResolveTermsAsync(request.AccountId, cancellationToken);

        if (terms.IsSuspended)
        {
            return new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Suspended, null, 0m, 0m, terms.Unit, null);
        }

        var period = await periods.OpenAsync(request.AccountId, terms, now, cancellationToken);

        // The exact price of one picture, not an estimate of it: the provider prices the model, the tier and
        // the quality, all of which are known before anything is generated (AP.2.4). An unrated picture has no
        // price, and is refused rather than admitted for nothing (PRICE-001).
        var perImage = AiImageQuotaConverter.Charge(
            period.Unit, request.ModelName, request.Resolution, request.Quality, _options);

        if (perImage is not > 0m)
        {
            return new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Unrated, null, 0m, Remaining(period), period.Unit, period.EndsAt);
        }

        var hold = AiQuotaUnitConverter.Round(perImage.Value * request.VariantCount);

        // What the request adds to the platform's day, at list price, when a ceiling is configured (AP.2.7). A
        // picture with a credit rate and no list price cannot be counted, and is refused rather than let through
        // uncounted — the same reading of an unknown cost as Unrated, and startup refuses such a host anyway.
        PlatformImageSpendCharge? platform = null;

        if (_options.PlatformImageSpendPerDay is { } ceiling)
        {
            if (AiImageRateKeys.Figure(_listPrices.Images, request.ModelName, request.Resolution, request.Quality)
                is not { } listPrice)
            {
                return new AiQuotaAdmissionServiceModel(
                    AiQuotaAdmissionOutcome.Unrated, null, 0m, Remaining(period), period.Unit, period.EndsAt);
            }

            platform = new PlatformImageSpendCharge(
                PlatformImageSpendPolicy.DayOf(now),
                PlatformImageSpendPolicy.ToMicros(listPrice * request.VariantCount),
                PlatformImageSpendPolicy.ToMicros(ceiling),
                request.GeneratedImageOperationId,
                ImageLeaseToken);
        }

        // Committed here, not staged: the period's row version is what makes two admissions for one account
        // unable to both succeed, and a concurrency check needs its own transaction. So the hold exists before
        // the operation row does, and a caller whose operation then fails to write owes a release —
        // ReleaseImageAsync, with the expiry sweep behind it.
        var (outcome, reservation, written) = await dataLayer.ReserveAsync(
            request.AccountId,
            now,
            current => Covers(current, hold)
                ? new AccountAiQuotaReservation
                {
                    Id = Guid.NewGuid(),
                    AccountId = request.AccountId,
                    PeriodId = current.Id,
                    AiOperationId = request.GeneratedImageOperationId,
                    LeaseToken = ImageLeaseToken,
                    TaskType = AiTaskType.ImageGeneration,
                    Unit = current.Unit,
                    ReservedAmount = hold,

                    // The per-picture price. Settlement multiplies it by what was billed, so it never needs
                    // the model, tier or quality again — which is what lets a worker that does not know them
                    // settle correctly.
                    PerAttemptEstimate = AiQuotaUnitConverter.Round(perImage.Value),
                    Status = AiQuotaReservationStatus.Held,
                    HeldAt = now,
                    ExpiresAt = request.ExpiresAt,
                }
                : null,
            platform,
            cancellationToken);

        var settled = written ?? period;

        if (outcome is AiQuotaWriteOutcome.Applied && platform is not null)
        {
            PlatformImageSpendTelemetry.RecordAdmitted(platform.Micros);
        }

        if (outcome is AiQuotaWriteOutcome.OverPlatformCeiling)
        {
            await TripAsync(platform!, now, cancellationToken);

            // No balance, no reset and no figure of any kind: the account's own allowance is not why, and every
            // number behind the real reason is everybody's. Required stays the account's own hold, which is the
            // one figure here that is theirs, for a caller that logs it.
            return new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.PlatformCeiling, null, 0m, 0m, settled.Unit, null) { Required = hold };
        }

        return outcome switch
        {
            AiQuotaWriteOutcome.Applied => new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Admitted,
                reservation!.Id,
                hold,
                Remaining(settled),
                settled.Unit,
                settled.EndsAt) { Required = hold },

            // Carrying what it would have taken, which is what the refusal has to be able to tell a creator
            // (USAGE-007): "your allowance is spent" alone does not say whether they are one picture over or
            // twenty. Nothing was held, so Reserved stays nought.
            AiQuotaWriteOutcome.Refused => new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Exhausted,
                null,
                0m,
                Remaining(settled),
                settled.Unit,
                settled.EndsAt) { Required = hold },

            _ => new AiQuotaAdmissionServiceModel(
                AiQuotaAdmissionOutcome.Busy, null, 0m, 0m, settled.Unit, settled.EndsAt) { Required = hold },
        };
    }

    /// <remarks>
    /// <para>
    /// <strong>Exact, not estimated.</strong> A completion's settlement converts reported tokens and falls back
    /// to an estimate for a call the provider said nothing about; a picture has a price, held on the
    /// reservation as <c>PerAttemptEstimate</c>, so the charge is that price times the number of pictures the
    /// provider actually made. Nothing is read from configuration here, which also means a rate changed between
    /// the request and the settlement cannot re-price a generation already admitted.
    /// </para>
    /// <para>
    /// <strong>Nothing billed is a release, not a zero charge.</strong> A generation whose every call failed
    /// before anything was generated owes nothing, and holding its allowance until the lease lapsed would
    /// strand it.
    /// </para>
    /// </remarks>
    public async Task<string?> StageImageSettlementAsync(
        AiImageUsageServiceModel usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var now = clock.UtcNow;
        string? accountId = null;

        var outcome = await dataLayer.StageSettlementAsync(
            usage.ReservationId,
            reservation =>
            {
                // A replay. The recorded outcome stands and nothing is written a second time, which is what
                // makes a redelivered settlement post once (USAGE-004).
                if (reservation.Status is not AiQuotaReservationStatus.Held)
                {
                    return false;
                }

                accountId = reservation.AccountId;

                if (usage.BilledVariants == 0)
                {
                    reservation.Status = AiQuotaReservationStatus.Released;
                    reservation.SettledAmount = 0m;
                    reservation.UsageReported = false;
                    reservation.SettledAt = now;

                    return true;
                }

                reservation.Status = AiQuotaReservationStatus.Settled;

                // True, and it means something different here than it does for a completion: an image provider
                // reports no tokens, but what it charged for is known exactly, which is the question this flag
                // is asked at settlement.
                reservation.UsageReported = true;
                reservation.SettledAmount = AiQuotaUnitConverter.Round(
                    reservation.PerAttemptEstimate * usage.BilledVariants);
                reservation.SettledAt = now;

                return true;
            },
            cancellationToken);

        if (outcome is AiQuotaWriteOutcome.NotFound)
        {
            throw new InvalidOperationException(
                $"Image generation {usage.GeneratedImageOperationId} settled against reservation "
                    + $"{usage.ReservationId}, which does not exist.");
        }

        return accountId;
    }

    public async Task<bool> IsImageHoldLiveAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await dataLayer.FindReservationByIdAsync(reservationId, cancellationToken)
            is { Status: AiQuotaReservationStatus.Held, ReleasedAt: null };

    /// <remarks>
    /// Gives back what the hold added to the platform's day as well (AP.2.7): a request that was never queued
    /// can never reach a provider, so counting it would trip the breaker on spend that cannot happen. This is the
    /// only give-back the counter has — see <c>PlatformImageSpendDay.AdmittedMicros</c>.
    /// </remarks>
    public async Task ReleaseImageAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await dataLayer.ReleaseImageAsync(reservationId, clock.UtcNow, cancellationToken);

    /// <summary>
    /// Records a refusal for the ceiling and, for the one refusal that trips the day, raises the alert: the
    /// platform audit row and an error-level log line (PRICE-005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One alert a day, not one a refusal.</strong> The trip is a guarded write that exactly one caller
    /// wins, and only that caller logs; every refusal still counts on the metric, so a dashboard sees the
    /// pressure while the pager sees one event.
    /// </para>
    /// <para>
    /// <strong>A failure to alert never changes the creator's answer.</strong> The refusal has already been
    /// decided and nothing was written for it; an exception here would turn a deliberate 503 into a 500 that
    /// tells nobody anything. It is logged at the same level the trip would have been, so it still pages.
    /// </para>
    /// <para>
    /// The log line carries the day and the configured ceiling, never an account, a workspace or a model — what
    /// tripped it is the sum of everybody.
    /// </para>
    /// </remarks>
    private async Task TripAsync(PlatformImageSpendCharge charge, DateTimeOffset now, CancellationToken cancellationToken)
    {
        PlatformImageSpendTelemetry.RecordRefused();

        var ceiling = _options.PlatformImageSpendPerDay;

        try
        {
            var day = await platformSpend.FindDayAsync(charge.Day, cancellationToken);

            // Already tripped, or lifted since: nothing to raise. Read rather than attempted, so a client
            // retrying on 503 during an incident costs one read here, not a transaction on the row every
            // admission is waiting for. The guarded write below is still what decides a race.
            if (day is null or { TrippedAt: not null } or { LiftedAt: not null })
            {
                return;
            }

            var admitted = PlatformImageSpendPolicy.ToDollars(day.AdmittedMicros);

            var before = new PlatformImageSpendDayServiceModel(
                charge.Day, PlatformImageSpendPolicy.Currency, ceiling, admitted, null,
                PlatformImageSpendState.Armed, null, null, null);

            var entry = new PlatformAuditEntry(
                PlatformImageSpendPolicy.Breaker.Type,
                PlatformImageSpendPolicy.Breaker.Id,
                PlatformImageSpendPolicy.Breaker.Name,
                PlatformImageSpendAuditActions.Tripped,
                PlatformImageSpendAuditActions.SubjectType,
                charge.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CorrelationId(),
                PlatformImageSpendPolicy.TripReason,
                PlatformImageSpendPolicy.AuditReference(before),
                PlatformImageSpendPolicy.AuditReference(before with { State = PlatformImageSpendState.Tripped }));

            if (!await platformSpend.TripAsync(charge.Day, now, entry, cancellationToken))
            {
                return;
            }

            PlatformImageSpendTelemetry.RecordTripped();

            logger.LogError(
                BreakerTripped,
                "The platform's estimated image spend for {ImageSpendDay} reached its ceiling of {ImageSpendCeiling} USD "
                    + "({ImageSpendAdmitted} USD admitted). New picture requests are refused until the UTC day ends or "
                    + "an operator lifts the breaker.",
                charge.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ceiling,
                admitted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                BreakerTripped,
                exception,
                "A picture request was refused for the platform's image spend ceiling on {ImageSpendDay}, but the "
                    + "trip could not be recorded.",
                charge.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The ambient trace id as a Guid, so the trip's audit row ties back to the request that tripped it.
    /// </summary>
    /// <remarks>The derivation <c>AiUsageAdministrationBusiness</c> uses, for the same reason.</remarks>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static bool Covers(AccountAiQuotaPeriod period, decimal hold) =>
        period.Consumed + period.Reserved + hold <= period.Allowance + period.CarriedOver;

    /// <summary>
    /// What is left, from the shared calculator, so the figure a refusal reports is the one the creator's own
    /// usage read shows them.
    /// </summary>
    private static decimal Remaining(AccountAiQuotaPeriod period) =>
        AiQuotaPeriodCalculator.Remaining(
            period.Allowance, period.CarriedOver, period.Consumed, period.Reserved);

    // The terms, the period roll and the zone fallback all moved to IAiQuotaPeriodResolver when the read seam
    // needed them too. Two copies of a roll would eventually show a creator a balance the next admission did
    // not agree with.
}
