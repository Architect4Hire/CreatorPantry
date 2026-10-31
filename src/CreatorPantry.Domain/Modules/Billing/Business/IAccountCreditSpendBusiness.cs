using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>
/// Spending an account's grants after its allowance, and giving back what a hold did not spend (PRICE-008, B-50,
/// AP.5.2).
/// </summary>
internal interface IAccountCreditSpendBusiness
{
    /// <inheritdoc cref="Facade.IAccountCreditSpendFacade.BalanceAsync"/>
    Task<decimal> BalanceAsync(string accountId, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IAccountCreditSpendFacade.StageDrawAsync"/>
    Task<bool> StageDrawAsync(
        string accountId, Guid holdId, decimal credits, DateTimeOffset at, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IAccountCreditSpendFacade.StageResolveAsync"/>
    Task StageResolveAsync(
        string accountId, Guid holdId, decimal spent, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountCreditSpendBusiness"/>
internal sealed class AccountCreditSpendBusiness(IAccountCreditSpendDataLayer data) : IAccountCreditSpendBusiness
{
    public async Task<decimal> BalanceAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return (await data.ListRemainingAsync(accountId, cancellationToken)).Sum();
    }

    /// <remarks>
    /// <para>
    /// <strong>Oldest first, whatever the source</strong> (AP.5.2): a pack bought, an operator's grant and a
    /// promotion are spent in the order they were granted, the id breaking a tie. Nothing expires (B-50), so the
    /// order only decides what a later claw-back finds unspent — and first in, first out leaves the newest grant,
    /// the one a refund is likeliest to be about, whole for longest.
    /// </para>
    /// <para>
    /// <strong>All or none.</strong> The total is judged before anything is touched, so a refusal stages nothing.
    /// </para>
    /// </remarks>
    public async Task<bool> StageDrawAsync(
        string accountId, Guid holdId, decimal credits, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credits);

        var standing = (await data.ListStandingForUpdateAsync(accountId, cancellationToken))
            .Where(grant => grant.CreditsRemaining > 0m)
            .ToList();

        if (standing.Sum(grant => grant.CreditsRemaining) < credits)
        {
            return false;
        }

        var left = credits;

        foreach (var grant in standing)
        {
            var take = Math.Min(left, grant.CreditsRemaining);

            grant.CreditsRemaining -= take;
            data.StageDraw(new AccountCreditGrantDraw
            {
                Id = Guid.NewGuid(),
                GrantId = grant.Id,
                AccountId = accountId,
                HoldId = holdId,
                CreditsHeld = take,
                HeldAt = at,
            });

            left -= take;

            if (left == 0m)
            {
                break;
            }
        }

        return true;
    }

    /// <remarks>
    /// <para>
    /// <strong>The spend is kept in the order the credits were taken</strong>, so the oldest grant keeps first and
    /// the newest gets back first — what admission would have taken had it known the cost.
    /// </para>
    /// <para>
    /// <strong>A grant clawed back while the hold was open never has a balance again</strong> (B-50): what it gets
    /// back joins its claw-back, and the draw records that it did. What the hold spent stays spent; the credits were
    /// the account's when it was admitted.
    /// </para>
    /// <para>
    /// Idempotent: a hold whose draws have resolved has none open, and a hold that took nothing from grants never
    /// had any, so both write nothing.
    /// </para>
    /// </remarks>
    public async Task StageResolveAsync(
        string accountId, Guid holdId, decimal spent, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentOutOfRangeException.ThrowIfNegative(spent);

        var draws = await data.ListOpenDrawsForUpdateAsync(accountId, holdId, cancellationToken);

        if (draws.Count == 0)
        {
            return;
        }

        // An invariant, not a branch: the caller lands anything a run cost beyond its hold on the allowance, never
        // on a grant, so asking a grant to keep more than it gave is a broken caller rather than a charge to make.
        if (spent > draws.Sum(draw => draw.CreditsHeld))
        {
            throw new ArgumentOutOfRangeException(
                nameof(spent),
                spent,
                $"Hold {holdId} took {draws.Sum(draw => draw.CreditsHeld)} credits from grants and cannot spend more of them.");
        }

        var left = spent;

        foreach (var draw in draws)
        {
            var kept = Math.Min(left, draw.CreditsHeld);
            var back = draw.CreditsHeld - kept;
            var grant = draw.Grant!;

            left -= kept;

            if (grant.ClawedBackAt is null)
            {
                grant.CreditsRemaining += back;
            }
            else
            {
                grant.CreditsClawedBack += back;
                draw.CreditsClawedBack = back;
            }

            draw.CreditsSpent = kept;
            draw.ResolvedAt = at;
        }
    }
}
