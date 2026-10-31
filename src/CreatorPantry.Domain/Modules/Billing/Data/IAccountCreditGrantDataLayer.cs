using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>Composes the credit grant reads and owns each write's transaction boundary (AP.5.1).</summary>
internal interface IAccountCreditGrantDataLayer
{
    /// <summary>The grant, tracked for a claw-back, or null when the account has none with that id.</summary>
    Task<AccountCreditGrant?> FindForUpdateAsync(string accountId, Guid grantId, CancellationToken cancellationToken);

    Task<AccountCreditGrant?> FindAsync(string accountId, Guid grantId, CancellationToken cancellationToken);

    Task<AccountCreditGrant?> FindByReferenceAsync(
        CreditGrantSource source, string externalReferenceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountCreditGrant>> ListAsync(
        string accountId, AccountCreditGrant? after, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Records a new grant, with its audit row in the same save when there is one.</summary>
    /// <returns>
    /// <see cref="AccountCreditGrantOutcome.Granted"/>, or <see cref="AccountCreditGrantOutcome.Contended"/> when
    /// another writer recorded a grant for the same reference first.
    /// </returns>
    Task<AccountCreditGrantOutcome> AddAsync(
        AccountCreditGrant grant, PlatformAuditEntry? audit, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the claw-back Business recorded on <paramref name="grant"/> — as read by
    /// <see cref="FindForUpdateAsync"/> — under the row version it was read with, with its audit row in the same save.
    /// </summary>
    /// <returns>
    /// <see cref="AccountCreditGrantOutcome.ClawedBack"/>, or <see cref="AccountCreditGrantOutcome.Contended"/> when
    /// the balance moved after it was read: the row version no longer matches, and nothing is taken.
    /// </returns>
    /// <remarks>
    /// The figure taken is the one read, which is why a moved row version refuses the write rather than retrying
    /// it here: a claw-back computed from a balance a spend has since lowered would record credits taken that
    /// were already spent.
    /// </remarks>
    Task<AccountCreditGrantOutcome> SaveClawBackAsync(
        AccountCreditGrant grant, PlatformAuditEntry? audit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountCreditGrantDataLayer"/>
internal sealed class AccountCreditGrantDataLayer(
    IAccountCreditGrantRepository repository, IPlatformAuditWriter auditWriter) : IAccountCreditGrantDataLayer
{
    public Task<AccountCreditGrant?> FindForUpdateAsync(
        string accountId, Guid grantId, CancellationToken cancellationToken) =>
        repository.FindTrackedAsync(accountId, grantId, cancellationToken);

    public Task<AccountCreditGrant?> FindAsync(string accountId, Guid grantId, CancellationToken cancellationToken) =>
        repository.FindAsync(accountId, grantId, cancellationToken);

    public Task<AccountCreditGrant?> FindByReferenceAsync(
        CreditGrantSource source, string externalReferenceId, CancellationToken cancellationToken) =>
        repository.FindByReferenceAsync(source, externalReferenceId, cancellationToken);

    public Task<IReadOnlyList<AccountCreditGrant>> ListAsync(
        string accountId, AccountCreditGrant? after, int take, CancellationToken cancellationToken) =>
        repository.ListAsync(accountId, after, take, cancellationToken);

    public Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken) =>
        repository.ListRemainingAsync(accountId, cancellationToken);

    public async Task<AccountCreditGrantOutcome> AddAsync(
        AccountCreditGrant grant, PlatformAuditEntry? audit, CancellationToken cancellationToken)
    {
        repository.Add(grant);

        // Staged on the same DbContext, so the grant and its audit row commit together or neither does. Null for
        // a grant nothing audits here — AP.6.3's purchase, which answers to its verified webhook instead.
        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsLostRace(exception))
        {
            // Forget first, as the plan's append does: the insert and its audit row are staged on the shared
            // scoped DbContext, and a later save in the same request must not commit a grant this method just
            // told its caller was refused.
            repository.Forget();

            return AccountCreditGrantOutcome.Contended;
        }

        return AccountCreditGrantOutcome.Granted;
    }

    public async Task<AccountCreditGrantOutcome> SaveClawBackAsync(
        AccountCreditGrant grant, PlatformAuditEntry? audit, CancellationToken cancellationToken)
    {
        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            repository.Forget();

            return AccountCreditGrantOutcome.Contended;
        }

        return AccountCreditGrantOutcome.ClawedBack;
    }

    /// <summary>
    /// Whether a refused insert lost a race on the reference index (2601, 2627) rather than failed. Anything else,
    /// an outage included, is not a race and is not reported as one, since a caller told "contended" re-reads and
    /// decides again. A provider other than SQL Server — the SQLite the tests run on — is read as a race throughout,
    /// as the plan's append reads it.
    /// </summary>
    private static bool IsLostRace(DbUpdateException exception) =>
        exception.InnerException is not SqlException sql || sql.Number is 2601 or 2627;
}
