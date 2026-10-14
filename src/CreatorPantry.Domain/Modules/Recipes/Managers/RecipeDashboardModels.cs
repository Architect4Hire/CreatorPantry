namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Bounds on what a dashboard summary of this module reads.</summary>
public static class RecipeDashboardPolicy
{
    /// <summary>The most rows any one list in a summary returns, however much the workspace holds.</summary>
    public const int ListLimit = 5;

    /// <summary>The states a recipe is "in progress" in: being written, tested or reviewed, not yet settled.</summary>
    public static readonly IReadOnlyList<RecipeStatus> InProgress =
        [RecipeStatus.Draft, RecipeStatus.InDevelopment, RecipeStatus.Testing, RecipeStatus.ReadyForReview];
}

/// <summary>How many recipes are in one state.</summary>
public sealed record RecipeStatusCountServiceModel(RecipeStatus Status, int Count);

/// <summary>One recipe still being worked on. The title is the creator's; nothing else of the recipe is read.</summary>
public sealed record RecipeInProgressServiceModel(Guid Id, string Title, RecipeStatus Status, DateTimeOffset UpdatedAt);

/// <summary>The workspace's recipes in progress: how many in each state, and the most recently changed.</summary>
/// <param name="Counts">Every in-progress state, each with its count — zero included.</param>
/// <param name="Recent">At most <see cref="RecipeDashboardPolicy.ListLimit"/>, most recently changed first.</param>
public sealed record RecipeWorkSummaryServiceModel(
    IReadOnlyList<RecipeStatusCountServiceModel> Counts,
    IReadOnlyList<RecipeInProgressServiceModel> Recent);

/// <summary>One test issue nobody has resolved yet.</summary>
public sealed record TestFollowUpServiceModel(
    Guid IssueId,
    Guid TestRunId,
    Guid RecipeId,
    string RecipeTitle,
    string Title,
    TestIssueSeverity Severity,
    DateTimeOffset TestedAt);

/// <summary>The workspace's unresolved test issues: how many at each severity, and the ones to look at first.</summary>
/// <param name="Recent">At most <see cref="RecipeDashboardPolicy.ListLimit"/>: the most severe first, then the most recently tested.</param>
public sealed record TestFollowUpSummaryServiceModel(
    int Open,
    int Blocking,
    int Major,
    int Minor,
    IReadOnlyList<TestFollowUpServiceModel> Recent);
