using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>An account's provider customer, or why there is none.</summary>
/// <param name="Customer">Present exactly when the outcome is a success.</param>
internal sealed record BillingCustomerEnsured(BillingPaymentOutcome Outcome, BillingCustomer? Customer = null);

/// <summary>Composes the customer mapping with the provider that the mapping is to.</summary>
internal interface IBillingCustomerDataLayer
{
    /// <summary>The account's customer in the mode this host's provider acts in, or null when it has none yet.</summary>
    Task<BillingCustomer?> FindAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// The account's customer, made at the provider and recorded the first time it is asked for, and read from the
    /// record every time after. Asked for twice at once, both callers get the one customer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read comes first, so an account that has paid before never reaches the provider here. Then the provider,
    /// then one insert. Two requests that both read nothing both ask the provider — with the same idempotency key,
    /// derived from the account, so Stripe makes one customer and answers both with it — and both insert; the unique
    /// index on the account lets one in, and the other re-reads that row and returns it.
    /// </para>
    /// <para>
    /// The provider call sits outside any transaction on purpose: a transaction held open across an HTTP call holds
    /// its locks for as long as the provider takes. Nothing is lost by it, because the index decides the race and the
    /// key makes the provider's side of it one customer.
    /// </para>
    /// </remarks>
    Task<BillingCustomerEnsured> EnsureAsync(string accountId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingCustomerDataLayer"/>
internal sealed class BillingCustomerDataLayer(
    IBillingCustomerRepository repository,
    IBillingPaymentGateway gateway,
    IClock clock,
    ILogger<BillingCustomerDataLayer> logger) : IBillingCustomerDataLayer
{
    public Task<BillingCustomer?> FindAsync(string accountId, CancellationToken cancellationToken) =>
        gateway.Mode == BillingPaymentMode.NotConfigured
            ? Task.FromResult<BillingCustomer?>(null)
            : repository.FindByAccountAsync(accountId, gateway.Mode == BillingPaymentMode.Live, cancellationToken);

    public async Task<BillingCustomerEnsured> EnsureAsync(string accountId, CancellationToken cancellationToken)
    {
        if (gateway.Mode == BillingPaymentMode.NotConfigured)
        {
            return new BillingCustomerEnsured(BillingPaymentOutcome.NotConfigured);
        }

        var isLiveMode = gateway.Mode == BillingPaymentMode.Live;

        if (await repository.FindByAccountAsync(accountId, isLiveMode, cancellationToken) is { } existing)
        {
            return new BillingCustomerEnsured(BillingPaymentOutcome.Succeeded, existing);
        }

        var created = await gateway.CreateCustomerAsync(accountId, cancellationToken);
        if (created.Outcome is not BillingPaymentOutcome.Succeeded || created.ExternalCustomerId is null)
        {
            return new BillingCustomerEnsured(created.Outcome is BillingPaymentOutcome.Succeeded
                ? BillingPaymentOutcome.Unreadable
                : created.Outcome);
        }

        var customer = new BillingCustomer
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExternalCustomerId = created.ExternalCustomerId,
            IsLiveMode = created.IsLiveMode,
            CreatedAt = clock.UtcNow,
        };

        repository.Add(customer);

        try
        {
            await repository.SaveAsync(cancellationToken);
            return new BillingCustomerEnsured(BillingPaymentOutcome.Succeeded, customer);
        }
        catch (DbUpdateException exception) when (IsLostRace(exception))
        {
            // Forget first: the insert is staged on the shared scoped DbContext, and leaving it there would let a
            // later save in the same request try it again. Only this row: anything else staged is the caller's.
            repository.Forget(customer);
        }

        var winner = await repository.FindByAccountAsync(accountId, isLiveMode, cancellationToken);
        if (winner is null)
        {
            // The index refused the row and no row is there: the customer id belongs to another account, which Stripe
            // cannot have done for a key derived from this one. Not a race, and not something to paper over.
            logger.LogError("A new billing customer could not be recorded, and the account still has none.");
            return new BillingCustomerEnsured(BillingPaymentOutcome.Unreadable);
        }

        if (!string.Equals(winner.ExternalCustomerId, customer.ExternalCustomerId, StringComparison.Ordinal))
        {
            // Two customers at Stripe for one account: only possible once the idempotency key has been pruned, a day
            // after the first. Harmless — nothing refers to the loser — but worth an operator knowing about.
            logger.LogWarning("Two provider customers were created for one account; the first recorded is kept.");
        }

        return new BillingCustomerEnsured(BillingPaymentOutcome.Succeeded, winner);
    }

    /// <summary>
    /// A unique index (2601) or key (2627): another request recorded the account's customer first. A provider other
    /// than SQL Server — the SQLite the tests run on — is read as a race throughout, as the plan's append reads it.
    /// </summary>
    private static bool IsLostRace(DbUpdateException exception) =>
        exception.InnerException is not SqlException sql || sql.Number is 2601 or 2627;
}
