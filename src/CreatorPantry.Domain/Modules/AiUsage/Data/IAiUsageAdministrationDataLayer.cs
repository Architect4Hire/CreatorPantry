using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>What a quota write did.</summary>
internal enum AiQuotaAdministrationWriteOutcome
{
    Applied = 1,

    /// <summary>Another writer closed or opened a row first; the caller re-reads and decides again.</summary>
    Contended = 2,
}

/// <summary>The terms to put in force, or null to leave the account on the platform default.</summary>
internal sealed record AccountAiQuotaWrite(
    AiQuotaUnit Unit,
    decimal? Allowance,
    AiQuotaPeriodLength PeriodLength,
    int PeriodAnchor,
    string TimeZoneId,
    AiQuotaCarryOver CarryOver,
    decimal? CarryOverCap,
    bool IsSuspended);

/// <summary>
/// Composes the platform administration writes and owns their transaction boundary.
/// </summary>
internal interface IAiUsageAdministrationDataLayer
{
    Task<AccountAiQuota?> FindCurrentQuotaAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Whether the account has terms of its own, for a read that will not write them.</summary>
    Task<bool> HasCurrentQuotaAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>What each capability charged the account this period, and how many attempts it made.</summary>
    Task<IReadOnlyList<AiUsageTaskTotal>> SumByTaskAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>What work in each workspace charged the account this period.</summary>
    Task<IReadOnlyList<AiUsageWorkspaceTotal>> SumByWorkspaceAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes the account's open terms row and opens <paramref name="replacement"/> in its place, staging
    /// <paramref name="audit"/> alongside.
    /// </summary>
    /// <param name="replacement">Null clears the quota: the open row is closed and nothing replaces it.</param>
    /// <remarks>
    /// <para>
    /// <strong>One <c>SaveChangesAsync</c>, and therefore one transaction</strong> — the close, the insert and
    /// the audit row commit together or not at all. An audit trail that could record a change the database
    /// rejected would be worse than none.
    /// </para>
    /// <para>
    /// <strong>The terms are never edited in place.</strong> <c>AccountAiQuota</c> is effective-dated, so a
    /// change is a close plus an insert, and the filtered unique index
    /// <c>UX_AccountAiQuotas_Account_Current</c> is what makes "at most one open row" true rather than
    /// intended. A racing writer loses on that index and comes back as
    /// <see cref="AiQuotaAdministrationWriteOutcome.Contended"/>.
    /// </para>
    /// <para>
    /// <strong>The index alone is not enough, because a clear inserts nothing.</strong> A concurrent clear and
    /// set would both close the same open row and only one would insert, so neither would collide and both
    /// would report success — leaving an audit row claiming the account was returned to the platform default
    /// while it actually sits on the other command's terms. <c>AccountAiQuota.EffectiveTo</c> is therefore a
    /// concurrency token: the close carries <c>WHERE EffectiveTo IS NULL</c>, and the second writer finds no
    /// row to close and is told so.
    /// </para>
    /// </remarks>
    Task<AiQuotaAdministrationWriteOutcome> ReplaceQuotaAsync(
        string accountId,
        AccountAiQuota? current,
        AccountAiQuotaWrite? replacement,
        PlatformAuditEntry audit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AiUsageConsumerTotal>> FindTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int take, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageAdministrationDataLayer"/>
internal sealed class AiUsageAdministrationDataLayer(
    IAiUsageAdministrationRepository repository,
    IAiUsageReadRepository usage,
    IPlatformAuditWriter audit,
    IClock clock) : IAiUsageAdministrationDataLayer
{
    public Task<AccountAiQuota?> FindCurrentQuotaAsync(string accountId, CancellationToken cancellationToken) =>
        repository.FindCurrentQuotaForUpdateAsync(accountId, cancellationToken);

    public Task<bool> HasCurrentQuotaAsync(string accountId, CancellationToken cancellationToken) =>
        repository.HasCurrentQuotaAsync(accountId, cancellationToken);

    /// <remarks>
    /// Composed here rather than reached directly from Business, which calls a DataLayer and never a
    /// repository (backend.md). The breakdown repository is the one USAGE-008's read seam already uses, so an
    /// administrator's figures and a creator's own come from the same query and cannot drift apart.
    /// </remarks>
    public Task<IReadOnlyList<AiUsageTaskTotal>> SumByTaskAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) =>
        usage.SumByTaskAsync(accountId, periodId, from, to, cancellationToken);

    /// <inheritdoc cref="SumByTaskAsync"/>
    public Task<IReadOnlyList<AiUsageWorkspaceTotal>> SumByWorkspaceAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) =>
        usage.SumByWorkspaceAsync(accountId, periodId, from, to, cancellationToken);

    public async Task<AiQuotaAdministrationWriteOutcome> ReplaceQuotaAsync(
        string accountId,
        AccountAiQuota? current,
        AccountAiQuotaWrite? replacement,
        PlatformAuditEntry entry,
        CancellationToken cancellationToken)
    {
        // Strictly after the row being closed, not merely "now". CK_AccountAiQuotas_Effective_Range demands
        // EffectiveTo > EffectiveFrom, and two commands against one account can land on the same clock reading
        // — a set followed straight away by a suspension, or anything under a frozen test clock. Nudging by a
        // tick keeps the rows abutting exactly while staying inside the constraint.
        var now = clock.UtcNow;
        var at = current is not null && now <= current.EffectiveFrom
            ? current.EffectiveFrom.AddTicks(1)
            : now;

        if (current is not null)
        {
            current.EffectiveTo = at;
        }

        if (replacement is not null)
        {
            repository.AddQuota(new AccountAiQuota
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Unit = replacement.Unit,
                Allowance = replacement.Allowance,
                PeriodLength = replacement.PeriodLength,
                PeriodAnchor = replacement.PeriodAnchor,
                TimeZoneId = replacement.TimeZoneId,
                CarryOver = replacement.CarryOver,
                CarryOverCap = replacement.CarryOverCap,
                IsSuspended = replacement.IsSuspended,

                // The new row takes effect at the instant the old one stopped, so the two abut exactly and no
                // reader can land between them and find the account with no terms at all.
                EffectiveFrom = at,
                EffectiveTo = null,

                // Null on purpose for an ops client: this is a user-id column and an ops credential is not a
                // user (B-14). Who acted is recorded in PlatformAuditLog, which is where USAGE-009 asks for it
                // and which can name a machine actor.
                LastChangedByUserId = null,
                LastChangedAt = at,
            });
        }

        audit.Record(entry);

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException)
        {
            // Two writers, two ways to lose, and both mean the same thing to the caller. A concurrency failure
            // is the close losing its `EffectiveTo IS NULL` predicate — somebody else closed this row first,
            // which is the case the filtered unique index cannot catch because a *clear* inserts nothing. A
            // plain DbUpdateException is two inserts racing that index.
            //
            // Forget first. The close is staged as a mutation on the shared scoped DbContext, and leaving it
            // there would let a later SaveChangesAsync in the same request commit a change this method just
            // told its caller was refused.
            repository.Forget();

            return AiQuotaAdministrationWriteOutcome.Contended;
        }

        return AiQuotaAdministrationWriteOutcome.Applied;
    }

    public Task<IReadOnlyList<AiUsageConsumerTotal>> FindTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int take, CancellationToken cancellationToken) =>
        repository.FindTopConsumersAsync(from, to, take, cancellationToken);
}
