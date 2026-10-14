namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>How many publishing records are in one state.</summary>
public sealed record PublishingStatusCountServiceModel(PublishingRecordStatus Status, int Count);

/// <summary>
/// One publishing record, as the dashboard names it. Ids, a network, a state and a time: never the post's
/// words, a provider id or a provider's message.
/// </summary>
/// <param name="ContentCardId">The card it was published from, when it was.</param>
/// <param name="ScheduledAt">When it is, or was, to go out (UTC).</param>
public sealed record PublishingDashboardItemServiceModel(
    Guid Id, Guid? ContentCardId, PublishingPlatform Platform, PublishingRecordStatus Status, DateTimeOffset ScheduledAt);

/// <summary>Where the workspace's publishing stands.</summary>
/// <param name="Counts">Every state, each with its count — zero included.</param>
/// <param name="NeedsAttentionCount">Records that failed or need the creator: <c>Failed</c> and <c>NeedsAttention</c>.</param>
/// <param name="NeedsAttention">The most recently changed of those, at most the limit asked for.</param>
/// <param name="UpcomingCount">Posts scheduled to go out in the window and still on their way.</param>
/// <param name="Upcoming">The soonest of those, at most the limit asked for.</param>
public sealed record PublishingSummaryServiceModel(
    IReadOnlyList<PublishingStatusCountServiceModel> Counts,
    int NeedsAttentionCount,
    IReadOnlyList<PublishingDashboardItemServiceModel> NeedsAttention,
    int UpcomingCount,
    IReadOnlyList<PublishingDashboardItemServiceModel> Upcoming);
