using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>EF access for the credits granted to an account (AP.5.1).</summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore: <see cref="AccountCreditGrant"/> is not
/// <see cref="IWorkspaceOwned"/>, and every query here is keyed by the account it was asked about — or, for the
/// reference, by the source and reference that are unique across every account.
/// </remarks>
internal interface IAccountCreditGrantRepository
{
    /// <summary>
    /// One grant of one account, tracked so a claw-back can change it under its row version; null when the account
    /// has no grant with that id.
    /// </summary>
    Task<AccountCreditGrant?> FindTrackedAsync(string accountId, Guid grantId, CancellationToken cancellationToken);

    /// <summary>One grant of one account, untracked, or null.</summary>
    Task<AccountCreditGrant?> FindAsync(string accountId, Guid grantId, CancellationToken cancellationToken);

    /// <summary>The grant an external reference made within its source, whichever account it went to, or null.</summary>
    Task<AccountCreditGrant?> FindByReferenceAsync(
        CreditGrantSource source, string externalReferenceId, CancellationToken cancellationToken);

    /// <summary>
    /// The account's grants in the order they were made, after <paramref name="after"/> exclusive when given.
    /// </summary>
    /// <param name="take">One more than the page, so the caller knows whether a next page exists.</param>
    Task<IReadOnlyList<AccountCreditGrant>> ListAsync(
        string accountId, AccountCreditGrant? after, int take, CancellationToken cancellationToken);

    /// <summary>What is left on each of the account's grants, for the total across them.</summary>
    /// <remarks>
    /// The figures rather than their sum: SQLite keeps a decimal as text and has no exact sum of its own, and an
    /// account holds tens of grants, not thousands.
    /// </remarks>
    Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken);

    void Add(AccountCreditGrant grant);

    /// <summary>Forgets everything tracked, so a rejected write cannot ride along on a later save.</summary>
    void Forget();

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountCreditGrantRepository"/>
internal sealed class AccountCreditGrantRepository(CreatorPantryDbContext context) : IAccountCreditGrantRepository
{
    public Task<AccountCreditGrant?> FindTrackedAsync(
        string accountId, Guid grantId, CancellationToken cancellationToken) =>
        context.AccountCreditGrants
            .SingleOrDefaultAsync(grant => grant.Id == grantId && grant.AccountId == accountId, cancellationToken);

    public Task<AccountCreditGrant?> FindAsync(string accountId, Guid grantId, CancellationToken cancellationToken) =>
        context.AccountCreditGrants
            .AsNoTracking()
            .SingleOrDefaultAsync(grant => grant.Id == grantId && grant.AccountId == accountId, cancellationToken);

    /// <remarks>Seeks <c>UX_AccountCreditGrants_Source_Reference</c>.</remarks>
    public Task<AccountCreditGrant?> FindByReferenceAsync(
        CreditGrantSource source, string externalReferenceId, CancellationToken cancellationToken) =>
        context.AccountCreditGrants
            .AsNoTracking()
            .SingleOrDefaultAsync(
                grant => grant.Source == source && grant.ExternalReferenceId == externalReferenceId,
                cancellationToken);

    /// <remarks>Seeks <c>IX_AccountCreditGrants_Account_GrantedAt</c>, keyed on the instant and then the id.</remarks>
    public async Task<IReadOnlyList<AccountCreditGrant>> ListAsync(
        string accountId, AccountCreditGrant? after, int take, CancellationToken cancellationToken)
    {
        var grants = context.AccountCreditGrants
            .AsNoTracking()
            .Where(grant => grant.AccountId == accountId);

        if (after is not null)
        {
            grants = grants.Where(grant => grant.GrantedAt > after.GrantedAt
                || (grant.GrantedAt == after.GrantedAt && grant.Id.CompareTo(after.Id) > 0));
        }

        return await grants
            .OrderBy(grant => grant.GrantedAt)
            .ThenBy(grant => grant.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken) =>
        await context.AccountCreditGrants
            .AsNoTracking()
            .Where(grant => grant.AccountId == accountId)
            .Select(grant => grant.CreditsRemaining)
            .ToListAsync(cancellationToken);

    public void Add(AccountCreditGrant grant) => context.AccountCreditGrants.Add(grant);

    public void Forget() => context.ChangeTracker.Clear();

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
