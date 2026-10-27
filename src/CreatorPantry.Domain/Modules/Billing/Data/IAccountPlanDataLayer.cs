using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>The row in force at an instant, and when the next one takes over.</summary>
/// <param name="InForce">Null when the account has no row that has started.</param>
/// <param name="NextStartsAt">Null when no later row is scheduled.</param>
internal sealed record AccountPlanSpan(AccountPlan? InForce, DateTimeOffset? NextStartsAt);

/// <summary>Composes the plan reads and owns the change's transaction boundary.</summary>
internal interface IAccountPlanDataLayer
{
    Task<AccountPlanSpan> FindSpanAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<AccountPlan?> FindLatestAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a row superseding <paramref name="latest"/> — the end of the chain the caller read — or the account's
    /// first row when it is null.
    /// </summary>
    /// <returns>
    /// <see cref="AccountPlanChangeOutcome.Changed"/>, or <see cref="AccountPlanChangeOutcome.Contended"/> when
    /// another writer appended first.
    /// </returns>
    /// <remarks>
    /// One insert and one <c>SaveChangesAsync</c>; nothing already stored is edited. A racing writer that read the
    /// same end of the chain loses on <c>UX_AccountPlans_Account_Successor</c> (or, for a first row, on
    /// <c>UX_AccountPlans_Account_Root</c>), and two rows landing on one instant lose on the alternate key — each a
    /// <see cref="DbUpdateException"/>, and each the same answer to the caller.
    /// </remarks>
    Task<AccountPlanChangeOutcome> AppendAsync(
        string accountId,
        AccountPlan? latest,
        string? planKey,
        AccountPlanSource source,
        DateTimeOffset effectiveFrom,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountPlanDataLayer"/>
internal sealed class AccountPlanDataLayer(IAccountPlanRepository repository) : IAccountPlanDataLayer
{
    public async Task<AccountPlanSpan> FindSpanAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var inForce = await repository.FindInForceAsync(accountId, at, cancellationToken);
        var next = await repository.FindNextStartAfterAsync(accountId, at, cancellationToken);

        return new AccountPlanSpan(inForce, next);
    }

    public Task<AccountPlan?> FindLatestAsync(string accountId, CancellationToken cancellationToken) =>
        repository.FindLatestAsync(accountId, cancellationToken);

    public async Task<AccountPlanChangeOutcome> AppendAsync(
        string accountId,
        AccountPlan? latest,
        string? planKey,
        AccountPlanSource source,
        DateTimeOffset effectiveFrom,
        CancellationToken cancellationToken)
    {
        repository.Add(new AccountPlan
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanKey = planKey,
            Source = source,
            EffectiveFrom = effectiveFrom,
            SupersedesFrom = latest?.EffectiveFrom,
        });

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsLostRace(exception))
        {
            // Forget first: the insert is staged on the shared scoped DbContext, and leaving it there would let a
            // later save in the same request commit a change this method just told its caller was refused.
            repository.Forget();

            return AccountPlanChangeOutcome.Contended;
        }

        return AccountPlanChangeOutcome.Changed;
    }

    /// <summary>
    /// Whether a refused insert lost a race rather than failed: a unique index or key (2601, 2627) — another writer
    /// appended first — or the foreign key (547), because the row this supersedes was read a moment ago and can only
    /// be missing now if it has since been removed. Anything else, an outage included, is not a race and is not
    /// reported as one, since a caller told "contended" re-reads and tries again.
    /// </summary>
    /// <remarks>
    /// A check constraint is also 547, and is still not expected here: Business refuses a blank or over-long
    /// account, an undeclared source and a start not after the latest row before it calls. A provider other than
    /// SQL Server — the SQLite the tests run on — is read as a race throughout, as the quota's replacement reads it.
    /// </remarks>
    private static bool IsLostRace(DbUpdateException exception) =>
        exception.InnerException is not SqlException sql || sql.Number is 2601 or 2627 or 547;
}
