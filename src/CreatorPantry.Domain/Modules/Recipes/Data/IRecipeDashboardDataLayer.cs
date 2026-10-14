using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// The bounded reads a dashboard summary of this module makes (13.11). Every one is a count grouped in the
/// database or a list capped at <see cref="RecipeDashboardPolicy.ListLimit"/>, so its cost does not grow with
/// the workspace, and every one runs under the workspace query filter.
/// </summary>
public interface IRecipeDashboardRepository
{
    /// <summary>Recipes per in-progress state. A state with none is absent.</summary>
    Task<IReadOnlyList<RecipeStatusCountServiceModel>> CountInProgressAsync(CancellationToken cancellationToken);

    /// <summary>The most recently changed in-progress recipes. Served by <c>IX_Recipes_Workspace_Status_Updated</c>.</summary>
    Task<IReadOnlyList<RecipeInProgressServiceModel>> ListRecentInProgressAsync(CancellationToken cancellationToken);

    /// <summary>Unresolved test issues per severity. A severity with none is absent.</summary>
    Task<IReadOnlyDictionary<TestIssueSeverity, int>> CountOpenIssuesAsync(CancellationToken cancellationToken);

    /// <summary>Unresolved test issues, most severe first, then most recently tested.</summary>
    Task<IReadOnlyList<TestFollowUpServiceModel>> ListOpenIssuesAsync(CancellationToken cancellationToken);
}

internal sealed class RecipeDashboardRepository(CreatorPantryDbContext context) : IRecipeDashboardRepository
{
    public async Task<IReadOnlyList<RecipeStatusCountServiceModel>> CountInProgressAsync(CancellationToken cancellationToken) =>
        await context.Recipes.AsNoTracking()
            .Where(recipe => RecipeDashboardPolicy.InProgress.Contains(recipe.Status))
            .GroupBy(recipe => recipe.Status)
            .Select(group => new RecipeStatusCountServiceModel(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RecipeInProgressServiceModel>> ListRecentInProgressAsync(CancellationToken cancellationToken) =>
        await context.Recipes.AsNoTracking()
            .Where(recipe => RecipeDashboardPolicy.InProgress.Contains(recipe.Status))
            .OrderByDescending(recipe => recipe.UpdatedAt)
            .ThenByDescending(recipe => recipe.Id)
            .Take(RecipeDashboardPolicy.ListLimit)
            .Select(recipe => new RecipeInProgressServiceModel(recipe.Id, recipe.Title, recipe.Status, recipe.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<TestIssueSeverity, int>> CountOpenIssuesAsync(CancellationToken cancellationToken) =>
        await OpenIssues()
            .GroupBy(issue => issue.Severity)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

    public async Task<IReadOnlyList<TestFollowUpServiceModel>> ListOpenIssuesAsync(CancellationToken cancellationToken) =>
        await OpenIssues()
            .Join(context.RecipeTestRuns, issue => issue.RecipeTestRunId, run => run.Id, (issue, run) => new { issue, run })
            .Join(context.Recipes, row => row.issue.RecipeId, recipe => recipe.Id, (row, recipe) => new { row.issue, row.run, recipe })
            .OrderByDescending(row => row.issue.Severity)
            .ThenByDescending(row => row.run.TestedAt)
            .ThenBy(row => row.issue.Id)
            .Take(RecipeDashboardPolicy.ListLimit)
            .Select(row => new TestFollowUpServiceModel(
                row.issue.Id,
                row.run.Id,
                row.recipe.Id,
                row.recipe.Title,
                row.issue.Title,
                row.issue.Severity,
                row.run.TestedAt))
            .ToListAsync(cancellationToken);

    /// <summary>An issue is open until it has a resolution: one row, unique per issue, which is what is asked.</summary>
    private IQueryable<Entities.TestIssue> OpenIssues() =>
        context.TestIssues.AsNoTracking()
            .Where(issue => !context.TestIssueResolutions.Any(resolution => resolution.TestIssueId == issue.Id));
}

/// <summary>The recipe module's dashboard reads, composed.</summary>
public interface IRecipeDashboardDataLayer
{
    Task<RecipeWorkSummaryServiceModel> GetWorkAsync(CancellationToken cancellationToken);

    Task<TestFollowUpSummaryServiceModel> GetTestFollowUpsAsync(CancellationToken cancellationToken);
}

internal sealed class RecipeDashboardDataLayer(IRecipeDashboardRepository repository) : IRecipeDashboardDataLayer
{
    public async Task<RecipeWorkSummaryServiceModel> GetWorkAsync(CancellationToken cancellationToken)
    {
        var counts = await repository.CountInProgressAsync(cancellationToken);
        var recent = await repository.ListRecentInProgressAsync(cancellationToken);

        // Every in-progress state, in the order work moves through them, zero where there are none: a state
        // missing from the answer would read as "not a state", not as "nothing there".
        return new RecipeWorkSummaryServiceModel(
            [.. RecipeDashboardPolicy.InProgress.Select(status =>
                new RecipeStatusCountServiceModel(status, counts.FirstOrDefault(count => count.Status == status)?.Count ?? 0))],
            recent);
    }

    public async Task<TestFollowUpSummaryServiceModel> GetTestFollowUpsAsync(CancellationToken cancellationToken)
    {
        var counts = await repository.CountOpenIssuesAsync(cancellationToken);
        var recent = await repository.ListOpenIssuesAsync(cancellationToken);

        int Of(TestIssueSeverity severity) => counts.TryGetValue(severity, out var count) ? count : 0;

        return new TestFollowUpSummaryServiceModel(
            counts.Values.Sum(),
            Of(TestIssueSeverity.Blocking),
            Of(TestIssueSeverity.Major),
            Of(TestIssueSeverity.Minor),
            recent);
    }
}
