using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
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
    IAiQuotaPeriodResolver periods,
    IClock clock,
    IOptions<AiQuotaOptions> options) : IAiQuotaAdmissionBusiness
{
    private readonly AiQuotaOptions _options = options.Value;

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
