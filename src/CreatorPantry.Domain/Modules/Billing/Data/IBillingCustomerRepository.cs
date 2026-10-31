using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>EF access for which provider customer an account is.</summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore: <see cref="BillingCustomer"/> is not
/// <see cref="IWorkspaceOwned"/>, and every query here is keyed by the account or the customer it was asked about.
/// </remarks>
internal interface IBillingCustomerRepository
{
    /// <summary>Seeks <c>UX_BillingCustomers_Account_Mode</c>.</summary>
    Task<BillingCustomer?> FindByAccountAsync(string accountId, bool isLiveMode, CancellationToken cancellationToken);

    /// <summary>Seeks <c>UX_BillingCustomers_ExternalCustomerId</c>: the account a webhook's customer is (AP.6.2).</summary>
    Task<BillingCustomer?> FindByExternalCustomerAsync(string externalCustomerId, CancellationToken cancellationToken);

    void Add(BillingCustomer customer);

    /// <summary>
    /// Forgets one rejected insert, so it cannot ride along on a later save — and only that one, so whatever else
    /// the request has staged on the shared context survives the race.
    /// </summary>
    void Forget(BillingCustomer customer);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingCustomerRepository"/>
internal sealed class BillingCustomerRepository(CreatorPantryDbContext context) : IBillingCustomerRepository
{
    public Task<BillingCustomer?> FindByAccountAsync(string accountId, bool isLiveMode, CancellationToken cancellationToken) =>
        context.BillingCustomers
            .AsNoTracking()
            .SingleOrDefaultAsync(customer => customer.AccountId == accountId && customer.IsLiveMode == isLiveMode, cancellationToken);

    public Task<BillingCustomer?> FindByExternalCustomerAsync(string externalCustomerId, CancellationToken cancellationToken) =>
        context.BillingCustomers
            .AsNoTracking()
            .SingleOrDefaultAsync(customer => customer.ExternalCustomerId == externalCustomerId, cancellationToken);

    public void Add(BillingCustomer customer) => context.BillingCustomers.Add(customer);

    public void Forget(BillingCustomer customer) => context.Entry(customer).State = EntityState.Detached;

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
