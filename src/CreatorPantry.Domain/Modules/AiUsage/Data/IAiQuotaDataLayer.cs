using CreatorPantry.Domain.Managers.Persistence;
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

    /// <summary>
    /// The account could afford it, but the platform's image spend for the day could not (AP.2.7). Nothing was
    /// written: neither the hold nor the charge.
    /// </summary>
    OverPlatformCeiling = 6,
}

/// <summary>
/// What a picture adds to the platform's daily image spend, and the line it may not pass (AP.2.7).
/// </summary>
/// <param name="Day">The UTC day of the admission instant.</param>
/// <param name="Micros">The request's list price, in millionths of a dollar. Above nought.</param>
/// <param name="CeilingMicros">The configured ceiling, in the same unit.</param>
/// <param name="AiOperationId">The operation the hold is for, with <paramref name="LeaseToken"/> its identity.</param>
/// <param name="LeaseToken">
/// Together with <paramref name="AiOperationId"/>, how a re-run of the charge's transaction recognises a hold an
/// earlier run already committed: an execution strategy re-runs the unit when a fault arrives after the commit
/// has landed, and charging again then would count one request twice and fail the hold's unique index.
/// </param>
internal sealed record PlatformImageSpendCharge(
    DateOnly Day, long Micros, long CeilingMicros, Guid AiOperationId, Guid LeaseToken);

/// <summary>Which of the account's picture counts a picture admission is judged against (AP.4.2, AP.7.1).</summary>
/// <param name="DayStart">The start of the UTC day whose pictures count against the plan's daily cap.</param>
/// <param name="InAll">
/// Whether to count every picture the account has ever asked for, for a trial's lifetime cap (B-52). False on a paid
/// plan, which has none, so a paid admission does not pay for a count nothing reads.
/// </param>
internal sealed record AccountImageWindows(DateTimeOffset DayStart, bool InAll);

/// <summary>The account's pictures that still count, as <see cref="IAiQuotaRepository.CountImagesSinceAsync"/> counts them.</summary>
/// <param name="Today">Since the start of the UTC day.</param>
/// <param name="InAll">Ever, across every workspace; nought unless <see cref="AccountImageWindows.InAll"/> asked for it.</param>
internal readonly record struct AccountImageCounts(int Today, int InAll)
{
    /// <summary>What a completion, which asks for no pictures, is told.</summary>
    public static readonly AccountImageCounts None = new(0, 0);
}

/// <summary>
/// Stages the end of a hold's draws on the account's grants: <paramref name="spentFromGrants"/> of what it took is
/// kept and the rest goes back where it came from (AP.5.2). Business's, through Billing's facade; called by the data
/// layer inside the save that releases the hold, so the two commit together.
/// </summary>
internal delegate Task ResolveGrants(
    AccountAiQuotaReservation reservation, decimal spentFromGrants, CancellationToken cancellationToken);

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
    /// <summary>One hold by its own id, for a caller asking whether it may still spend against it.</summary>
    Task<AccountAiQuotaReservation?> FindReservationByIdAsync(
        Guid reservationId, CancellationToken cancellationToken);

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
        Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
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
        Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes a hold against a period, if the balance covers it. One concurrency-safe transaction.
    /// </summary>
    /// <param name="admit">
    /// Decides whether the balance covers the hold, and builds the reservation when it does. Given the period
    /// as it is at this instant; returns null to refuse. Re-invoked on every retry, against a freshly read
    /// period, because the balance it judged may have moved underneath it. What it takes from the account's grants
    /// it stages on this unit of work through Billing's facade (AP.5.2), so it commits with the hold or not at all,
    /// and a retry forgets it with everything else.
    /// </param>
    /// <param name="platform">
    /// For a picture while a ceiling is configured: what it adds to the platform's day and the line that may
    /// not be passed. The charge and the hold then commit in one transaction or not at all (AP.2.7). Null for a
    /// completion, and for a picture on a host with no ceiling.
    /// </param>
    /// <param name="images">
    /// For a picture: which of the account's pictures <paramref name="admit"/> is told it has already asked for —
    /// the UTC day's (AP.4.2), and every one ever for a trial's lifetime cap (AP.7.1). Counted after the period is
    /// read on every attempt, so an admission that commits in between moves the period's row version and this one
    /// is judged again; that is what keeps two requests from both taking a trial's last picture. Null for a
    /// completion, which is told nought.
    /// </param>
    /// <returns>
    /// The outcome, the hold when one was taken, and the period as it stands after the write — which is not
    /// the instance the caller resolved earlier, because a retry re-reads.
    /// </returns>
    /// <remarks>
    /// The account's own balance is judged first, so an account that cannot afford the request is refused as
    /// <see cref="AiQuotaWriteOutcome.Refused"/> without ever touching the platform row: its refusal is about
    /// its own allowance, and it must not count toward, or wait on, everybody else's.
    /// </remarks>
    Task<(AiQuotaWriteOutcome Outcome, AccountAiQuotaReservation? Reservation, AccountAiQuotaPeriod? Period)>
        ReserveAsync(
            string accountId,
            DateTimeOffset instant,
            Func<AccountAiQuotaPeriod, AccountImageCounts, CancellationToken, Task<AccountAiQuotaReservation?>> admit,
            PlatformImageSpendCharge? platform,
            AccountImageWindows? images,
            CancellationToken cancellationToken);

    /// <summary>
    /// Raises a period's grant for an upgrade (AP.4.2), and returns the period as it stands afterwards; null when
    /// it no longer exists. One concurrency-safe write.
    /// </summary>
    /// <param name="raise">
    /// Applies the top-up to the period as it is at this instant, or returns false when it already has as much.
    /// Re-invoked against a fresh read on every retry, so two admissions racing to apply one upgrade apply it once.
    /// </param>
    Task<AccountAiQuotaPeriod?> RaisePeriodAsync(
        Guid periodId, Func<AccountAiQuotaPeriod, bool> raise, CancellationToken cancellationToken);

    /// <summary>Whether the account has ever had a period opened under this plan (B-48).</summary>
    Task<bool> HasPeriodUnderPlanAsync(string accountId, string planKey, CancellationToken cancellationToken);

    /// <summary>
    /// Gives back a picture's hold whose operation was never queued, and what it added to the platform's day.
    /// </summary>
    /// <remarks>
    /// <see cref="ExpireAsync"/>, then the platform give-back — only when the expiry was this call's, so a
    /// release run twice returns the charge once. Two statements, not one transaction: a give-back that is lost
    /// leaves the day's total high, which a breaker can afford and an account's allowance could not.
    /// </remarks>
    Task<AiQuotaWriteOutcome> ReleaseImageAsync(
        Guid reservationId, DateTimeOffset now, ResolveGrants resolveGrants, CancellationToken cancellationToken);

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
        Guid reservationId, DateTimeOffset now, ResolveGrants resolveGrants, CancellationToken cancellationToken);

    /// <summary>Gives back a hold whose lease lapsed with nothing settled against it.</summary>
    Task<AiQuotaWriteOutcome> ExpireAsync(
        Guid reservationId, DateTimeOffset now, ResolveGrants resolveGrants, CancellationToken cancellationToken);

    /// <summary>Extends a hold's lifetime, so it tracks the lease the run is actually holding.</summary>
    Task<AiQuotaWriteOutcome> RenewAsync(
        Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Marks an ended period whose totals can no longer move.</summary>
    Task<AiQuotaWriteOutcome> FinalizePeriodAsync(
        Guid periodId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records that this period's admissions have left the account low on spendable credits, once (B-59).
    /// </summary>
    /// <remarks>
    /// <see cref="AiQuotaWriteOutcome.AlreadyDone"/> for a period already marked, which is the ordinary answer
    /// once a creator has been told and keeps working. Not a hold and not part of one: the caller runs this
    /// after its own write has committed, and treats every outcome, including a throw, as "not this time".
    /// </remarks>
    Task<AiQuotaWriteOutcome> MarkLowCreditNoticedAsync(
        Guid periodId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindLapsedReservationIdsAsync(
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindUnpostedReservationIdsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindFinalizablePeriodIdsAsync(
        DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiQuotaDataLayer"/>
internal sealed class AiQuotaDataLayer(
    IAiQuotaRepository quotas,
    IPlatformImageSpendRepository spend,
    CreatorPantryDbContext context) : IAiQuotaDataLayer
{
    public Task<AccountAiQuota?> FindCurrentQuotaAsync(
        string accountId, CancellationToken cancellationToken) =>
        quotas.FindCurrentQuotaAsync(accountId, cancellationToken);

    public Task<AccountAiQuotaReservation?> FindReservationByIdAsync(
        Guid reservationId, CancellationToken cancellationToken) =>
        quotas.GetReservationWithPeriodAsync(reservationId, cancellationToken);

    public Task<AccountAiQuotaReservation?> FindReservationAsync(
        Guid aiOperationId, Guid leaseToken, CancellationToken cancellationToken) =>
        quotas.FindReservationAsync(aiOperationId, leaseToken, cancellationToken);

    public async Task<AccountAiQuotaPeriod> ProjectPeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
        CancellationToken cancellationToken) =>
        await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken)
        ?? await BuildPeriodAsync(accountId, instant, open, cancellationToken);

    public async Task<AccountAiQuotaPeriod> ResolvePeriodAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
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
        Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
        CancellationToken cancellationToken)
    {
        var previous = await quotas.FindPeriodBeforeAsync(accountId, instant, cancellationToken);
        var outstanding = previous is null
            ? 0m
            : await quotas.SumOutstandingAsync(previous.Id, cancellationToken);

        return await open(previous, outstanding, cancellationToken);
    }

    public async Task<(AiQuotaWriteOutcome, AccountAiQuotaReservation?, AccountAiQuotaPeriod?)> ReserveAsync(
        string accountId,
        DateTimeOffset instant,
        Func<AccountAiQuotaPeriod, AccountImageCounts, CancellationToken, Task<AccountAiQuotaReservation?>> admit,
        PlatformImageSpendCharge? platform,
        AccountImageWindows? images,
        CancellationToken cancellationToken)
    {
        if (platform is not null)
        {
            return await ReserveAgainstPlatformAsync(
                accountId, instant, admit, platform, images, cancellationToken);
        }

        AccountAiQuotaPeriod? period = null;

        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            period = await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken);

            if (period is null)
            {
                return (AiQuotaWriteOutcome.NotFound, null, null);
            }

            var reservation = await admit(
                period, await CountImagesAsync(accountId, images, cancellationToken), cancellationToken);

            if (reservation is null)
            {
                return (AiQuotaWriteOutcome.Refused, null, period);
            }

            Hold(period, reservation);
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

    /// <summary>
    /// <see cref="ReserveAsync"/> for a picture under a ceiling: the account's hold and the platform's charge in
    /// one explicit transaction per attempt (AP.2.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Order inside the transaction is the account, then the platform, then the save.</strong> The
    /// account's balance is judged in memory against a fresh read; only a request it covers reaches the
    /// platform's conditional <c>UPDATE</c>, whose row lock then holds every other picture admission until this
    /// one commits or rolls back. Rolling back after losing the period's row version takes the charge with it,
    /// so a retried attempt is charged once — the counter and the holds can never disagree.
    /// </para>
    /// <para>
    /// <strong>Inside an execution strategy</strong>, because a retrying strategy refuses a user-opened
    /// transaction it does not own, and re-runs the whole unit on a transient fault — which is why each run
    /// starts from a clean tracker and re-reads the period rather than reusing one.
    /// </para>
    /// <para>
    /// Lock order is platform row, then period. Nothing takes them the other way round: posting and expiry
    /// touch only the period, and the release give-back runs after its expiry has committed.
    /// </para>
    /// </remarks>
    private async Task<(AiQuotaWriteOutcome, AccountAiQuotaReservation?, AccountAiQuotaPeriod?)>
        ReserveAgainstPlatformAsync(
            string accountId,
            DateTimeOffset instant,
            Func<AccountAiQuotaPeriod, AccountImageCounts, CancellationToken, Task<AccountAiQuotaReservation?>> admit,
            PlatformImageSpendCharge platform,
            AccountImageWindows? images,
            CancellationToken cancellationToken)
    {
        // Its own commit, before the transaction: the charge is an UPDATE, and an update of a row that does not
        // exist would read as "over the ceiling" on the first picture of every day.
        await spend.EnsureAsync(platform.Day, cancellationToken);

        AccountAiQuotaPeriod? last = null;

        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var runs = 0;

            var (outcome, reservation, period, settled) = await context.Database.CreateExecutionStrategy()
                .ExecuteAsync(async () =>
                {
                    quotas.Forget();

                    // A second run of this unit means the strategy saw a transient fault — possibly one that
                    // arrived after the commit had already landed. Then the hold and its charge are both in the
                    // database, and the answer is that hold: judging again would charge the day twice for one
                    // request and lose to the unique index on the hold.
                    if (++runs > 1
                        && await quotas.FindReservationAsync(platform.AiOperationId, platform.LeaseToken, cancellationToken)
                            is { } committed)
                    {
                        return (AiQuotaWriteOutcome.Applied, (AccountAiQuotaReservation?)committed,
                            await quotas.GetPeriodAsync(committed.PeriodId, cancellationToken), true);
                    }

                    await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                    var current = await quotas.FindPeriodAtAsync(accountId, instant, cancellationToken);

                    if (current is null)
                    {
                        return (AiQuotaWriteOutcome.NotFound, (AccountAiQuotaReservation?)null, current, true);
                    }

                    var hold = await admit(
                        current, await CountImagesAsync(accountId, images, cancellationToken), cancellationToken);

                    if (hold is null)
                    {
                        return (AiQuotaWriteOutcome.Refused, null, current, true);
                    }

                    if (!await spend.TryChargeAsync(
                            platform.Day, platform.Micros, platform.CeilingMicros, cancellationToken))
                    {
                        await transaction.RollbackAsync(cancellationToken);

                        // The hold was judged affordable before the ceiling refused it, so admit has already staged
                        // what it takes from grants (AP.5.2). Forgotten here, so no later save in this scope can
                        // commit a debit for a picture that was never admitted.
                        quotas.Forget();

                        return (AiQuotaWriteOutcome.OverPlatformCeiling, null, current, true);
                    }

                    hold.PlatformImageSpendMicros = platform.Micros;
                    Hold(current, hold);
                    quotas.AddReservation(hold);

                    try
                    {
                        await quotas.SaveAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);

                        return (AiQuotaWriteOutcome.Applied, hold, current, true);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // Another admission for this account moved the period. The rollback takes this
                        // attempt's platform charge back with it, and the next attempt judges both afresh.
                        await transaction.RollbackAsync(cancellationToken);
                        quotas.Forget();

                        return (AiQuotaWriteOutcome.Contended, null, current, false);
                    }
                });

            if (settled)
            {
                return (outcome, reservation, period);
            }

            last = period;
        }

        return (AiQuotaWriteOutcome.Contended, null, last);
    }

    /// <summary>The account's pictures today, and in all when asked for; nought for a completion.</summary>
    /// <remarks>
    /// Read after the period on every attempt, never before it. An admission that commits between the two reads
    /// is counted here; one that commits after both moves the period's row version, and this attempt's save loses
    /// and judges again. Read the other way round, a commit between the reads would be missed by both.
    /// </remarks>
    private async Task<AccountImageCounts> CountImagesAsync(
        string accountId, AccountImageWindows? images, CancellationToken cancellationToken) =>
        images is null
            ? AccountImageCounts.None
            : new AccountImageCounts(
                await quotas.CountImagesSinceAsync(accountId, images.DayStart, cancellationToken),
                images.InAll ? await quotas.CountImagesSinceAsync(accountId, null, cancellationToken) : 0);

    public async Task<AccountAiQuotaPeriod?> RaisePeriodAsync(
        Guid periodId, Func<AccountAiQuotaPeriod, bool> raise, CancellationToken cancellationToken)
    {
        AccountAiQuotaPeriod? period = null;

        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            period = await quotas.GetPeriodAsync(periodId, cancellationToken);

            if (period is null || !raise(period))
            {
                return period;
            }

            try
            {
                await quotas.SaveAsync(cancellationToken);

                return period;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another admission moved the period, possibly by applying this same upgrade. The next read
                // shows whether there is still anything to add.
                quotas.Forget();
            }
        }

        // Contended to the end. The period is returned as last read, without the top-up: admission then judges
        // against what was there before the upgrade, which can refuse a request the upgrade would have covered
        // but can never admit one the account cannot pay for. The next request applies it.
        return period is null ? null : await quotas.GetPeriodAsync(periodId, cancellationToken);
    }

    public Task<bool> HasPeriodUnderPlanAsync(
        string accountId, string planKey, CancellationToken cancellationToken) =>
        quotas.HasPeriodUnderPlanAsync(accountId, planKey, cancellationToken);

    public async Task<AiQuotaWriteOutcome> ReleaseImageAsync(
        Guid reservationId, DateTimeOffset now, ResolveGrants resolveGrants, CancellationToken cancellationToken)
    {
        var outcome = await ExpireAsync(reservationId, now, resolveGrants, cancellationToken);

        if (outcome is AiQuotaWriteOutcome.Applied
            && await quotas.GetReservationWithPeriodAsync(reservationId, cancellationToken)
                is { PlatformImageSpendMicros: { } micros } released)
        {
            // To the day it was counted on, not today's: a request that failed across midnight added to
            // yesterday's total, and taking it from today's would let today pass its ceiling by that much.
            await spend.ReturnAsync(PlatformImageSpendPolicy.DayOf(released.HeldAt), micros, cancellationToken);
        }

        return outcome;
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
        Guid reservationId, DateTimeOffset now, ResolveGrants resolveGrants, CancellationToken cancellationToken) =>
        AgainstPeriodAsync(reservationId, async (reservation, period, token) =>
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
                Release(period, reservation);
                reservation.ReleasedAt = now;
                moved = true;
            }

            if (reservation.PostedAt is null)
            {
                // The allowance keeps first and the grants next, each up to what it gave (AP.5.2). What lands on
                // the allowance is permitted to push Consumed past the spendable total, and the table deliberately
                // does not constrain it: a provider can use more than the reservation estimated, and refusing to
                // record the overshoot would lose the attempt rather than the argument. The next admission refuses
                // — or spends grants, which the overshoot itself never does.
                var (toAllowance, fromGrants) = AiQuotaSourceSplit.Charge(
                    charge, reservation.ReservedFromAllowance, reservation.ReservedAmount);

                period.Consumed += toAllowance;
                reservation.ChargedToAllowance = toAllowance;
                reservation.PostedAt = now;
                moved = true;

                if (reservation.ReservedFromGrants > 0m)
                {
                    await resolveGrants(reservation, fromGrants, token);
                }
            }

            return moved;
        }, cancellationToken);

    public Task<AiQuotaWriteOutcome> ExpireAsync(
        Guid reservationId, DateTimeOffset now, ResolveGrants resolveGrants, CancellationToken cancellationToken) =>
        AgainstPeriodAsync(reservationId, async (reservation, period, token) =>
        {
            if (reservation.Status is not AiQuotaReservationStatus.Held || reservation.ReleasedAt is not null)
            {
                // Settled between the sweep's scan and this write. The settlement owns the outcome; expiring
                // it now would overwrite a decision with an absence of one.
                return false;
            }

            Release(period, reservation);
            reservation.Status = AiQuotaReservationStatus.Expired;
            reservation.ReleasedAt = now;

            // Nothing was learned about what it cost, so every grant it drew on gets the whole draw back — the
            // allowance's part is given back above on the same reading (AP.5.2).
            if (reservation.ReservedFromGrants > 0m)
            {
                await resolveGrants(reservation, 0m, token);
            }

            return true;
        }, cancellationToken);

    /// <summary>A hold taken against a period: its allowance part on <c>Reserved</c>, its grants' part on <c>GrantReserved</c>.</summary>
    /// <remarks>
    /// Every hold is above nought, so this always moves the period, and its row version decides any race with another
    /// hold for the account — including one paid wholly from grants.
    /// </remarks>
    private static void Hold(AccountAiQuotaPeriod period, AccountAiQuotaReservation reservation)
    {
        period.Reserved += reservation.ReservedFromAllowance;
        period.GrantReserved += reservation.ReservedFromGrants;
    }

    /// <summary>The reverse of <see cref="Hold"/>: exactly what the hold added, from the columns it added it to.</summary>
    private static void Release(AccountAiQuotaPeriod period, AccountAiQuotaReservation reservation)
    {
        period.Reserved -= reservation.ReservedFromAllowance;
        period.GrantReserved -= reservation.ReservedFromGrants;
    }

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

    public async Task<AiQuotaWriteOutcome> MarkLowCreditNoticedAsync(
        Guid periodId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var period = await quotas.GetPeriodAsync(periodId, cancellationToken);

            if (period is null)
            {
                return AiQuotaWriteOutcome.NotFound;
            }

            if (period.LowCreditNoticedAt is not null)
            {
                // Two admissions crossing the line together: the loser of the row version re-reads, finds the
                // column set, and declines. One notice, which is what B-59 asks for.
                return AiQuotaWriteOutcome.AlreadyDone;
            }

            period.LowCreditNoticedAt = now;

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
    /// What a mutation stages on the account's grants rides in the same save, so the grants get their credits back
    /// with the release or not at all, and a lost race forgets them with everything else (AP.5.2).
    /// </remarks>
    private async Task<AiQuotaWriteOutcome> AgainstPeriodAsync(
        Guid reservationId,
        Func<AccountAiQuotaReservation, AccountAiQuotaPeriod, CancellationToken, Task<bool>> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var reservation = await quotas.GetReservationWithPeriodAsync(reservationId, cancellationToken);

            if (reservation?.Period is null)
            {
                return AiQuotaWriteOutcome.NotFound;
            }

            if (!await mutate(reservation, reservation.Period, cancellationToken))
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
