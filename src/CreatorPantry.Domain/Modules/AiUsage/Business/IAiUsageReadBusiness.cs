using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>Assembles an account's own view of what it has spent (USAGE-008).</summary>
internal interface IAiUsageReadBusiness
{
    Task<AccountAiUsageServiceModel> GetCurrentAsync(string accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> GetHistoryAsync(
        string accountId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReadBusiness"/>
internal sealed class AiUsageReadBusiness(
    IAiUsageReadRepository usage,
    IAiQuotaPeriodResolver periods,
    IWorkspaceFacade workspaces,
    IClock clock) : IAiUsageReadBusiness
{
    public async Task<AccountAiUsageServiceModel> GetCurrentAsync(
        string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var now = clock.UtcNow;
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        // Projected, never opened. A read that wrote a row would be a surprising thing for a GET to do, and an
        // account that has never run anything is entitled to see its allowance without spending a row to.
        var period = await periods.ProjectAsync(accountId, terms, now, cancellationToken);

        var byTask = await usage.SumByTaskAsync(
            accountId, period.Id, period.StartsAt, period.EndsAt, cancellationToken);

        var byWorkspace = await usage.SumByWorkspaceAsync(
            accountId, period.Id, period.StartsAt, period.EndsAt, cancellationToken);

        return new AccountAiUsageServiceModel(
            Describe(period, terms.PeriodLength),
            terms.IsSuspended,
            [.. byTask
                .Select(row => new AccountAiUsageTaskTotalServiceModel(row.TaskType, row.Amount, row.Requests))
                .OrderByDescending(row => row.Amount)
                .ThenByDescending(row => row.Requests)
                .ThenBy(row => row.TaskType)],
            await NameAsync(accountId, byWorkspace, cancellationToken));
    }

    /// <remarks>
    /// Closed periods only: the running one is what <see cref="GetCurrentAsync"/> is for, and repeating it here
    /// would show a creator the same period twice with two different totals a moment apart.
    /// </remarks>
    public async Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> GetHistoryAsync(
        string accountId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        // The running period is excluded by the query rather than here, so the limit buys the caller that many
        // closed periods rather than that many rows one of which they cannot see.
        var rows = await usage.FindClosedPeriodsAsync(
            accountId,
            clock.UtcNow,
            Math.Clamp(limit, 1, AiUsagePolicy.HistoryMaxPeriods),
            cancellationToken);

        // The length comes from the terms in force rather than from each row, because a period stores the
        // boundaries it was opened with and not the rule that produced them. An account whose length changed
        // therefore reads its old periods under today's label -- which is wrong in a way that only a stored
        // length could fix, and is noted here rather than papered over.
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        return [.. rows.Select(period => Describe(period, terms.PeriodLength))];
    }

    /// <summary>
    /// Attaches a name to each workspace the account still belongs to, and withholds it from the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The total stays and the name goes</strong> (USAGE-008). The spend is the creator's own history
    /// and removing it would misreport what they used; the name is a fact about the workspace rather than
    /// about their usage, and somebody who has left is no longer entitled to it.
    /// </para>
    /// <para>
    /// <strong>Active membership, not merely a membership row.</strong> A row whose status is <c>Removed</c>
    /// is exactly the revoked case this rule is about, and <c>Invited</c> is somebody who has not accepted —
    /// neither is current access, so neither discloses a name.
    /// </para>
    /// <para>
    /// The membership set comes from the tenancy module's own facade, which is the same call
    /// <c>GET /api/v1/me</c> makes. This module therefore adds no cross-workspace query of its own: it asks who
    /// the caller belongs to and withholds everything else.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<AccountAiUsageWorkspaceTotalServiceModel>> NameAsync(
        string accountId,
        IReadOnlyList<AiUsageWorkspaceTotal> totals,
        CancellationToken cancellationToken)
    {
        if (totals.Count == 0)
        {
            return [];
        }

        var visible = (await workspaces.GetMyMembershipsAsync(accountId, cancellationToken))
            .Where(membership => membership.Status is WorkspaceMembershipStatus.Active)
            .ToDictionary(membership => membership.WorkspaceId, membership => membership.WorkspaceName);

        return
        [
            .. totals
                .Select(row => new AccountAiUsageWorkspaceTotalServiceModel(
                    row.WorkspaceId,
                    row.WorkspaceId is { } id && visible.TryGetValue(id, out var name) ? name : null,
                    row.Amount,
                    row.Requests))
                .OrderByDescending(row => row.Amount)
                .ThenByDescending(row => row.Requests)
                .ThenBy(row => row.WorkspaceId),
        ];
    }

    private static AccountAiUsagePeriodServiceModel Describe(
        AccountAiQuotaPeriod period, AiQuotaPeriodLength length) =>
        new(period.Unit,

            // The spendable total, which is what a quota refusal publishes under this name too. One word, one
            // number, across every payload about the same period.
            period.Allowance + period.CarriedOver,
            period.CarriedOver,
            period.Consumed,
            period.Reserved,

            // From the shared calculator, so the balance a creator is shown is the one an admission would then
            // judge their next request against.
            AiQuotaPeriodCalculator.Remaining(
                period.Allowance, period.CarriedOver, period.Consumed, period.Reserved),
            length,
            period.TimeZoneId,
            period.StartsAt,
            period.EndsAt);
}
