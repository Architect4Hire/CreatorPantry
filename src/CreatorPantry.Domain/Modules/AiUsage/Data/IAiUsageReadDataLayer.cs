using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// Composes the account's own usage reads (USAGE-008) and owns nothing beyond passing them through — the seam
/// exists so <c>AiUsageReadBusiness</c> calls a DataLayer and never a repository (backend.md), matching every
/// other Business type in this module.
/// </summary>
internal interface IAiUsageReadDataLayer
{
    /// <summary>The account's periods that have ended, newest first.</summary>
    Task<IReadOnlyList<AccountAiQuotaPeriod>> FindClosedPeriodsAsync(
        string accountId, DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>What each capability charged in this period, and how many attempts it made.</summary>
    Task<IReadOnlyList<AiUsageTaskTotal>> SumByTaskAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>What work in each workspace charged in this period, and how many attempts it made.</summary>
    Task<IReadOnlyList<AiUsageWorkspaceTotal>> SumByWorkspaceAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReadDataLayer"/>
internal sealed class AiUsageReadDataLayer(IAiUsageReadRepository usage) : IAiUsageReadDataLayer
{
    public Task<IReadOnlyList<AccountAiQuotaPeriod>> FindClosedPeriodsAsync(
        string accountId, DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        usage.FindClosedPeriodsAsync(accountId, now, take, cancellationToken);

    public Task<IReadOnlyList<AiUsageTaskTotal>> SumByTaskAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) =>
        usage.SumByTaskAsync(accountId, periodId, from, to, cancellationToken);

    public Task<IReadOnlyList<AiUsageWorkspaceTotal>> SumByWorkspaceAsync(
        string accountId, Guid periodId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) =>
        usage.SumByWorkspaceAsync(accountId, periodId, from, to, cancellationToken);
}
