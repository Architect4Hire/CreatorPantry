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

        var statuses = ParseEnums<RecipeStatus>(model.Status, "status", errors);
        var tagIds = ParseIds(model.Tag, "tag", errors);
        var cuisineIds = ParseIds(model.Cuisine, "cuisine", errors);
        var courseIds = ParseIds(model.Course, "course", errors);
        var readiness = ParseEnum<RecipeVersionReadiness>(model.Readiness, "readiness", errors);
        var review = ParseEnum<RecipeIngredientReviewFilter>(model.IngredientReview, "ingredientReview", errors);
        var sort = ParseEnum<RecipeSearchSort>(model.Sort, "sort", errors) ?? RecipeSearchSort.RecentlyUpdated;

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

    /// <summary>Splits a comma-separated list, or <c>null</c> when the caller did not send a value.</summary>
    /// <remarks>
    /// An empty value is treated as absent rather than as a filter matching nothing, because <c>?status=</c> is
    /// what a client sends when it has cleared a filter — refusing it would make clearing one an error. That
    /// includes a value that is nothing but separators: <c>?status=,,</c> names no status, so it is the same
    /// request as naming none at all, and returning an empty list would have made it a filter that matches
    /// nothing.
    /// </remarks>
    private static string[]? Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var entries = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return entries.Length == 0 ? null : entries;
    }

    private static IReadOnlyList<Guid>? ParseIds(string? raw, string field, List<(string, string)> errors)
    {
        if (Split(raw) is not { } entries)
        {
            return null;
        }

        var ids = new List<Guid>(entries.Length);

        foreach (var entry in entries)
        {
            if (Guid.TryParseExact(entry, "D", out var id))
            {
                ids.Add(id);
            }
            else
            {
                errors.Add((field, $"'{entry}' is not an id."));
            }
        }

        return ids;
    }

    private static IReadOnlyList<TEnum>? ParseEnums<TEnum>(string? raw, string field, List<(string, string)> errors)
        where TEnum : struct, Enum
    {
        if (Split(raw) is not { } entries)
        {
            return null;
        }

        var values = new List<TEnum>(entries.Length);

        foreach (var entry in entries)
        {
            if (TryParseDefined<TEnum>(entry, out var value))
            {
                values.Add(value);
            }
            else
            {
                errors.Add((field, Accepted<TEnum>(entry)));
            }
        }

        return values;
    }

    private static TEnum? ParseEnum<TEnum>(string? raw, string field, List<(string, string)> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (TryParseDefined<TEnum>(raw.Trim(), out var value))
        {
            return value;
        }

        errors.Add((field, Accepted<TEnum>(raw.Trim())));

        return null;
    }

    /// <summary>
    /// Parses a name, case-insensitively, and only a name.
    /// </summary>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> also accepts numbers, so <c>readiness=7</c>
    /// would otherwise succeed and produce a value no member has — a filter that compiles into a comparison
    /// against nothing. The digit check refuses a numeric form outright rather than letting
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> accept one that happens to land on a member, because the
    /// numbers are an implementation detail of the enum and never part of the published contract.
    /// </remarks>
    private static bool TryParseDefined<TEnum>(string entry, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        return !char.IsAsciiDigit(entry[0])
            && entry[0] != '-'
            && Enum.TryParse(entry, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    private static string Accepted<TEnum>(string entry)
        where TEnum : struct, Enum =>
        $"'{entry}' is not one of: {string.Join(", ", Enum.GetNames<TEnum>())}.";
}
