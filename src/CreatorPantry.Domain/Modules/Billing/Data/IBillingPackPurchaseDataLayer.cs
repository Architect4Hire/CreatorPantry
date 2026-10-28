using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>
/// Composes what a pack's purchase and its reversal need (AP.6.3): the provider reads, the account behind a provider
/// customer, and the purchase record's own writes.
/// </summary>
internal interface IBillingPackPurchaseDataLayer
{
    /// <summary>The pack's checkout as the provider holds it now.</summary>
    Task<BillingPackCheckoutRead> ReadCheckoutAsync(string externalSessionId, CancellationToken cancellationToken);

    /// <summary>The refund or dispute as the provider holds it now, and the payment it reverses.</summary>
    Task<BillingPaymentReversalRead> ReadReversalAsync(
        string externalReference, BillingPackReversal kind, CancellationToken cancellationToken);

    /// <summary>The account a provider customer is, in either mode; null when no account is.</summary>
    Task<BillingCustomer?> FindCustomerAsync(string externalCustomerId, CancellationToken cancellationToken);

    Task<BillingPackPurchase?> FindBySessionAsync(string externalSessionId, CancellationToken cancellationToken);

    /// <summary>The purchase a payment made, tracked so its reversal can be written under its row version.</summary>
    Task<BillingPackPurchase?> FindByPaymentForUpdateAsync(
        string externalPaymentReference, CancellationToken cancellationToken);

    /// <summary>Records a purchase against the grant it made.</summary>
    /// <returns>
    /// False when another worker recorded the same session or payment first: whoever won recorded the same purchase,
    /// because both read the same checkout from the provider.
    /// </returns>
    Task<bool> RecordPurchaseAsync(BillingPackPurchase purchase, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the reversal Business recorded on <paramref name="purchase"/> — as read by
    /// <see cref="FindByPaymentForUpdateAsync"/> — under the row version it was read with.
    /// </summary>
    /// <returns>False when the row moved since: another reversal of the same payment was written first.</returns>
    Task<bool> RecordReversalAsync(BillingPackPurchase purchase, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingPackPurchaseDataLayer"/>
internal sealed class BillingPackPurchaseDataLayer(
    IBillingPackPurchaseRepository repository,
    IBillingCustomerRepository customers,
    IBillingPaymentGateway gateway) : IBillingPackPurchaseDataLayer
{
    public Task<BillingPackCheckoutRead> ReadCheckoutAsync(
        string externalSessionId, CancellationToken cancellationToken) =>
        gateway.GetPackCheckoutAsync(externalSessionId, cancellationToken);

    public Task<BillingPaymentReversalRead> ReadReversalAsync(
        string externalReference, BillingPackReversal kind, CancellationToken cancellationToken) =>
        gateway.GetPaymentReversalAsync(externalReference, kind, cancellationToken);

    public Task<BillingCustomer?> FindCustomerAsync(string externalCustomerId, CancellationToken cancellationToken) =>
        customers.FindByExternalCustomerAsync(externalCustomerId, cancellationToken);

    public Task<BillingPackPurchase?> FindBySessionAsync(
        string externalSessionId, CancellationToken cancellationToken) =>
        repository.FindBySessionAsync(externalSessionId, cancellationToken);

    public Task<BillingPackPurchase?> FindByPaymentForUpdateAsync(
        string externalPaymentReference, CancellationToken cancellationToken) =>
        repository.FindByPaymentForUpdateAsync(externalPaymentReference, cancellationToken);

    public async Task<bool> RecordPurchaseAsync(BillingPackPurchase purchase, CancellationToken cancellationToken)
    {
        repository.Add(purchase);

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsLostRace(exception))
        {
            // Forget first, as the grant's own insert does: the row is staged on the shared scoped context, and a
            // later save in the same scope must not commit a purchase this method just reported refused.
            repository.Forget();

            return false;
        }

        return true;
    }

    public async Task<bool> RecordReversalAsync(BillingPackPurchase purchase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purchase);

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            repository.Forget();

            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a refused insert lost a race on the session or payment index (2601, 2627) rather than failed. Anything
    /// else, an outage included, is not a race and is not reported as one. A provider other than SQL Server — the
    /// SQLite the tests run on — is read as a race throughout, as the grant's insert reads it.
    /// </summary>
    private static bool IsLostRace(DbUpdateException exception) =>
        exception.InnerException is not SqlException sql || sql.Number is 2601 or 2627;
}
