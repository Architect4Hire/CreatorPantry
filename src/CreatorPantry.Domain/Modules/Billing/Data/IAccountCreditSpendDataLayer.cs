using CreatorPantry.Domain.Modules.Billing.Data.Entities;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>Composes the reads and staged writes that spend an account's grants (AP.5.2).</summary>
/// <remarks>
/// <para>
/// <strong>Stages, and owns no transaction</strong> — the departure from backend.md that
/// <c>IAiQuotaDataLayer.StageSettlementAsync</c> makes, for the reason AP.5.2 states: a hold split across the
/// allowance and grants commits atomically. The debits and draws are staged on the request's unit of work and the
/// AiUsage data layer's save commits them with the hold and the period, or not at all.
/// </para>
/// <para>
/// <strong>Contention is the caller's to retry.</strong> Every grant this changes carries a row version, so a writer
/// that read a balance somebody has since moved — another admission, a claw-back — fails the shared save; the caller
/// forgets everything tracked, including what was staged here, and asks again from a fresh read.
/// </para>
/// </remarks>
internal interface IAccountCreditSpendDataLayer
{
    /// <inheritdoc cref="IAccountCreditSpendRepository.ListStandingTrackedAsync"/>
    Task<IReadOnlyList<AccountCreditGrant>> ListStandingForUpdateAsync(string accountId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IAccountCreditSpendRepository.ListOpenDrawsTrackedAsync"/>
    Task<IReadOnlyList<AccountCreditGrantDraw>> ListOpenDrawsForUpdateAsync(
        string accountId, Guid holdId, CancellationToken cancellationToken);

    /// <summary>The unspent credits on each of the account's grants.</summary>
    Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Stages a draw. Does not save.</summary>
    void StageDraw(AccountCreditGrantDraw draw);
}

/// <inheritdoc cref="IAccountCreditSpendDataLayer"/>
internal sealed class AccountCreditSpendDataLayer(
    IAccountCreditSpendRepository spends, IAccountCreditGrantRepository grants) : IAccountCreditSpendDataLayer
{
    public Task<IReadOnlyList<AccountCreditGrant>> ListStandingForUpdateAsync(
        string accountId, CancellationToken cancellationToken) =>
        spends.ListStandingTrackedAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<AccountCreditGrantDraw>> ListOpenDrawsForUpdateAsync(
        string accountId, Guid holdId, CancellationToken cancellationToken) =>
        spends.ListOpenDrawsTrackedAsync(accountId, holdId, cancellationToken);

    public Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken) =>
        grants.ListRemainingAsync(accountId, cancellationToken);

    public void StageDraw(AccountCreditGrantDraw draw) => spends.AddDraw(draw);
}
