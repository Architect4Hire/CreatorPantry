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
    /// <summary>
    /// The versions whose content is the content being evaluated: the latest, plus the run of approval
    /// snapshots above the last version that actually changed something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not just "the latest version".</strong> It was, and that made the approval gate
    /// contradict itself. <c>RecipeVersion</c> is immutable, so an approval cannot mark the version it
    /// approved as ready — it has to <em>write</em> one, whose content is a byte-identical copy of its parent
    /// (<c>RecipeVersionSource.ReadinessApproval</c>). Keyed on the latest id alone,
    /// <c>recipe.testing.currentVersionUntested</c> then reported the approval's own snapshot as untested, so
    /// a recipe that was approved, reopened and advanced again could not be approved a second time until
    /// somebody recorded a test of words they had already tested.
    /// </para>
    /// <para>
    /// The rule always meant the words rather than the row — its own summary is "somebody has cooked the
    /// version as it now stands", and an approval snapshot <em>is</em> the version as it stands. This is what
    /// makes that true.
    /// </para>
    /// <para>
    /// <strong>Found by version number rather than by walking parents.</strong> Every version above the last
    /// non-approval one is an approval snapshot, and each copies its parent, so all of them carry that
    /// version's content — which makes "number at or above the last content version" the whole set, in one
    /// scalar read and one projection rather than a chain of lookups with a depth nobody can bound.
    /// </para>
    /// <para>
    /// A recipe with no versions answers empty, and the testing rules then report what
    /// <see cref="RecipeReadinessFacts.EvaluatedVersionId"/> being null already says.
    /// </para>
    /// </remarks>
    private async Task<List<Guid>> CurrentContentVersionIdsAsync(
        Guid recipeId,
        int? latestVersionNumber,
        CancellationToken cancellationToken)
    {
        if (latestVersionNumber is null)
        {
            return [];
        }

        // Version 1 is always a create, so this is never null for a recipe that has any version at all. Read
        // as nullable anyway rather than assumed: a Max over an empty set throws, and the honest reading of
        // "no content version" is the conservative one below.
        var contentVersionNumber = await context.RecipeVersions.AsNoTracking()
            .Where(version =>
                version.RecipeId == recipeId
                && version.Source != RecipeVersionSource.ReadinessApproval)
            .MaxAsync(version => (int?)version.VersionNumber, cancellationToken);

        return await context.RecipeVersions.AsNoTracking()
            .Where(version =>
                version.RecipeId == recipeId
                && version.VersionNumber >= (contentVersionNumber ?? latestVersionNumber))
            .Select(version => version.Id)
            .ToListAsync(cancellationToken);
    }

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

        // Every version whose content is the content being evaluated — usually just the latest, and more than
        // one once an approval has written a snapshot of words nobody changed.
        var currentContentVersionIds = await CurrentContentVersionIdsAsync(
            recipeId, head.LatestVersionNumber, cancellationToken);

        // Everything about testing is scoped to those versions, which is the whole point of the rules: a test
        // of an *earlier* version is evidence about words that have since changed, and a test of an identical
        // version is evidence about these exact words.
        var testedCurrentVersion = currentContentVersionIds.Count > 0
            && await context.RecipeTestRuns.AsNoTracking()
                .AnyAsync(run => currentContentVersionIds.Contains(run.RecipeVersionId), cancellationToken);

        // Most recently cooked, not most recently entered — the same ordering the test history uses, and for the
        // same reason: testers write their notes up days later.
        var latestTest = currentContentVersionIds.Count == 0
            ? null
            : await context.RecipeTestRuns.AsNoTracking()
                .Where(run => currentContentVersionIds.Contains(run.RecipeVersionId))
                .OrderByDescending(run => run.TestedAt)
                .ThenByDescending(run => run.Id)
                .Select(run => new { run.Id, run.Outcome })
                .FirstOrDefaultAsync(cancellationToken);

        // "Unresolved" is the absence of a resolution row and nothing else, which is the one meaning TestIssue
        // establishes. There is no flag here to disagree with it.
        var openIssues = currentContentVersionIds.Count == 0
            ? []
            : await context.TestIssues.AsNoTracking()
                .Where(issue =>
                    context.RecipeTestRuns.Any(run =>
                        run.Id == issue.RecipeTestRunId
                        && currentContentVersionIds.Contains(run.RecipeVersionId))
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
