using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>One account's charged spend over a window.</summary>
internal sealed record AiUsageConsumerTotal(string AccountId, AiQuotaUnit Unit, decimal Amount, int Runs);

/// <summary>
/// EF access for the platform administration seam: writing an account's quota terms, and reading spend across
/// accounts.
/// </summary>
/// <remarks>
/// <strong>No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore.</strong> None of these
/// entities is <see cref="IWorkspaceOwned"/> (USAGE-002), so reading across accounts here is the tables'
/// ordinary shape rather than an opt-out of scoping — which is why this file needs no
/// <c>BulkOperationBoundaryTests</c> exemption. What keeps it safe is the same thing that makes the ledger
/// safe: these rows hold amounts and identifiers and no creator content.
/// </remarks>
internal interface IAiUsageAdministrationRepository
{
    /// <summary>The account's open terms row, tracked so the caller can close it.</summary>
    Task<AccountAiQuota?> FindCurrentQuotaForUpdateAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Whether the account has terms of its own, without tracking a row a read will not write.</summary>
    Task<bool> HasCurrentQuotaAsync(string accountId, CancellationToken cancellationToken);

    void AddQuota(AccountAiQuota quota);

    /// <summary>Forgets everything tracked, so a rejected write cannot ride along on a later save.</summary>
    void Forget();

    /// <summary>
    /// The accounts that charged the most between <paramref name="from"/> (inclusive) and <paramref name="to"/>
    /// (exclusive), largest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Posted reservations, not ledger tokens.</strong> An allowance is denominated in credits and the
    /// ledger counts provider tokens; summing the wrong one would give an operator a number that does not
    /// match what <c>/api/v1/me/ai-usage</c> shows the same creator. This is the identical source
    /// <c>AiUsageReadRepository.SumByTaskAsync</c> uses.
    /// </para>
    /// <para>
    /// Windowed on <c>PostedAt</c> rather than on the period, so an operator can ask about any span they like
    /// without every account's periods having to line up with it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<AiUsageConsumerTotal>> FindTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int take, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageAdministrationRepository"/>
internal sealed class AiUsageAdministrationRepository(CreatorPantryDbContext context)
    : IAiUsageAdministrationRepository
{
    /// <remarks>
    /// Tracked, unlike <c>AiQuotaRepository.FindCurrentQuotaAsync</c>: this caller is about to stamp
    /// <c>EffectiveTo</c> on the row it gets back, and re-reading it to write would be a second chance to race.
    /// Seeks <c>UX_AccountAiQuotas_Account_Current</c>.
    /// </remarks>
    public Task<AccountAiQuota?> FindCurrentQuotaForUpdateAsync(
        string accountId, CancellationToken cancellationToken) =>
        context.AccountAiQuotas.SingleOrDefaultAsync(
            quota => quota.AccountId == accountId && quota.EffectiveTo == null, cancellationToken);

    /// <remarks>
    /// The read path's question: an administrator needs to know whether terms are the account's own or the
    /// platform default, and nothing else about the row. Tracking one for a GET would attach a mutable entity
    /// to a request that will never write it.
    /// </remarks>
    public Task<bool> HasCurrentQuotaAsync(string accountId, CancellationToken cancellationToken) =>
        context.AccountAiQuotas
            .AsNoTracking()
            .AnyAsync(quota => quota.AccountId == accountId && quota.EffectiveTo == null, cancellationToken);

    public void AddQuota(AccountAiQuota quota) => context.AccountAiQuotas.Add(quota);

    public void Forget() => context.ChangeTracker.Clear();

    /// <remarks>
    /// Seeks <c>IX_AccountAiQuotaReservations_Posted_Account</c>. The grouping projects to an anonymous type
    /// and the record is built afterwards, because EF cannot translate a constructor call over a grouping —
    /// the same reason <c>AiUsageReadRepository</c> shapes its own aggregates that way.
    /// </remarks>
    public async Task<IReadOnlyList<AiUsageConsumerTotal>> FindTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int take, CancellationToken cancellationToken)
    {
        var rows = await context.AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.PostedAt != null
                && reservation.PostedAt >= from
                && reservation.PostedAt < to)

            // Grouped by unit as well as account so the sum is never a total of two different things. In
            // practice an account has one unit and this is one row per account; when it is not, saying so is
            // better than adding credits to tokens.
            .GroupBy(reservation => new { reservation.AccountId, reservation.Unit })
            .Select(group => new
            {
                group.Key.AccountId,
                group.Key.Unit,
                Amount = group.Sum(reservation => reservation.SettledAmount ?? 0m),
                Runs = group.Count(),
            })
            .OrderByDescending(row => row.Amount)
            .ThenBy(row => row.AccountId)
            .Take(take)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new AiUsageConsumerTotal(row.AccountId, row.Unit, row.Amount, row.Runs))];
    }

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
