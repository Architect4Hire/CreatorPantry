using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// An account's own view of its AI allowance and what it has gone on, across every workspace it works in
/// (USAGE-008).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Account-scoped, and deliberately not reachable from a workspace route.</strong> The answer spans
/// workspaces, so nesting it under <c>/workspaces/{slug}</c> would either narrow it to one — which is not the
/// question — or return another workspace's figures from a route that claims to be about this one.
/// </para>
/// <para>
/// <strong>The account is a parameter and never a request field.</strong> The caller passes the id from the
/// validated session; there is no route segment, body, query value or header anywhere in this feature that
/// could carry one. That is what makes "returns the caller's own account only" structural rather than checked.
/// </para>
/// </remarks>
public interface IAiUsageReadFacade
{
    /// <summary>
    /// The running period's totals and what they went on.
    /// </summary>
    /// <param name="accountId">
    /// From the validated session. Never a route segment, a body field, a query value or a header.
    /// </param>
    /// <remarks>
    /// Opens no period. An account that has never run anything sees its real boundaries and a full allowance,
    /// computed from the terms in force and thrown away.
    /// </remarks>
    Task<AccountAiUsageServiceModel> GetCurrentAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// The account's closed periods, newest first.
    /// </summary>
    /// <param name="limit">
    /// How many to return, clamped to <see cref="AiUsagePolicy.HistoryMaxPeriods"/>. A bounded list rather
    /// than a cursor: api-contract.md reserves cursors for large or changing datasets, and a monthly period
    /// yields twelve rows a year that never change once closed.
    /// </param>
    /// <inheritdoc cref="GetCurrentAsync" path="/param[@name='accountId']"/>
    Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> GetHistoryAsync(
        string accountId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReadFacade"/>
internal sealed class AiUsageReadFacade(IAiUsageReadBusiness business) : IAiUsageReadFacade
{
    public Task<AccountAiUsageServiceModel> GetCurrentAsync(
        string accountId, CancellationToken cancellationToken) =>
        business.GetCurrentAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> GetHistoryAsync(
        string accountId, int limit, CancellationToken cancellationToken) =>
        business.GetHistoryAsync(accountId, limit, cancellationToken);
}
