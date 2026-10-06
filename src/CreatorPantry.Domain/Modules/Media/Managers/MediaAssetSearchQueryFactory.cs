using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Turns a library search's query string into criteria, or into the field errors that explain why not.
/// </summary>
/// <remarks>
/// A pure function over the request and the resolved workspace, so the parsing a cursor was bound to is the
/// parsing the query runs — the same shape <c>RecipeSearchQueryFactory</c> has, for the same reason.
/// </remarks>
public static class MediaAssetSearchQueryFactory
{
    public static bool TryCreate(
        MediaAssetSearchViewModel model,
        Guid workspaceId,
        out MediaAssetSearchCriteria? criteria,
        out OperationError? error)
    {
        ArgumentNullException.ThrowIfNull(model);

        criteria = null;
        error = null;

        var errors = new List<(string, string)>();
        var cuisineIds = QueryFilterParser.ParseIds(model.Cuisine, "cuisine", errors);
        var courseIds = QueryFilterParser.ParseIds(model.Course, "course", errors);
        var tagIds = QueryFilterParser.ParseIds(model.Tag, "tag", errors);
        var day = QueryFilterParser.ParseEnum<DayOfWeek>(model.Day, "day", errors);
        var sort = QueryFilterParser.ParseEnum<MediaAssetSearchSort>(model.Sort, "sort", errors)
            ?? MediaAssetSearchSort.RecentlyAdded;

        if (errors.Count > 0)
        {
            error = OperationError.Validation(
                MediaErrorCodes.AssetSearchInvalidRequest, "That search cannot be run as described.", errors);

            return false;
        }

        var filters = new MediaAssetSearchFilters(
            Search: MediaAssetSearchPolicy.NormalizeSearch(model.Search),
            ChannelKey: Trimmed(model.Channel),
            PlatformKey: Trimmed(model.Platform),
            Day: day,
            StyleKey: Trimmed(model.Style),
            CuisineIds: cuisineIds,
            CourseIds: courseIds,
            TagIds: tagIds,
            RecipeId: model.RecipeId,
            CreatedOnOrAfter: model.CreatedFrom,
            CreatedBefore: model.CreatedBefore);

        var scope = MediaAssetSearchScope.Build(workspaceId, sort, filters);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            error = CursorRefused();

            return false;
        }

        MediaAssetSearchPosition? position = null;

        if (cursor is not null && !MediaAssetSearchPosition.TryCreate(sort, cursor, out position))
        {
            // Structurally valid, bound to this exact scope, and still not a position in it — a cursor whose
            // tie-breaker is not an id, or whose sort value is not a timestamp under a timestamp ordering.
            // Only reachable by editing one, so it gets the same answer: start again without it.
            error = CursorRefused();

            return false;
        }

        criteria = new MediaAssetSearchCriteria(
            filters, scope, sort, position, model.Limit, IncludeTotal: model.IncludeTotal ?? true);

        return true;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            MediaErrorCodes.AssetCursorInvalidRequest,
            "This cursor was issued for a different workspace, ordering or set of filters. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);
}
