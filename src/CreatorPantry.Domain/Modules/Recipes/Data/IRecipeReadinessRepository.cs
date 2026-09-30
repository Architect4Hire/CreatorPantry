using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// The purpose-built read behind a readiness evaluation: every fact the rules need from this module, in as few
/// statements as they can be had in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IRecipeRepository"/> on purpose</strong>, for the reason
/// <see cref="IRecipeSearchRepository"/> is: that interface returns the recipe aggregate, tracked, because its
/// callers are about to write. Nothing here materialises an aggregate or tracks a row — it projects the shapes the
/// evaluator consumes. Loading the whole recipe with every child would read instruction text, equipment and asset
/// captions to answer questions that are counts.
/// </para>
/// <para>
/// <strong>Read-only, and there is nothing here that could write.</strong> TESTRUN-004's "evaluation writes
/// nothing" is kept structurally: no <c>Add</c>, no <c>SaveChanges</c>, no audit writer, no clock.
/// </para>
/// <para>
/// <strong>No <c>WorkspaceId</c> predicate anywhere.</strong> Every table touched — <c>Recipes</c>,
/// <c>RecipeVersions</c>, <c>RecipeIngredients</c>, <c>RecipeInstructionSteps</c>, <c>RecipeAssetLinks</c>,
/// <c>RecipeTags</c>, <c>RecipeTestRuns</c>, <c>TestIssues</c>, <c>TestIssueResolutions</c> — is workspace-owned,
/// so the global query filter scopes all of it, and writing one by hand would suggest the filter is optional.
/// </para>
/// </remarks>
public interface IRecipeReadinessRepository
{
    /// <summary>
    /// Every fact the readiness rules read about one recipe, or <c>null</c> when it is not visible.
    /// </summary>
    /// <returns>
    /// Null covers both "no such recipe" and "another workspace's recipe" — deliberately indistinguishable, so
    /// this read cannot be used to probe for a neighbour's recipe ids (tenancy.md).
    /// </returns>
    Task<RecipeReadinessFacts?> FindFactsAsync(Guid recipeId, CancellationToken cancellationToken);
}

internal sealed class RecipeReadinessRepository(CreatorPantryDbContext context) : IRecipeReadinessRepository
{
    public async Task<RecipeReadinessFacts?> FindFactsAsync(Guid recipeId, CancellationToken cancellationToken)
    {
        // The recipe, its scalar facts, its counts, and the latest version — one statement, because every part of
        // it is either a column of Recipes or a scalar subquery over an indexed child. Never tracked: nothing on
        // this path writes.
        var head = await context.Recipes.AsNoTracking()
            .Where(recipe => recipe.Id == recipeId)
            .Select(recipe => new
            {
                recipe.Id,
                recipe.RowVersion,
                recipe.Title,
                recipe.Description,
                recipe.AttributionText,
                recipe.SourceUrl,
                recipe.CuisineId,
                recipe.CourseId,
                recipe.PrepTimeMinutes,
                recipe.CookTimeMinutes,
                recipe.RestTimeMinutes,
                recipe.TotalTimeMinutes,
                recipe.YieldText,
                recipe.YieldQuantity,
                recipe.YieldUnitId,
                recipe.ServingCount,
                TagCount = context.RecipeTags.Count(tag => tag.RecipeId == recipe.Id),
                InstructionStepCount =
                    context.RecipeInstructionSteps.Count(step => step.RecipeId == recipe.Id),

                // The hero link's id as well as its existence, so a finding can point at the link rather than only
                // at the recipe when one is there but something else about it is wrong later.
                HeroAssetLinkId = context.RecipeAssetLinks
                    .Where(link => link.RecipeId == recipe.Id && link.Role == RecipeAssetRole.Hero)
                    .OrderBy(link => link.SortOrder)
                    .Select(link => (Guid?)link.Id)
                    .FirstOrDefault(),

                // By number, not by CreatedAt: the number is the gap-free identity creators cite, and two versions
                // written in the same tick would make a timestamp ordering arbitrary.
                LatestVersionId = context.RecipeVersions
                    .Where(version => version.RecipeId == recipe.Id)
                    .OrderByDescending(version => version.VersionNumber)
                    .Select(version => (Guid?)version.Id)
                    .FirstOrDefault(),
                LatestVersionNumber = context.RecipeVersions
                    .Where(version => version.RecipeId == recipe.Id)
                    .OrderByDescending(version => version.VersionNumber)
                    .Select(version => (int?)version.VersionNumber)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (head is null)
        {
            return null;
        }

        // The lines, because three rules need them individually rather than counted: two report which line, and
        // the allergen question is asked about the ids they carry.
        var lines = await context.RecipeIngredients.AsNoTracking()
            .Where(line => line.RecipeId == recipeId)
            .OrderBy(line => line.SortOrder)
            .Select(line => new RecipeReadinessIngredientLine(
                line.Id, line.DisplayText, line.MatchStatus, line.IngredientId))
            .ToListAsync(cancellationToken);

        var latestVersionId = head.LatestVersionId;

        // Everything about testing is scoped to the version being evaluated, which is the whole point of the
        // rules: a test of an earlier version is evidence about words that have since changed.
        var testedCurrentVersion = latestVersionId is not null
            && await context.RecipeTestRuns.AsNoTracking()
                .AnyAsync(run => run.RecipeVersionId == latestVersionId, cancellationToken);

        // Most recently cooked, not most recently entered — the same ordering the test history uses, and for the
        // same reason: testers write their notes up days later.
        var latestTest = latestVersionId is null
            ? null
            : await context.RecipeTestRuns.AsNoTracking()
                .Where(run => run.RecipeVersionId == latestVersionId)
                .OrderByDescending(run => run.TestedAt)
                .ThenByDescending(run => run.Id)
                .Select(run => new { run.Id, run.Outcome })
                .FirstOrDefaultAsync(cancellationToken);

        // "Unresolved" is the absence of a resolution row and nothing else, which is the one meaning TestIssue
        // establishes. There is no flag here to disagree with it.
        var openIssues = latestVersionId is null
            ? []
            : await context.TestIssues.AsNoTracking()
                .Where(issue =>
                    context.RecipeTestRuns.Any(run =>
                        run.Id == issue.RecipeTestRunId && run.RecipeVersionId == latestVersionId)
                    && !context.TestIssueResolutions.Any(resolution => resolution.TestIssueId == issue.Id))
                .OrderBy(issue => issue.RecipeTestRunId)
                .ThenBy(issue => issue.SortOrder)
                .Select(issue => new RecipeReadinessOpenIssue(
                    issue.Id, issue.RecipeTestRunId, issue.Severity, issue.Title))
                .ToListAsync(cancellationToken);

        return new RecipeReadinessFacts
        {
            RecipeId = head.Id,
            RecipeRowVersion = head.RowVersion,
            EvaluatedVersionId = latestVersionId,
            EvaluatedVersionNumber = head.LatestVersionNumber,
            Title = head.Title,
            Description = head.Description,
            AttributionText = head.AttributionText,
            SourceUrl = head.SourceUrl,
            CuisineId = head.CuisineId,
            CourseId = head.CourseId,
            TagCount = head.TagCount,
            PrepTimeMinutes = head.PrepTimeMinutes,
            CookTimeMinutes = head.CookTimeMinutes,
            RestTimeMinutes = head.RestTimeMinutes,
            TotalTimeMinutes = head.TotalTimeMinutes,
            YieldText = head.YieldText,
            YieldQuantity = head.YieldQuantity,
            YieldUnitId = head.YieldUnitId,
            ServingCount = head.ServingCount,
            InstructionStepCount = head.InstructionStepCount,
            HasHeroAsset = head.HeroAssetLinkId is not null,
            HeroAssetLinkId = head.HeroAssetLinkId,
            TestedCurrentVersion = testedCurrentVersion,
            LatestTestOutcome = latestTest?.Outcome,
            LatestTestRunId = latestTest?.Id,
            IngredientLines = lines,
            OpenIssues = openIssues,
        };
    }
}
