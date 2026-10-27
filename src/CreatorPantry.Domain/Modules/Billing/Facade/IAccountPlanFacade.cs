using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// The application boundary for which plan an account is on (PRICE-007, AP.4.1). How other modules learn an
/// account's allowance, picture caps and quality classes: through this facade, never the table.
/// </summary>
/// <remarks>
/// <strong>Keyed by account, never by workspace.</strong> A plan is the account's across every workspace it works
/// in (B-47). The caller passes the account it has already established — <c>IWorkspaceContext.AccountId</c> on a
/// creator's request; in a worker, the initiating account recorded on the validated job, <em>not</em> the worker's own
/// context, whose account is the background service identity and would resolve every job to the default plan — and
/// nothing here takes one from a request body, a query or a model.
/// </remarks>
public interface IAccountPlanFacade
{
    /// <summary>The plan the account is on now, with its terms from the catalogue.</summary>
    /// <returns>
    /// Null when no plans are configured — every deployment today — so the caller keeps its flat limits. Otherwise
    /// always a plan: an account with no row, a row that says "the default", or a row naming a plan the catalogue
    /// no longer has all read as <c>Billing:DefaultPlan</c>.
    /// </returns>
    Task<AccountPlanServiceModel?> ResolveAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>The plan the account was on at <paramref name="at"/>, read as <see cref="ResolveAsync(string, CancellationToken)"/> reads now.</summary>
    /// <remarks>
    /// For the allowance period (AP.4.2): a period opens with the plan in force at its own start, so a change made
    /// after the roll waits for the next one even when the account's period row is only written later.
    /// </remarks>
    Task<AccountPlanServiceModel?> ResolveAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountPlanFacade"/>
internal sealed class AccountPlanFacade(IAccountPlanBusiness business) : IAccountPlanFacade
{
    public Task<AccountPlanServiceModel?> ResolveAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return business.ResolveAsync(accountId, null, cancellationToken);
    }

    public Task<AccountPlanServiceModel?> ResolveAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return business.ResolveAsync(accountId, at, cancellationToken);
    }
}
