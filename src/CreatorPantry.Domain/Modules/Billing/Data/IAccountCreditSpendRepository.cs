using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>EF access for spending an account's grants and giving back what a hold did not spend (AP.5.2).</summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c>, for the reason <see cref="IAccountCreditGrantRepository"/> gives: neither entity is
/// <see cref="IWorkspaceOwned"/>, and every query here is keyed by the account it was asked about.
/// </remarks>
internal interface IAccountCreditSpendRepository
{
    /// <summary>
    /// The account's grants that have not been clawed back, tracked, in the order they are spent: oldest first, the
    /// id breaking a tie.
    /// </summary>
    /// <remarks>
    /// Includes grants with nothing left: the balance is filtered by the caller, because SQLite keeps a decimal as
    /// text and an account holds tens of grants, not thousands.
    /// </remarks>
    Task<IReadOnlyList<AccountCreditGrant>> ListStandingTrackedAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// A hold's draws that have not resolved, tracked with their grants, in the order they were taken — the grants'
    /// own spending order.
    /// </summary>
    Task<IReadOnlyList<AccountCreditGrantDraw>> ListOpenDrawsTrackedAsync(
        string accountId, Guid holdId, CancellationToken cancellationToken);

    void AddDraw(AccountCreditGrantDraw draw);
}

/// <inheritdoc cref="IAccountCreditSpendRepository"/>
internal sealed class AccountCreditSpendRepository(CreatorPantryDbContext context) : IAccountCreditSpendRepository
{
    /// <remarks>Seeks <c>IX_AccountCreditGrants_Account_GrantedAt</c>.</remarks>
    public async Task<IReadOnlyList<AccountCreditGrant>> ListStandingTrackedAsync(
        string accountId, CancellationToken cancellationToken) =>
        await context.AccountCreditGrants
            .Where(grant => grant.AccountId == accountId && grant.ClawedBackAt == null)
            .OrderBy(grant => grant.GrantedAt)
            .ThenBy(grant => grant.Id)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// Seeks <c>UX_AccountCreditGrantDraws_Hold_Grant</c>. Ordered in the database, by the grant's own keys, so the
    /// order is the one <see cref="ListStandingTrackedAsync"/> spent them in — a tie on the instant is broken the way
    /// the database compares ids, which is not the way .NET does.
    /// </remarks>
    public async Task<IReadOnlyList<AccountCreditGrantDraw>> ListOpenDrawsTrackedAsync(
        string accountId, Guid holdId, CancellationToken cancellationToken) =>
        await context.AccountCreditGrantDraws
            .Include(draw => draw.Grant)
            .Where(draw => draw.HoldId == holdId && draw.AccountId == accountId && draw.ResolvedAt == null)
            .OrderBy(draw => draw.Grant!.GrantedAt)
            .ThenBy(draw => draw.GrantId)
            .ToListAsync(cancellationToken);

    public void AddDraw(AccountCreditGrantDraw draw) => context.AccountCreditGrantDraws.Add(draw);
}
