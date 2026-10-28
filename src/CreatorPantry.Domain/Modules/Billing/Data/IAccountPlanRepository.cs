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

    /// <summary>
    /// Accounts whose row <em>in force</em> at <paramref name="at"/> names <paramref name="planKey"/>, by account
    /// id, from <paramref name="afterAccountId"/> exclusive (AP.4.4).
    /// </summary>
    /// <param name="includeDefaultRows">
    /// Whether a row that resolves to the configured default counts. True when the plan asked about <em>is</em>
    /// the configured default.
    /// </param>
    /// <param name="catalogueKeys">
    /// Every key the catalogue still has, when <paramref name="includeDefaultRows"/> is set. A row naming a key
    /// outside this set names a plan since withdrawn, which resolves to the default too, so it is listed under
    /// the default rather than falling out of every answer.
    /// </param>
    /// <param name="take">One more than the page, so the caller knows whether a next page exists.</param>
    /// <remarks>
    /// "In force" is the row with the latest start that has started — the same rule
    /// <see cref="FindInForceAsync"/> applies to one account, written here as "no later row of this account has
    /// also started", which the provider reads as a <c>NOT EXISTS</c>. A scheduled change is therefore not
    /// listed under the plan it will move the account to, which is the honest answer to "who is on this plan".
    /// </remarks>
    Task<IReadOnlyList<AccountPlan>> ListInForceByPlanAsync(
        string planKey,
        bool includeDefaultRows,
        IReadOnlyCollection<string> catalogueKeys,
        DateTimeOffset at,
        string? afterAccountId,
        int take,
        CancellationToken cancellationToken);

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

    public async Task<IReadOnlyList<AccountPlan>> ListInForceByPlanAsync(
        string planKey,
        bool includeDefaultRows,
        IReadOnlyCollection<string> catalogueKeys,
        DateTimeOffset at,
        string? afterAccountId,
        int take,
        CancellationToken cancellationToken) =>
        await context.AccountPlans
            .AsNoTracking()
            .Where(plan => plan.EffectiveFrom <= at
                && (plan.PlanKey == planKey
                    || (includeDefaultRows
                        && (plan.PlanKey == null || !catalogueKeys.Contains(plan.PlanKey))))
                && (afterAccountId == null || plan.AccountId.CompareTo(afterAccountId) > 0)
                && !context.AccountPlans.Any(later =>
                    later.AccountId == plan.AccountId
                    && later.EffectiveFrom > plan.EffectiveFrom
                    && later.EffectiveFrom <= at))
            .OrderBy(plan => plan.AccountId)
            .Take(take)
            .ToListAsync(cancellationToken);

    public void Add(AccountPlan plan) => context.AccountPlans.Add(plan);

    public void Forget() => context.ChangeTracker.Clear();

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
