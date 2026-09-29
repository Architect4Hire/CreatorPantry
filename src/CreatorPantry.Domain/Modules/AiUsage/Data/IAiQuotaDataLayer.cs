using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>What a contended write against a period did.</summary>
internal enum AiQuotaWriteOutcome
{
    Applied = 1,

    /// <summary>The row it would have written was already in the state it wanted. Nothing was written.</summary>
    /// <remarks>
    /// Not a failure. Every write here is idempotent by a stored timestamp, so a replay, a retry after a lost
    /// response, and a sweep racing the worker that owns the row all land here.
    /// </remarks>
    AlreadyDone = 2,

    /// <summary>Nothing of that id exists.</summary>
    NotFound = 3,

    /// <summary>Competing writers kept the period moving until the retries ran out.</summary>
    Contended = 4,

    /// <summary>Business declined to write, having read the row. Nothing was written.</summary>
    /// <remarks>
    /// Distinct from <see cref="AlreadyDone"/>, which means the write was unnecessary. This means it was
    /// judged and refused — the balance did not cover the hold — and the caller carries the reason.
    /// </remarks>
    Refused = 5,
}

/// <summary>
/// Composes the quota's persistence operations, and owns the one thing this module's ledger seam deliberately
/// does not: transaction boundaries.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three separate boundaries, and the split is the design rather than an accident.</strong> Admission
/// reads a balance, so it must serialize against other admissions — its own transaction, the period's row
/// version, and a bounded retry. Applying a settlement writes a delta and reads no balance, so it is a second
/// transaction that runs after the attempt it describes has already committed. Expiry is the same shape as
/// applying a settlement and shares its retry.
/// </para>
/// <para>
/// <strong>Deciding a settlement is the exception and is not here at all.</strong> It stages onto the caller's
/// unit of work, exactly as <see cref="Facade.IAiUsageRecordingFacade.StageAttemptsAsync"/> does and for the
/// same reason: the decision has to commit with the attempt record and the ledger entry it describes, or a
/// settled attempt whose reservation was never settled becomes reachable.
/// </para>
/// <para>
/// No provider call happens inside any of these, and nothing in this type could make one.
/// </para>
/// </remarks>
internal interface IAiQuotaDataLayer
{
    /// <summary>The terms in force for an account, or null when it has none of its own.</summary>
    Task<AccountAiQuota?> FindCurrentQuotaAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>The hold already taken for this claim, or null when none was.</summary>
    Task<AccountAiQuotaReservation?> FindReservationAsync(
        Guid aiOperationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the period containing <paramref name="instant"/>, opening it if it has not been opened yet.
    /// </summary>
    /// <param name="open">
    /// Builds the period to open, given the preceding one when there is an abutting one to carry over from.
    /// Business's decision, passed in as a delegate so the terms are decided by Business and the race is
    /// handled here.
    /// </param>
    /// <remarks>
    /// Idempotent under concurrency by the unique index on <c>(AccountId, StartsAt)</c>: two workers opening
    /// the same period race, the loser catches the violation and re-reads the winner's row. That is the whole
    /// reason the index is unique rather than merely a seek.
    /// </remarks>
    Task<AccountAiQuotaPeriod> ResolvePeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, AccountAiQuotaPeriod> open,
        CancellationToken cancellationToken);

    /// <summary>
    /// The period containing <paramref name="instant"/>, or the one that <em>would</em> be opened for it —
    /// built and returned unsaved.
    /// </summary>
    /// <remarks>
    /// <see cref="ResolvePeriodAsync"/> without the insert, sharing its reads and its <paramref name="open"/>
    /// delegate so the projection cannot drift from what opening actually produces. For the refusal path, which
    /// must be able to say what an account has left without spending anything to find out (USAGE-006).
    /// </remarks>
    Task<AccountAiQuotaPeriod> ProjectPeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, AccountAiQuotaPeriod> open,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes a hold against a period, if the balance covers it. One concurrency-safe transaction.
    /// </summary>
    /// <param name="admit">
    /// Decides whether the balance covers the hold, and builds the reservation when it does. Given the period
    /// as it is at this instant; returns null to refuse. Re-invoked on every retry, against a freshly read
    /// period, because the balance it judged may have moved underneath it.
    /// </param>
    /// <returns>
    /// The outcome, the hold when one was taken, and the period as it stands after the write — which is not
    /// the instance the caller resolved earlier, because a retry re-reads.
    /// </returns>
    Task<(AiQuotaWriteOutcome Outcome, AccountAiQuotaReservation? Reservation, AccountAiQuotaPeriod? Period)>
        ReserveAsync(
            string accountId,
            DateTimeOffset instant,
            Func<AccountAiQuotaPeriod, AccountAiQuotaReservation?> admit,
            CancellationToken cancellationToken);

    /// <summary>Stages a decided settlement on the caller's unit of work. Does not save.</summary>
    Task<AiQuotaWriteOutcome> StageSettlementAsync(
        Guid reservationId,
        Func<AccountAiQuotaReservation, bool> settle,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a decided settlement to its period: gives the hold back and lands the charge.
    /// </summary>
    /// <remarks>
    /// Idempotent per column rather than per call. The hold is given back by whichever of this and
    /// <see cref="ExpireAsync"/> reaches the period first, and the charge lands exactly once — which is what
    /// makes both safe to retry, to re-deliver, and to race against each other.
    /// </remarks>
    Task<AiQuotaWriteOutcome> PostAsync(
        Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Gives back a hold whose lease lapsed with nothing settled against it.</summary>
    Task<AiQuotaWriteOutcome> ExpireAsync(
        Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Extends a hold's lifetime, so it tracks the lease the run is actually holding.</summary>
    Task<AiQuotaWriteOutcome> RenewAsync(
        Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Marks an ended period whose totals can no longer move.</summary>
    Task<AiQuotaWriteOutcome> FinalizePeriodAsync(
        Guid periodId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindLapsedReservationIdsAsync(
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindUnpostedReservationIdsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindFinalizablePeriodIdsAsync(
        DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiQuotaDataLayer"/>
internal sealed class AiQuotaDataLayer(IAiQuotaRepository quotas) : IAiQuotaDataLayer
{
    public Task<AccountAiQuota?> FindCurrentQuotaAsync(
        string accountId, CancellationToken cancellationToken) =>
        quotas.FindCurrentQuotaAsync(accountId, cancellationToken);

    public Task<AccountAiQuotaReservation?> FindReservationAsync(
        Guid aiOperationId, Guid leaseToken, CancellationToken cancellationToken) =>
        quotas.FindReservationAsync(aiOperationId, leaseToken, cancellationToken);

    public async Task<AccountAiQuotaPeriod> ProjectPeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, AccountAiQuotaPeriod> open,
        CancellationToken cancellationToken) =>
        await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken)
        ?? await BuildPeriodAsync(accountId, instant, open, cancellationToken);

    public async Task<AccountAiQuotaPeriod> ResolvePeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, AccountAiQuotaPeriod> open,
        CancellationToken cancellationToken)
    {
        var existing = await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        quotas.AddPeriod(await BuildPeriodAsync(accountId, instant, open, cancellationToken));

        try
        {
            await quotas.SaveAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another worker opened the same period between the read above and this insert. The unique index
            // on (AccountId, StartsAt) is what turned that into a loss rather than two periods splitting one
            // allowance, and the winner's row is the answer.
            quotas.Forget();

            return await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Opening an AI quota period for account '{accountId}' was refused, but no period covers "
                        + $"{instant:O}. The unique index and the period boundaries disagree.");
        }

        return await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken)
            ?? throw new InvalidOperationException(
                $"An AI quota period was opened for account '{accountId}' that does not contain {instant:O}.");
    }

    /// <summary>
    /// The period an account would open for this instant, built and not persisted.
    /// </summary>
    /// <remarks>
    /// The previous period and what it still has outstanding are read here rather than inside a lock: the
    /// carry-over can only be wrong while that period is still being spent against, and counting outstanding
    /// holds against it is what makes the number conservative in exactly that case.
    /// </remarks>
    private async Task<AccountAiQuotaPeriod> BuildPeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, AccountAiQuotaPeriod> open,
        CancellationToken cancellationToken)
    {
        var previous = await quotas.FindPeriodBeforeAsync(accountId, instant, cancellationToken);
        var outstanding = previous is null
            ? 0m
            : await quotas.SumOutstandingAsync(previous.Id, cancellationToken);

        return open(previous, outstanding);
    }

    public async Task<(AiQuotaWriteOutcome, AccountAiQuotaReservation?, AccountAiQuotaPeriod?)> ReserveAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod, AccountAiQuotaReservation?> admit,
        CancellationToken cancellationToken)
    {
        AccountAiQuotaPeriod? period = null;

        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            period = await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken);

            if (period is null)
            {
                return (AiQuotaWriteOutcome.NotFound, null, null);
            }

            var reservation = admit(period);

            if (reservation is null)
            {
                return (AiQuotaWriteOutcome.Refused, null, period);
            }

            period.Reserved += reservation.ReservedAmount;
            quotas.AddReservation(reservation);

            try
            {
                // One SaveChanges over both rows is one transaction, and the period's row version is inside
                // it: two workers that read the same remaining balance cannot both get here (USAGE-004).
                await quotas.SaveAsync(cancellationToken);

                return (AiQuotaWriteOutcome.Applied, reservation, period);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else moved the period. The balance this attempt judged is stale, so the decision is
                // made again against a fresh read rather than retried with the same answer.
                quotas.Forget();
                period = null;
            }
        }

        return (AiQuotaWriteOutcome.Contended, null, period);
    }

    /// <remarks>
    /// The one write here that does not own a transaction. It stages and the caller's save commits — see the
    /// interface's remarks, and <c>IAiUsageDataLayer</c>, which departs from backend.md the same way and for
    /// the same reason.
    /// </remarks>
    public async Task<AiQuotaWriteOutcome> StageSettlementAsync(
        Guid reservationId,
        Func<AccountAiQuotaReservation, bool> settle,
        CancellationToken cancellationToken)
    {
        var reservation = await quotas.GetReservationWithPeriodAsync(reservationId, cancellationToken);

        if (reservation is null)
        {
            return AiQuotaWriteOutcome.NotFound;
        }

        return settle(reservation) ? AiQuotaWriteOutcome.Applied : AiQuotaWriteOutcome.AlreadyDone;
    }

    public Task<AiQuotaWriteOutcome> PostAsync(
        Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        AgainstPeriodAsync(reservationId, (reservation, period) =>
        {
            if (reservation.SettledAmount is not { } charge)
            {
                // Nothing has been decided yet. Not an error: the sweep and the worker both reach for this,
                // and the worker usually wins the race to settle before the sweep notices the hold at all.
                return false;
            }

            var moved = false;

            if (reservation.ReleasedAt is null)
            {
                period.Reserved -= reservation.ReservedAmount;
                reservation.ReleasedAt = now;
                moved = true;
            }

            if (reservation.PostedAt is null)
            {
                // Permitted to push Consumed past the spendable total, and the table deliberately does not
                // constrain it: a provider can use more than the reservation estimated, and refusing to record
                // the overshoot would lose the attempt rather than the argument. The next admission refuses.
                period.Consumed += charge;
                reservation.PostedAt = now;
                moved = true;
            }

            return moved;
        }, cancellationToken);

    public Task<AiQuotaWriteOutcome> ExpireAsync(
        Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        AgainstPeriodAsync(reservationId, (reservation, period) =>
        {
            if (reservation.Status is not AiQuotaReservationStatus.Held || reservation.ReleasedAt is not null)
            {
                // Settled between the sweep's scan and this write. The settlement owns the outcome; expiring
                // it now would overwrite a decision with an absence of one.
                return false;
            }

            period.Reserved -= reservation.ReservedAmount;
            reservation.Status = AiQuotaReservationStatus.Expired;
            reservation.ReleasedAt = now;

            return true;
        }, cancellationToken);

    public async Task<AiQuotaWriteOutcome> RenewAsync(
        Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var reservation = await quotas.GetReservationWithPeriodAsync(reservationId, cancellationToken);

            if (reservation is null)
            {
                return AiQuotaWriteOutcome.NotFound;
            }

            // A hold the sweep already gave back is not extended. The run has lost its lease as well by then,
            // and the AI seam will refuse its write for the same reason.
            if (reservation.Status is not AiQuotaReservationStatus.Held)
            {
                return AiQuotaWriteOutcome.AlreadyDone;
            }

            reservation.ExpiresAt = expiresAt;

            try
            {
                await quotas.SaveAsync(cancellationToken);

                return AiQuotaWriteOutcome.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                quotas.Forget();
            }
        }

        return AiQuotaWriteOutcome.Contended;
    }

    public async Task<AiQuotaWriteOutcome> FinalizePeriodAsync(
        Guid periodId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var period = await quotas.GetPeriodAsync(periodId, cancellationToken);

            if (period is null)
            {
                return AiQuotaWriteOutcome.NotFound;
            }

            if (period.SettledAt is not null)
            {
                return AiQuotaWriteOutcome.AlreadyDone;
            }

            if (period.EndsAt > now)
            {
                // The check constraint refuses SettledAt before EndsAt, and it is right to: a period that
                // finished settling before it finished running is two facts disagreeing.
                return AiQuotaWriteOutcome.AlreadyDone;
            }

            period.SettledAt = now;

            try
            {
                await quotas.SaveAsync(cancellationToken);

                return AiQuotaWriteOutcome.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                quotas.Forget();
            }
        }

        return AiQuotaWriteOutcome.Contended;
    }

    public Task<IReadOnlyList<Guid>> FindLapsedReservationIdsAsync(
        DateTimeOffset now, CancellationToken cancellationToken) =>
        quotas.FindLapsedReservationIdsAsync(now, AiQuotaPolicy.MaintenanceBatchSize, cancellationToken);

    public Task<IReadOnlyList<Guid>> FindUnpostedReservationIdsAsync(CancellationToken cancellationToken) =>
        quotas.FindUnpostedReservationIdsAsync(AiQuotaPolicy.MaintenanceBatchSize, cancellationToken);

    public Task<IReadOnlyList<Guid>> FindFinalizablePeriodIdsAsync(
        DateTimeOffset now, CancellationToken cancellationToken) =>
        quotas.FindFinalizablePeriodIdsAsync(now, AiQuotaPolicy.MaintenanceBatchSize, cancellationToken);

    /// <summary>
    /// One retried transaction over a reservation and the period it spends from.
    /// </summary>
    /// <remarks>
    /// The shared shape of posting and expiry: both move <c>Reserved</c>, both are guarded by a stored
    /// timestamp rather than by who calls them, and both lose to the period's row version when they race — at
    /// which point re-reading shows the other one already did the work and the mutation declines to repeat it.
    /// </remarks>
    private async Task<AiQuotaWriteOutcome> AgainstPeriodAsync(
        Guid reservationId,
        Func<AccountAiQuotaReservation, AccountAiQuotaPeriod, bool> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var reservation = await quotas.GetReservationWithPeriodAsync(reservationId, cancellationToken);

            if (reservation?.Period is null)
            {
                return AiQuotaWriteOutcome.NotFound;
            }

            if (!mutate(reservation, reservation.Period))
            {
                return AiQuotaWriteOutcome.AlreadyDone;
            }

            try
            {
                await quotas.SaveAsync(cancellationToken);

                return AiQuotaWriteOutcome.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                quotas.Forget();
            }
        }

        return AiQuotaWriteOutcome.Contended;
    }
}
