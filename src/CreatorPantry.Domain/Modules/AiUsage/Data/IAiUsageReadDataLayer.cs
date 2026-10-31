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

    /// <summary>Whether the account is told when its credits run low (B-59). True when it has never chosen.</summary>
    Task<bool> IsLowCreditNoticeEnabledAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// The account's pictures that still count, by the query admission counts them with (DL.9). A snapshot read
    /// without a hold, as a quote reads it.
    /// </summary>
    Task<AccountImageCounts> CountImagesAsync(
        string accountId, AccountImageWindows images, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageReadDataLayer"/>
internal sealed class AiUsageReadDataLayer(
    IAiUsageReadRepository usage, IAiNoticeSettingRepository settings, IAiQuotaRepository quotas)
    : IAiUsageReadDataLayer
{
    public Task<AccountImageCounts> CountImagesAsync(
        string accountId, AccountImageWindows images, CancellationToken cancellationToken) =>
        quotas.CountImagesAsync(accountId, images, cancellationToken);

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

    /// <remarks>
    /// No row means on: a notice is not a charge and needs no opt-in (B-59), so an account that has never had
    /// an opinion gets the default without one having been written for it.
    /// </remarks>
    public async Task<bool> IsLowCreditNoticeEnabledAsync(
        string accountId, CancellationToken cancellationToken) =>
        (await settings.FindAsync(accountId, cancellationToken))?.LowCreditNoticeEnabled ?? true;
}
