using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Publishing.Data;

/// <summary>
/// The bounded reads the dashboard makes of the publishing ledger (13.11), all over
/// <c>IX_PublishingRecords_Workspace_Status_ScheduledAt</c>.
/// </summary>
public interface IPublishingDashboardRepository
{
    Task<IReadOnlyDictionary<PublishingRecordStatus, int>> CountByStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PublishingDashboardItemServiceModel>> ListInStatusAsync(
        IReadOnlyCollection<PublishingRecordStatus> statuses, int limit, CancellationToken cancellationToken);

    Task<int> CountScheduledAsync(
        IReadOnlyCollection<PublishingRecordStatus> statuses, DateTimeOffset from, DateTimeOffset before, CancellationToken cancellationToken);

    Task<IReadOnlyList<PublishingDashboardItemServiceModel>> ListScheduledAsync(
        IReadOnlyCollection<PublishingRecordStatus> statuses, DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken);
}

internal sealed class PublishingDashboardRepository(CreatorPantryDbContext context) : IPublishingDashboardRepository
{
    public async Task<IReadOnlyDictionary<PublishingRecordStatus, int>> CountByStatusAsync(CancellationToken cancellationToken) =>
        await context.PublishingRecords.AsNoTracking()
            .GroupBy(record => record.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

    public async Task<IReadOnlyList<PublishingDashboardItemServiceModel>> ListInStatusAsync(
        IReadOnlyCollection<PublishingRecordStatus> statuses, int limit, CancellationToken cancellationToken) =>
        await context.PublishingRecords.AsNoTracking()
            .Where(record => statuses.Contains(record.Status))
            .OrderByDescending(record => record.StatusChangedAt)
            .ThenByDescending(record => record.Id)
            .Take(limit)
            .Select(record => new PublishingDashboardItemServiceModel(
                record.Id, record.ContentCardId, record.Platform, record.Status, record.ScheduledAt))
            .ToListAsync(cancellationToken);

    public Task<int> CountScheduledAsync(
        IReadOnlyCollection<PublishingRecordStatus> statuses, DateTimeOffset from, DateTimeOffset before, CancellationToken cancellationToken) =>
        context.PublishingRecords.AsNoTracking()
            .CountAsync(record => statuses.Contains(record.Status) && record.ScheduledAt >= from && record.ScheduledAt < before, cancellationToken);

    public async Task<IReadOnlyList<PublishingDashboardItemServiceModel>> ListScheduledAsync(
        IReadOnlyCollection<PublishingRecordStatus> statuses, DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken) =>
        await context.PublishingRecords.AsNoTracking()
            .Where(record => statuses.Contains(record.Status) && record.ScheduledAt >= from && record.ScheduledAt < before)
            .OrderBy(record => record.ScheduledAt)
            .ThenBy(record => record.Id)
            .Take(limit)
            .Select(record => new PublishingDashboardItemServiceModel(
                record.Id, record.ContentCardId, record.Platform, record.Status, record.ScheduledAt))
            .ToListAsync(cancellationToken);
}

public interface IPublishingDashboardDataLayer
{
    Task<PublishingSummaryServiceModel> GetSummaryAsync(
        DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken);
}

internal sealed class PublishingDashboardDataLayer(IPublishingDashboardRepository repository) : IPublishingDashboardDataLayer
{
    /// <summary>What needs the creator: it failed, or its outcome could not be settled without them.</summary>
    private static readonly PublishingRecordStatus[] Attention = [PublishingRecordStatus.Failed, PublishingRecordStatus.NeedsAttention];

    /// <summary>Still on its way out: accepted by us or by the provider, not yet gone, not called back.</summary>
    private static readonly PublishingRecordStatus[] OnItsWay =
        [PublishingRecordStatus.Pending, PublishingRecordStatus.Submitting, PublishingRecordStatus.Scheduled];

    public async Task<PublishingSummaryServiceModel> GetSummaryAsync(
        DateTimeOffset from, DateTimeOffset before, int limit, CancellationToken cancellationToken)
    {
        var counts = await repository.CountByStatusAsync(cancellationToken);
        int Of(PublishingRecordStatus status) => counts.TryGetValue(status, out var count) ? count : 0;

        return new PublishingSummaryServiceModel(
            [.. Enum.GetValues<PublishingRecordStatus>().Order().Select(status => new PublishingStatusCountServiceModel(status, Of(status)))],
            Attention.Sum(Of),
            await repository.ListInStatusAsync(Attention, limit, cancellationToken),
            await repository.CountScheduledAsync(OnItsWay, from, before, cancellationToken),
            await repository.ListScheduledAsync(OnItsWay, from, before, limit, cancellationToken));
    }
}
