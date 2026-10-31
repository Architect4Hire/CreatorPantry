using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>Credits charged by one capability in a period, with the attempts behind them.</summary>
internal sealed record AiUsageTaskTotal(AiTaskType TaskType, decimal Amount, int Requests);

/// <summary>Credits charged by work done in one workspace, with the attempts behind them.</summary>
internal sealed record AiUsageWorkspaceTotal(Guid? WorkspaceId, decimal Amount, int Requests);

/// <summary>
/// The account's own view of its usage: period history and per-period breakdowns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every query here is keyed by account and nothing else.</strong> There is no workspace filter to
/// apply and none to ignore — these tables are platform-scoped by design (USAGE-002) — so the account
/// predicate is the whole of the scoping, and it comes from the validated session rather than from any part of
/// the request.
/// </para>
/// <para>
/// Amounts come from settled reservations and counts from the ledger, which is why the two are read
/// separately: the reservation knows what a run was charged, and the ledger knows how many provider calls it
/// took to get there.
/// </para>
/// </remarks>
internal interface IAiUsageReadRepository
{
    /// <summary>The account's periods that have ended, newest first.</summary>
    Task<IReadOnlyList<AccountAiQuotaPeriod>> FindClosedPeriodsAsync(
        string accountId, DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>What each capability charged in this period, and how many attempts it made.</summary>
    Task<IReadOnlyList<AiUsageTaskTotal>> SumByTaskAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>What work in each workspace charged in this period, and how many attempts it made.</summary>
    Task<IReadOnlyList<AiUsageWorkspaceTotal>> SumByWorkspaceAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReadRepository"/>
internal sealed class AiUsageReadRepository(CreatorPantryDbContext context) : IAiUsageReadRepository
{
    /// <remarks>
    /// <para>
    /// Seeks <c>UX_AccountAiQuotaPeriods_Account_StartsAt</c> backwards.
    /// </para>
    /// <para>
    /// <strong>The running period is excluded here rather than afterwards</strong>, and that is not tidiness:
    /// filtering it out in memory would let it occupy one of the <paramref name="take"/> rows, so a caller
    /// asking for twelve would get eleven whenever a period was open — with no cursor and no total, they
    /// could not tell that from having reached the end.
    /// </para>
    /// <para>
    /// Ended, not <c>SettledAt</c>: a period stops being history when it stops running, not when its last
    /// reservation settles. A creator looking at last month should not have to wait for a sweep.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AccountAiQuotaPeriod>> FindClosedPeriodsAsync(
        string accountId, DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await context.AccountAiQuotaPeriods
            .AsNoTracking()
            .Where(period => period.AccountId == accountId && period.EndsAt <= now)
            .OrderByDescending(period => period.StartsAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// Amounts from the reservations, which carry the task type themselves; counts from the ledger, which is
    /// the only table that knows how many provider calls a run made. Joined in memory on the task type rather
    /// than in SQL, because both sides are at most one row per capability.
    /// </remarks>
    public async Task<IReadOnlyList<AiUsageTaskTotal>> SumByTaskAsync(
        string accountId,
        Guid periodId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var amounts = await context.AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.AccountId == accountId
                && reservation.PeriodId == periodId
                && reservation.PostedAt != null)
            .GroupBy(reservation => reservation.TaskType)
            .Select(group => new
            {
                TaskType = group.Key,
                Amount = group.Sum(reservation => reservation.SettledAmount ?? 0m),
            })
            .ToListAsync(cancellationToken);

        var requests = await context.AccountAiUsageEntries
            .AsNoTracking()
            .Where(entry => entry.AccountId == accountId
                && entry.OccurredAt >= from
                && entry.OccurredAt < to)
            .GroupBy(entry => entry.TaskType)
            .Select(group => new { TaskType = group.Key, Requests = group.Count() })
            .ToListAsync(cancellationToken);

        return Merge(
            amounts.Select(row => (Key: (AiTaskType?)row.TaskType, row.Amount)),
            requests.Select(row => (Key: (AiTaskType?)row.TaskType, row.Requests)),
            (key, amount, count) => new AiUsageTaskTotal(key!.Value, amount, count));
    }

    /// <remarks>
    /// <para>
    /// A reservation carries no workspace — an allowance belongs to an account, and the quota tables have no
    /// workspace dimension at all. The ledger does, so a run's workspace is read from the entries its operation
    /// posted; one run is one operation is one workspace, so the mapping is unambiguous.
    /// </para>
    /// <para>
    /// <strong>Either operation column, because a reservation has one and the ledger has two.</strong> An image
    /// hold stores its generation's id in <c>AiOperationId</c>, but its charges name it in
    /// <c>GeneratedImageOperationId</c> and leave <c>AiOperationId</c> null (AP.2.5). Matching the one column
    /// left every picture's spend with no workspace. The two id spaces are both fresh Guids, so a match on either
    /// is still exactly one operation.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AiUsageWorkspaceTotal>> SumByWorkspaceAsync(
        string accountId,
        Guid periodId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var amounts = await context.AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.AccountId == accountId
                && reservation.PeriodId == periodId
                && reservation.PostedAt != null)
            .Select(reservation => new
            {
                Amount = reservation.SettledAmount ?? 0m,

                // Null when the run posted no ledger entry, which a settled charge always does. Carried rather
                // than dropped so the breakdown still sums to what the period's runs cost even if it ever happens —
                // its Consumed, plus whatever pack credits paid for (AP.5.2), since a run's cost is what it cost
                // whichever source paid.
                //
                // The account predicate is repeated here rather than inferred from the outer reservation: it
                // is what scopes every other query in this file, and the one place it was left implicit would
                // be the one place a reader has to reconstruct the argument. It costs nothing and it means no
                // row in this subquery can belong to another account.
                WorkspaceId = context.AccountAiUsageEntries
                    .Where(entry => (entry.AiOperationId == reservation.AiOperationId
                            || entry.GeneratedImageOperationId == reservation.AiOperationId)
                        && entry.AccountId == accountId)
                    .Select(entry => (Guid?)entry.WorkspaceId)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var requests = await context.AccountAiUsageEntries
            .AsNoTracking()
            .Where(entry => entry.AccountId == accountId
                && entry.OccurredAt >= from
                && entry.OccurredAt < to)
            .GroupBy(entry => entry.WorkspaceId)
            .Select(group => new { WorkspaceId = group.Key, Requests = group.Count() })
            .ToListAsync(cancellationToken);

        return Merge(
            amounts.GroupBy(row => row.WorkspaceId)
                .Select(group => (Key: group.Key, Amount: group.Sum(row => row.Amount))),
            requests.Select(row => (Key: (Guid?)row.WorkspaceId, row.Requests)),
            (key, amount, count) => new AiUsageWorkspaceTotal(key, amount, count));
    }

    /// <summary>
    /// Full-outer-joins the amounts and the counts, so a key present in only one still appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are genuinely reachable and both matter. A capability with a charge and no attempts cannot
    /// happen; a capability with attempts and no charge is every run made before allowances existed, and
    /// dropping those would silently under-report a creator's own history.
    /// </para>
    /// <para>
    /// <strong>Lookups, not a dictionary, because a key may be null.</strong> The workspace breakdown carries a
    /// charge whose workspace could not be read as a null key on purpose, and a dictionary throws on one — which
    /// turned a missing label into a failed page.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<TResult> Merge<TKey, TResult>(
        IEnumerable<(TKey Key, decimal Amount)> amounts,
        IEnumerable<(TKey Key, int Requests)> requests,
        Func<TKey, decimal, int, TResult> build)
    {
        var amountsByKey = amounts.ToLookup(row => row.Key, row => row.Amount);
        var requestsByKey = requests.ToLookup(row => row.Key, row => row.Requests);

        return
        [
            .. amountsByKey.Select(group => group.Key)
                .Union(requestsByKey.Select(group => group.Key))
                .Select(key => build(key, amountsByKey[key].Sum(), requestsByKey[key].Sum())),
        ];
    }
}
