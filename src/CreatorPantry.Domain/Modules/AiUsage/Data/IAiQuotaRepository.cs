using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// EF access to an account's quota terms, periods and reservations. Reads and writes no other module's tables.
/// </summary>
/// <remarks>
/// <strong>No <c>IgnoreQueryFilters()</c> anywhere in here, because there is no filter to ignore.</strong> None
/// of these entities is <see cref="IWorkspaceOwned"/>, so every query below reads across workspaces by
/// construction rather than by opting out of scoping — which is why this file is not a
/// <c>BulkOperationBoundaryTests</c> exemption and does not need to be.
/// </remarks>
internal interface IAiQuotaRepository
{
    /// <summary>The terms in force for this account right now, or null when it has none of its own.</summary>
    Task<AccountAiQuota?> FindCurrentQuotaAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>The period containing <paramref name="instant"/>, or null when none has been opened.</summary>
    Task<AccountAiQuotaPeriod?> FindPeriodAtAsync(
        string accountId, DateTimeOffset instant, CancellationToken cancellationToken);

    /// <summary>The account's most recent period starting before <paramref name="startsAt"/>.</summary>
    /// <remarks>
    /// What a roll reads to decide a carry-over. It answers "the one before this", not "the one that abuts
    /// this" — whether the two actually meet is the caller's decision, because an account idle for several
    /// periods has a previous period that is not the preceding one.
    /// </remarks>
    Task<AccountAiQuotaPeriod?> FindPeriodBeforeAsync(
        string accountId, DateTimeOffset startsAt, CancellationToken cancellationToken);

    Task<AccountAiQuotaPeriod?> GetPeriodAsync(Guid periodId, CancellationToken cancellationToken);

    /// <summary>The hold taken for this exact claim, or null when none was.</summary>
    Task<AccountAiQuotaReservation?> FindReservationAsync(
        Guid aiOperationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>The hold and the period it spends from, loaded together for a write that moves both.</summary>
    Task<AccountAiQuotaReservation?> GetReservationWithPeriodAsync(
        Guid reservationId, CancellationToken cancellationToken);

    /// <summary>What this period still has outstanding, in holds nobody has given back.</summary>
    Task<decimal> SumOutstandingAsync(Guid periodId, CancellationToken cancellationToken);

    /// <summary>Holds whose lease lapsed before anything settled them.</summary>
    Task<IReadOnlyList<Guid>> FindLapsedReservationIdsAsync(
        DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>Settlements decided but not yet applied to their period.</summary>
    Task<IReadOnlyList<Guid>> FindUnpostedReservationIdsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Ended periods whose totals may still move, with nothing outstanding left to move them.</summary>
    Task<IReadOnlyList<Guid>> FindFinalizablePeriodIdsAsync(
        DateTimeOffset now, int take, CancellationToken cancellationToken);

    void AddPeriod(AccountAiQuotaPeriod period);

    void AddReservation(AccountAiQuotaReservation reservation);

    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Forgets everything tracked, so a contended write can start its next attempt from the database.</summary>
    /// <remarks>
    /// A retry that reused the tracker would re-send the same stale row version and lose again forever, and
    /// would re-apply the increments the failed attempt had already staged.
    /// </remarks>
    void Forget();
}

/// <inheritdoc cref="IAiQuotaRepository"/>
internal sealed class AiQuotaRepository(CreatorPantryDbContext context) : IAiQuotaRepository
{
    /// <remarks>Seeks <c>UX_AccountAiQuotas_Account_Current</c>, the filtered index that is the invariant.</remarks>
    public Task<AccountAiQuota?> FindCurrentQuotaAsync(
        string accountId, CancellationToken cancellationToken) =>
        context.AccountAiQuotas
            .AsNoTracking()
            .SingleOrDefaultAsync(
                quota => quota.AccountId == accountId && quota.EffectiveTo == null, cancellationToken);

    /// <remarks>
    /// Seeks <c>UX_AccountAiQuotaPeriods_Account_StartsAt</c> with <c>EndsAt</c> as a residual. Tracked, not
    /// <c>AsNoTracking</c>: the caller that asks this question is usually about to increment the row it gets
    /// back, and re-reading it to write would be two round trips and a second chance to race.
    /// </remarks>
    public Task<AccountAiQuotaPeriod?> FindPeriodAtAsync(
        string accountId, DateTimeOffset instant, CancellationToken cancellationToken) =>
        context.AccountAiQuotaPeriods
            .SingleOrDefaultAsync(
                period => period.AccountId == accountId
                    && period.StartsAt <= instant
                    && period.EndsAt > instant,
                cancellationToken);

    public Task<AccountAiQuotaPeriod?> FindPeriodBeforeAsync(
        string accountId, DateTimeOffset startsAt, CancellationToken cancellationToken) =>
        context.AccountAiQuotaPeriods
            .AsNoTracking()
            .Where(period => period.AccountId == accountId && period.StartsAt < startsAt)
            .OrderByDescending(period => period.StartsAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AccountAiQuotaPeriod?> GetPeriodAsync(Guid periodId, CancellationToken cancellationToken) =>
        context.AccountAiQuotaPeriods.SingleOrDefaultAsync(
            period => period.Id == periodId, cancellationToken);

    /// <remarks>Seeks <c>UX_AccountAiQuotaReservations_Operation_Lease</c>.</remarks>
    public Task<AccountAiQuotaReservation?> FindReservationAsync(
        Guid aiOperationId, Guid leaseToken, CancellationToken cancellationToken) =>
        context.AccountAiQuotaReservations.SingleOrDefaultAsync(
            reservation => reservation.AiOperationId == aiOperationId
                && reservation.LeaseToken == leaseToken,
            cancellationToken);

    public Task<AccountAiQuotaReservation?> GetReservationWithPeriodAsync(
        Guid reservationId, CancellationToken cancellationToken) =>
        context.AccountAiQuotaReservations
            .Include(reservation => reservation.Period)
            .SingleOrDefaultAsync(reservation => reservation.Id == reservationId, cancellationToken);

    /// <remarks>
    /// <c>ReleasedAt IS NULL</c> is the definition of outstanding, and it is the same predicate
    /// <c>AccountAiQuotaPeriod.Reserved</c> is documented to equal the sum over — so this is the number that
    /// column is, recomputed, rather than a second opinion about it.
    /// </remarks>
    public async Task<decimal> SumOutstandingAsync(Guid periodId, CancellationToken cancellationToken) =>
        await context.AccountAiQuotaReservations
            .Where(reservation => reservation.PeriodId == periodId && reservation.ReleasedAt == null)
            .SumAsync(reservation => reservation.ReservedAmount, cancellationToken);

    /// <remarks>
    /// Seeks <c>IX_AccountAiQuotaReservations_Outstanding</c>, and projects to identifiers only: the sweep is
    /// looking for work before it knows whose it is, and it settles each one in its own transaction.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> FindLapsedReservationIdsAsync(
        DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await context.AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.ReleasedAt == null
                && reservation.Status == AiQuotaReservationStatus.Held
                && reservation.ExpiresAt < now)
            .OrderBy(reservation => reservation.ExpiresAt)
            .Take(take)
            .Select(reservation => reservation.Id)
            .ToListAsync(cancellationToken);

    /// <remarks>Seeks <c>IX_AccountAiQuotaReservations_Unposted</c>. Identifiers only, as above.</remarks>
    public async Task<IReadOnlyList<Guid>> FindUnpostedReservationIdsAsync(
        int take, CancellationToken cancellationToken) =>
        await context.AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.PostedAt == null && reservation.SettledAt != null)
            .OrderBy(reservation => reservation.SettledAt)
            .Take(take)
            .Select(reservation => reservation.Id)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// <para>
    /// Seeks <c>IX_AccountAiQuotaPeriods_Unsettled</c>. A period is finalizable once it has ended and holds
    /// nothing that could still move its totals — which is the condition
    /// <c>AccountAiQuotaPeriod.SettledAt</c> exists to record, and why it is not simply "the clock passed
    /// <c>EndsAt</c>".
    /// </para>
    /// <para>
    /// Two things can still move them: a hold that has not been decided, and a decision that has not been
    /// applied. An <em>expired</em> hold is neither. It looks like it might be — a worker could in principle
    /// settle after its hold lapsed — but the AI seam already refuses that: a lease and its reservation lapse
    /// for the same reason at the same instant, and <c>StoreProposalAsync</c> writes nothing at all once the
    /// lease is lost, so no settlement is ever staged for it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> FindFinalizablePeriodIdsAsync(
        DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await context.AccountAiQuotaPeriods
            .AsNoTracking()
            .Where(period => period.SettledAt == null
                && period.EndsAt <= now
                && !context.AccountAiQuotaReservations.Any(reservation =>
                    reservation.PeriodId == period.Id
                    && (reservation.Status == AiQuotaReservationStatus.Held
                        || (reservation.SettledAt != null && reservation.PostedAt == null))))
            .OrderBy(period => period.EndsAt)
            .Take(take)
            .Select(period => period.Id)
            .ToListAsync(cancellationToken);

    public void AddPeriod(AccountAiQuotaPeriod period) => context.AccountAiQuotaPeriods.Add(period);

    public void AddReservation(AccountAiQuotaReservation reservation) =>
        context.AccountAiQuotaReservations.Add(reservation);

    public Task SaveAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);

    public void Forget() => context.ChangeTracker.Clear();
}
