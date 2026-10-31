using CreatorPantry.Domain.Modules.Billing.Business;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// How the AI allowance spends an account's grants after its period allowance, and gives back what a hold did not
/// spend (PRICE-008, B-46, B-50, AP.5.2). The only way into the grants' balances from another module.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed by account, never by workspace</strong> (B-47), and told the account rather than looking one up:
/// the caller resolved it server-side, from the request's own context, and never from a request field or a model.
/// </para>
/// <para>
/// <strong>The two writes stage and do not save</strong>, so the hold that asks for them commits them in its own
/// transaction — the split across the allowance and grants is atomic, and a hold whose save fails leaves no debit
/// behind. A caller that retries after a lost race forgets what was tracked and asks again.
/// </para>
/// <para>
/// Separate from <see cref="IAccountCreditGrantFacade"/>, which is the operator's boundary and carries who acted.
/// Nothing here is an operator's act, and nothing here writes an audit row: the hold is the record.
/// </para>
/// </remarks>
public interface IAccountCreditSpendFacade
{
    /// <summary>The account's unspent grant credits — its pack balance, which never resets (B-50). Reads only.</summary>
    Task<decimal> BalanceAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes <paramref name="credits"/> from the account's grants for one hold, oldest grant first; false, having
    /// staged nothing, when they do not hold that many between them. Stages; does not save.
    /// </summary>
    /// <param name="holdId">The hold the credits are for, which is how they find their way back.</param>
    /// <param name="at">The hold's own instant.</param>
    Task<bool> StageDrawAsync(
        string accountId, Guid holdId, decimal credits, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a hold's draws: <paramref name="spent"/> of what it took is kept, in the order it was taken, and the rest
    /// goes back to the grants it came from. Stages; does not save. Idempotent.
    /// </summary>
    /// <param name="spent">
    /// What the hold's charge took from grants, at most what it drew from them; nought for a hold released or lapsed.
    /// </param>
    Task StageResolveAsync(
        string accountId, Guid holdId, decimal spent, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountCreditSpendFacade"/>
internal sealed class AccountCreditSpendFacade(IAccountCreditSpendBusiness business) : IAccountCreditSpendFacade
{
    public Task<decimal> BalanceAsync(string accountId, CancellationToken cancellationToken) =>
        business.BalanceAsync(accountId, cancellationToken);

    public Task<bool> StageDrawAsync(
        string accountId, Guid holdId, decimal credits, DateTimeOffset at, CancellationToken cancellationToken) =>
        business.StageDrawAsync(accountId, holdId, credits, at, cancellationToken);

    public Task StageResolveAsync(
        string accountId, Guid holdId, decimal spent, DateTimeOffset at, CancellationToken cancellationToken) =>
        business.StageResolveAsync(accountId, holdId, spent, at, cancellationToken);
}
