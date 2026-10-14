using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Dashboard.Managers;

/// <summary>Bounds on the dashboard.</summary>
public static class DashboardPolicy
{
    /// <summary>The most rows any list on the dashboard has, however much the workspace holds.</summary>
    public const int ListLimit = 5;

    /// <summary>How far ahead "upcoming" looks, counting today: today and the thirteen days after it.</summary>
    public const int UpcomingDays = 14;

    /// <summary>The most readiness findings named per recipe. The count says how many there are in all.</summary>
    public const int BlockersPerRecipe = 3;
}

/// <summary>Whether one section of the dashboard could be read.</summary>
public enum DashboardSectionStatus
{
    Ready = 1,

    /// <summary>Its source could not be read just now. The rest of the dashboard is unaffected.</summary>
    Unavailable = 2,
}

/// <summary>
/// One part of the dashboard: its status, and what it says when it could be read.
/// </summary>
/// <param name="Data">Present exactly when <paramref name="Status"/> is <see cref="DashboardSectionStatus.Ready"/>.</param>
public sealed record DashboardSection<T>(DashboardSectionStatus Status, T? Data)
    where T : class
{
    public static DashboardSection<T> Ready(T data) => new(DashboardSectionStatus.Ready, data);

    public static DashboardSection<T> Unavailable() => new(DashboardSectionStatus.Unavailable, null);
}

/// <summary>One readiness rule a recipe does not yet meet, in the readiness evaluation's own words.</summary>
public sealed record DashboardBlockerServiceModel(string RuleId, string Summary);

/// <summary>A recipe in progress that readiness says cannot be approved yet.</summary>
/// <param name="BlockerCount">Every blocker the evaluation found; <paramref name="Blockers"/> names the first few.</param>
public sealed record DashboardBlockedRecipeServiceModel(
    Guid RecipeId, string Title, int BlockerCount, IReadOnlyList<DashboardBlockerServiceModel> Blockers);

/// <summary>
/// Readiness blockers among the recipes the dashboard lists as in progress. Only those recipes are evaluated —
/// at most <see cref="DashboardPolicy.ListLimit"/> — so this is never a claim about the rest.
/// </summary>
/// <param name="Evaluated">How many of the listed recipes were actually checked. One whose check failed is not among them.</param>
/// <param name="Unevaluated">How many of those could not be evaluated just now, and so are not listed either way.</param>
/// <param name="Blocked">Those with at least one blocker, in the order the in-progress list gives them.</param>
public sealed record DashboardReadinessServiceModel(
    int Evaluated, int Unevaluated, IReadOnlyList<DashboardBlockedRecipeServiceModel> Blocked);

/// <summary>One proposal awaiting a decision, with its recipe's title when it is about one the workspace can see.</summary>
public sealed record DashboardAiProposalServiceModel(
    Guid OperationId, AiTaskType TaskType, Guid? RecipeId, string? RecipeTitle, DateTimeOffset ProposedAt);

public sealed record DashboardAiProposalsServiceModel(int AwaitingDecision, IReadOnlyList<DashboardAiProposalServiceModel> Recent);

/// <summary>A publishing record as the dashboard lists it, with its card's title when it has a card.</summary>
public sealed record DashboardPublishingItemServiceModel(
    Guid Id,
    Guid? ContentCardId,
    string? ContentCardTitle,
    PublishingPlatform Platform,
    PublishingRecordStatus Status,
    DateTimeOffset ScheduledAt);

/// <summary>What is coming up in the window: cards planned for a day in it, and posts scheduled to go out in it.</summary>
/// <param name="From">The first day of the window, in the workspace's own zone.</param>
/// <param name="Through">The last day of the window, in the workspace's own zone.</param>
public sealed record DashboardUpcomingServiceModel(
    DateOnly From,
    DateOnly Through,
    int CardCount,
    IReadOnlyList<UpcomingContentCardServiceModel> Cards,
    int PostCount,
    IReadOnlyList<DashboardPublishingItemServiceModel> Posts);

/// <summary>The board's four columns and their total.</summary>
public sealed record DashboardBoardServiceModel(int Total, IReadOnlyList<ContentCardColumnCountServiceModel> Columns);

public sealed record DashboardRecentUploadsServiceModel(IReadOnlyList<RecentMediaUploadServiceModel> Items);

public sealed record DashboardPublishingServiceModel(
    IReadOnlyList<PublishingStatusCountServiceModel> Counts,
    int NeedsAttentionCount,
    IReadOnlyList<DashboardPublishingItemServiceModel> NeedsAttention);

/// <summary>
/// The workspace at a glance (13.11, HOME-UI-001/002): a read-only projection over what each module already
/// holds. Nothing here is stored, and nothing here is a second account of anything.
/// </summary>
/// <param name="GeneratedAt">When it was read (UTC). It is not cached, so this is now.</param>
/// <param name="TimeZoneId">The zone "today" was worked out in: the brand profile's, or null for UTC when it has none.</param>
/// <param name="Today">Today, in that zone.</param>
public sealed record DashboardServiceModel(
    DateTimeOffset GeneratedAt,
    string? TimeZoneId,
    DateOnly Today,
    DashboardSection<RecipeWorkSummaryServiceModel> RecipesInProgress,
    DashboardSection<DashboardReadinessServiceModel> ReadinessBlockers,
    DashboardSection<DashboardAiProposalsServiceModel> AiProposals,
    DashboardSection<TestFollowUpSummaryServiceModel> TestFollowUps,
    DashboardSection<DashboardUpcomingServiceModel> Upcoming,
    DashboardSection<DashboardBoardServiceModel> Board,
    DashboardSection<DashboardRecentUploadsServiceModel> RecentUploads,
    DashboardSection<DashboardPublishingServiceModel> Publishing);
