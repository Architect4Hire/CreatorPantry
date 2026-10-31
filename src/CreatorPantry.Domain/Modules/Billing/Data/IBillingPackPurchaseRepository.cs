using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>EF access for the packs an account has paid for (AP.6.3).</summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore: <see cref="BillingPackPurchase"/> is not
/// <see cref="IWorkspaceOwned"/>, and every query here is keyed by the checkout session or the payment it asks about
/// — each unique across every account, which is what lets a reversal find the one purchase it is about.
/// </remarks>
internal interface IBillingPackPurchaseRepository
{
    /// <summary>Untracked. Seeks <c>UX_BillingPackPurchases_Session</c>.</summary>
    Task<BillingPackPurchase?> FindBySessionAsync(string externalSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Tracked, for the one update a purchase takes: its reversal. Seeks <c>UX_BillingPackPurchases_Payment</c>.
    /// </summary>
    Task<BillingPackPurchase?> FindByPaymentForUpdateAsync(
        string externalPaymentReference, CancellationToken cancellationToken);

    void Add(BillingPackPurchase purchase);

    /// <summary>Forgets everything tracked, so a rejected write cannot ride along on a later save.</summary>
    void Forget();

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingPackPurchaseRepository"/>
internal sealed class BillingPackPurchaseRepository(CreatorPantryDbContext context) : IBillingPackPurchaseRepository
{
    public Task<BillingPackPurchase?> FindBySessionAsync(string externalSessionId, CancellationToken cancellationToken) =>
        context.BillingPackPurchases
            .AsNoTracking()
            .SingleOrDefaultAsync(purchase => purchase.ExternalSessionId == externalSessionId, cancellationToken);

    public Task<BillingPackPurchase?> FindByPaymentForUpdateAsync(
        string externalPaymentReference, CancellationToken cancellationToken) =>
        context.BillingPackPurchases
            .SingleOrDefaultAsync(
                purchase => purchase.ExternalPaymentReference == externalPaymentReference, cancellationToken);

    public void Add(BillingPackPurchase purchase) => context.BillingPackPurchases.Add(purchase);

    public void Forget() => context.ChangeTracker.Clear();

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
