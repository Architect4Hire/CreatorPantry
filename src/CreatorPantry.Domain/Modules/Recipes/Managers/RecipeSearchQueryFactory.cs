using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Turns a bound <see cref="RecipeSearchViewModel"/> into the typed criteria the rest of the seam works in:
/// filters parsed, scope built, cursor checked and decoded, page size clamped.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It takes the workspace id, and the reference query factories do not.</strong> That is the one place
/// the shared paging pattern does not carry over: a reference cursor's scope is fully determined by its route and
/// filters, while a workspace-owned one also depends on which workspace was resolved. The id comes from
/// <c>IWorkspaceContext</c> by way of the facade — never from the view model, which has no field for it.
/// </para>
/// <para>
/// <strong>Filter errors are reported before the cursor is considered.</strong> A caller who mistyped a status
/// and is told their cursor is stale would fix the wrong thing; and a mistyped filter genuinely does invalidate
/// the cursor, because it changes the scope, so reporting the cursor first would be true and useless.
/// </para>
/// </remarks>
public static class RecipeSearchQueryFactory
{
    public static bool TryCreate(
        RecipeSearchViewModel model,
        Guid workspaceId,
        Guid membershipId,
        out RecipeSearchCriteria? criteria,
        out OperationError? error)
    {
        criteria = null;
        error = null;

        var errors = new List<(string Field, string Error)>();

        var statuses = QueryFilterParser.ParseEnums<RecipeStatus>(model.Status, "status", errors);
        var tagIds = QueryFilterParser.ParseIds(model.Tag, "tag", errors);
        var cuisineIds = QueryFilterParser.ParseIds(model.Cuisine, "cuisine", errors);
        var courseIds = QueryFilterParser.ParseIds(model.Course, "course", errors);
        var readiness = QueryFilterParser.ParseEnum<RecipeVersionReadiness>(model.Readiness, "readiness", errors);
        var review = QueryFilterParser.ParseEnum<RecipeIngredientReviewFilter>(model.IngredientReview, "ingredientReview", errors);
        var sort = QueryFilterParser.ParseEnum<RecipeSearchSort>(model.Sort, "sort", errors) ?? RecipeSearchSort.RecentlyUpdated;

        if (errors.Count > 0)
        {
            error = OperationError.Validation(
                RecipeErrorCodes.SearchInvalidRequest, "That search cannot be run as described.", errors);

            return false;
        }

        var filters = new RecipeSearchFilters(
            Search: RecipeSearchPolicy.NormalizeSearch(model.Search),

            // A caller who named no status gets the library rather than the archive — REC-006's default
            // exclusion, applied by turning "no filter" into the explicit default set. Stated in the criteria
            // rather than left implicit in the repository, so the predicate the query runs is the predicate
            // the cursor is bound to, and `?status=Archived` still reaches archived recipes because it names
            // a status. See RecipePolicy.DefaultSearchStatuses.
            Statuses: statuses is { Count: > 0 } ? statuses : RecipePolicy.DefaultSearchStatuses,
            TagIds: tagIds,
            CuisineIds: cuisineIds,
            CourseIds: courseIds,

            // The caller's own membership, taken from the resolved context. "Mine" is the only authorship filter
            // there is: no route publishes another member's membership id, so an id-valued filter would be one
            // nobody could populate — and accepting one would invite a client to guess.
            CreatorMembershipIds: model.Mine is true ? [membershipId] : null,
            LatestVersionReadiness: readiness,
            IngredientReview: review,
            UpdatedOnOrAfter: model.UpdatedFrom,
            UpdatedBefore: model.UpdatedBefore,
            CreatedOnOrAfter: model.CreatedFrom,
            CreatedBefore: model.CreatedBefore);

        var scope = RecipeSearchScope.Build(workspaceId, sort, filters);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            error = CursorRefused();

            return false;
        }

        RecipeSearchPosition? position = null;
        if (cursor is not null && !RecipeSearchPosition.TryCreate(sort, cursor, out position))
        {
            // Structurally valid, bound to this exact scope, and still not a position in it — a cursor whose
            // tie-breaker is not an id, or whose sort value is not a timestamp under a timestamp ordering. Only
            // reachable by editing one, so it gets the same answer: start again without it.
            error = CursorRefused();

            return false;
        }

        criteria = new RecipeSearchCriteria(
            filters,
            scope,
            sort,
            position,
            model.Limit,
            IncludeTotal: model.IncludeTotal ?? true);

        return true;
    }

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            RecipeErrorCodes.CursorInvalidRequest,
            "This cursor was issued for a different workspace, ordering or set of filters. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);
}
