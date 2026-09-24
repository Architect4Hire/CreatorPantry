using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

internal sealed class RecipeSearchRepository(CreatorPantryDbContext context) : IRecipeSearchRepository
{
    public async Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore)> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var fetched = await Project(Ordered(Positioned(Filtered(criteria), criteria), criteria), criteria.Sort)
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public Task<int> CountAsync(RecipeSearchCriteria criteria, CancellationToken cancellationToken) =>
        // Filtered, and deliberately neither positioned nor limited: a count of what follows the current page
        // is not a total, and a count capped at the page size is not one either.
        Filtered(criteria).CountAsync(cancellationToken);

    /// <summary>
    /// Every filter, and nothing else — no ordering, no keyset, no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place the filters are expressed, shared by the page and the count. Two copies would be two
    /// predicates that had to stay identical, and the first time they diverged the product would report a total
    /// for a set it had not paged.
    /// </para>
    /// <para>
    /// No <c>WorkspaceId</c> predicate anywhere below. The global query filter already scopes every one of these
    /// tables — <c>Recipes</c>, <c>RecipeTags</c>, <c>RecipeVersions</c> and <c>RecipeIngredients</c> are all
    /// workspace-owned — and writing one by hand would suggest the filter is optional. That is also what makes
    /// the correlated subqueries safe: a version or a tag link belonging to another workspace is not merely
    /// filtered out of them, it is not visible to them.
    /// </para>
    /// </remarks>
    private IQueryable<Recipe> Filtered(RecipeSearchCriteria criteria)
    {
        var filters = criteria.Filters;
        var recipes = context.Recipes.AsNoTracking();

        if (filters.Search is { } term)
        {
            // Lowered on both sides rather than relying on the collation, matching the reference repositories:
            // SQL Server's default is case-insensitive and SQLite's is not, and a search should mean the same
            // thing in a test as in production. Forfeits an index seek, which a substring match never had.
            recipes = recipes.Where(recipe =>
                recipe.Title.ToLower().Contains(term)
                || (recipe.Description != null && recipe.Description.ToLower().Contains(term)));
        }

        if (filters.Statuses is { Count: > 0 } statuses)
        {
            recipes = recipes.Where(recipe => statuses.Contains(recipe.Status));
        }

        if (filters.CuisineIds is { Count: > 0 } cuisineIds)
        {
            recipes = recipes.Where(recipe =>
                recipe.CuisineId.HasValue && cuisineIds.Contains(recipe.CuisineId.Value));
        }

        if (filters.CourseIds is { Count: > 0 } courseIds)
        {
            recipes = recipes.Where(recipe =>
                recipe.CourseId.HasValue && courseIds.Contains(recipe.CourseId.Value));
        }

        if (filters.CreatorMembershipIds is { Count: > 0 } creatorIds)
        {
            recipes = recipes.Where(recipe => creatorIds.Contains(recipe.CreatedByMembershipId));
        }

        if (filters.TagIds is { Count: > 0 } tagIds)
        {
            // A semi-join, not a join: a recipe carrying two of the requested tags must appear once. Served by
            // IX_RecipeTags_Workspace_Tag, which already carries RecipeId in its leaf rows because that column
            // is part of the table's clustered primary key.
            recipes = recipes.Where(recipe => context.RecipeTags.Any(tag =>
                tag.RecipeId == recipe.Id && tagIds.Contains(tag.WorkspaceTagId)));
        }

        if (filters.LatestVersionReadiness is { } readiness)
        {
            recipes = recipes.Where(recipe => context.RecipeVersions
                .Where(version => version.RecipeId == recipe.Id)
                // By number, not by CreatedAt: the number is the gap-free identity creators cite, and two
                // versions written in the same tick would make a timestamp ordering arbitrary.
                .OrderByDescending(version => version.VersionNumber)
                .Select(version => (RecipeVersionReadiness?)version.Readiness)
                .FirstOrDefault() == readiness);
        }

        if (filters.IngredientReview is { } review)
        {
            // Branched in C# rather than compared to a boolean parameter, so this stays EXISTS / NOT EXISTS and
            // can short-circuit on the first unmatched line instead of evaluating a CASE for every row.
            //
            // Written out twice rather than factored into a helper: EF Core translates the expression tree it is
            // given and does not inline a method call, so a shared `HasUnmatched(recipe.Id)` would compile and
            // then fail to translate at runtime. The duplication is the price of the query being translatable,
            // and the projection below pays it a third time for the same reason.
            recipes = review switch
            {
                RecipeIngredientReviewFilter.HasUnmatched => recipes.Where(recipe =>
                    context.RecipeIngredients.Any(line =>
                        line.RecipeId == recipe.Id && line.MatchStatus != IngredientMatchStatus.Matched)),

                RecipeIngredientReviewFilter.AllMatched => recipes.Where(recipe =>
                    !context.RecipeIngredients.Any(line =>
                        line.RecipeId == recipe.Id && line.MatchStatus != IngredientMatchStatus.Matched)),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(criteria),
                    review,
                    "Unknown ingredient review filter."),
            };
        }

        if (filters.UpdatedOnOrAfter is { } updatedFrom)
        {
            recipes = recipes.Where(recipe => recipe.UpdatedAt >= updatedFrom);
        }

        if (filters.UpdatedBefore is { } updatedBefore)
        {
            recipes = recipes.Where(recipe => recipe.UpdatedAt < updatedBefore);
        }

        if (filters.CreatedOnOrAfter is { } createdFrom)
        {
            recipes = recipes.Where(recipe => recipe.CreatedAt >= createdFrom);
        }

        if (filters.CreatedBefore is { } createdBefore)
        {
            recipes = recipes.Where(recipe => recipe.CreatedAt < createdBefore);
        }

        return recipes;
    }

    /// <summary>
    /// Resumes after the last row of the previous page.
    /// </summary>
    /// <remarks>
    /// The predicate and the <c>ORDER BY</c> in <see cref="Ordered"/> must name the same two columns in the same
    /// two directions. That is the whole correctness condition of a keyset: if they disagree by one column or
    /// one direction, paging silently repeats or skips rows rather than failing.
    /// </remarks>
    private static IQueryable<Recipe> Positioned(IQueryable<Recipe> recipes, RecipeSearchCriteria criteria)
    {
        if (criteria.Position is not { } position)
        {
            return recipes;
        }

        return criteria.Sort switch
        {
            RecipeSearchSort.RecentlyUpdated => recipes.Where(recipe =>
                recipe.UpdatedAt < position.UpdatedAt
                || (recipe.UpdatedAt == position.UpdatedAt
                    && recipe.Id.CompareTo(position.RecipeId) < 0)),

            RecipeSearchSort.Title => recipes.Where(recipe =>
                string.Compare(recipe.Title, position.Title) > 0
                || (recipe.Title == position.Title
                    && recipe.Id.CompareTo(position.RecipeId) > 0)),

            _ => throw new ArgumentOutOfRangeException(nameof(criteria), criteria.Sort, "Unknown recipe sort."),
        };
    }

    /// <summary>
    /// Orders by the requested key, breaks every tie by id, and fetches one row more than asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tie-break is not decoration. Two recipes saved in the same tick, or two sharing a title — which
    /// creators legitimately do — would otherwise have no defined order between them, and a page boundary
    /// falling between them would repeat one and skip the other on every read.
    /// </para>
    /// <para>
    /// Each branch has an index whose key is exactly these columns in exactly these directions, so neither is a
    /// sort: <c>IX_Recipes_Workspace_UpdatedAt</c> and <c>IX_Recipes_Workspace_Title</c>, plus
    /// <c>IX_Recipes_Workspace_Status_UpdatedAt</c> when a status filter narrows the first of them.
    /// </para>
    /// </remarks>
    private static IQueryable<Recipe> Ordered(IQueryable<Recipe> recipes, RecipeSearchCriteria criteria) =>
        (criteria.Sort switch
        {
            RecipeSearchSort.RecentlyUpdated => recipes
                .OrderByDescending(recipe => recipe.UpdatedAt)
                .ThenByDescending(recipe => recipe.Id),

            RecipeSearchSort.Title => recipes
                .OrderBy(recipe => recipe.Title)
                .ThenBy(recipe => recipe.Id),

            _ => throw new ArgumentOutOfRangeException(nameof(criteria), criteria.Sort, "Unknown recipe sort."),
        })
        // One row past the limit. That row is never returned — it is how the page learns whether another
        // follows, without a second COUNT against a set that may have changed in between.
        .Take(criteria.Limit + 1);

    /// <summary>
    /// Projects summaries in SQL. Nothing here materialises a recipe entity, loads a child collection, or
    /// touches a version snapshot or a media asset.
    /// </summary>
    /// <remarks>
    /// The two latest-version subqueries read the same row and become two <c>OUTER APPLY</c>s, each an
    /// index-only seek of <c>UX_RecipeVersions_Workspace_Recipe_VersionNumber</c> — the reason
    /// <c>Readiness</c> is an included column on it. Collapsing them into one apply would mean projecting an
    /// intermediate shape and re-mapping it in memory, which buys one less apply over a page of at most a
    /// hundred rows and costs the projection being readable as the record it produces.
    /// </remarks>
    private IQueryable<RecipeSummaryRecord> Project(IQueryable<Recipe> recipes, RecipeSearchSort sort) =>
        recipes.Select(recipe => new RecipeSummaryRecord(
            recipe.Id,
            recipe.Title,
            recipe.Description,
            recipe.Status,
            recipe.CuisineId,
            recipe.CourseId,
            recipe.CreatedByMembershipId,
            recipe.UpdatedByMembershipId,
            recipe.CreatedAt,
            recipe.UpdatedAt,
            context.RecipeVersions
                .Where(version => version.RecipeId == recipe.Id)
                .OrderByDescending(version => version.VersionNumber)
                .Select(version => (int?)version.VersionNumber)
                .FirstOrDefault(),
            context.RecipeVersions
                .Where(version => version.RecipeId == recipe.Id)
                .OrderByDescending(version => version.VersionNumber)
                .Select(version => (RecipeVersionReadiness?)version.Readiness)
                .FirstOrDefault(),
            context.RecipeIngredients.Any(line =>
                line.RecipeId == recipe.Id && line.MatchStatus != IngredientMatchStatus.Matched),
            sort));
}
