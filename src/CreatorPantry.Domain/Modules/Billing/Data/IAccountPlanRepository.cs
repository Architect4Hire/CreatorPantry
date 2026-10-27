using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>EF access for which plan an account is on.</summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore: <see cref="AccountPlan"/> is not
/// <see cref="IWorkspaceOwned"/>, and every query here is keyed by the account it was asked about.
/// </remarks>
internal interface IAccountPlanRepository
{
    /// <summary>The row in force at <paramref name="at"/>: the latest to have started by then.</summary>
    Task<AccountPlan?> FindInForceAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>When the first row after <paramref name="at"/> starts, or null when none is scheduled.</summary>
    Task<DateTimeOffset?> FindNextStartAfterAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The end of the account's chain — the row a change must supersede — or null when it has none.</summary>
    Task<AccountPlan?> FindLatestAsync(string accountId, CancellationToken cancellationToken);

    void Add(AccountPlan plan);

    /// <summary>Forgets everything tracked, so a rejected write cannot ride along on a later save.</summary>
    void Forget();

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountPlanRepository"/>
internal sealed class AccountPlanRepository(CreatorPantryDbContext context) : IAccountPlanRepository
{
    /// <remarks>Seeks <c>AK_AccountPlans_Account_EffectiveFrom</c> backwards from the instant.</remarks>
    public Task<AccountPlan?> FindInForceAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken) =>
        context.AccountPlans
            .AsNoTracking()
            .Where(plan => plan.AccountId == accountId && plan.EffectiveFrom <= at)
            .OrderByDescending(plan => plan.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

    /// <remarks>
    /// Projected to a nullable instant so an account with nothing scheduled reads as null rather than as the
    /// type's default.
    /// </remarks>
    public Task<DateTimeOffset?> FindNextStartAfterAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken) =>
        context.AccountPlans
            .AsNoTracking()
            .Where(plan => plan.AccountId == accountId && plan.EffectiveFrom > at)
            .OrderBy(plan => plan.EffectiveFrom)
            .Select(plan => (DateTimeOffset?)plan.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

    /// <remarks>
    /// Untracked: a change never edits the row it supersedes, it only names that row's start. The successor
    /// index decides a race, so reading this under a lock would buy nothing.
    /// </remarks>
    public Task<AccountPlan?> FindLatestAsync(string accountId, CancellationToken cancellationToken) =>
        context.AccountPlans
            .AsNoTracking()
            .Where(plan => plan.AccountId == accountId)
            .OrderByDescending(plan => plan.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(AccountPlan plan) => context.AccountPlans.Add(plan);

    public void Forget() => context.ChangeTracker.Clear();

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
