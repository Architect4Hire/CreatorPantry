using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Turns a bound <see cref="PromptSearchViewModel"/> into the typed criteria the rest of the seam works in:
/// filters normalized, scope built, cursor checked and decoded, page size clamped.
/// </summary>
/// <remarks>
/// <strong>It takes the workspace id, and the reference query factories do not.</strong> That is the one place
/// the shared paging pattern does not carry over: a reference cursor's scope is fully determined by its route
/// and filters, while a workspace-owned one also depends on which workspace was resolved. The id comes from
/// <c>IWorkspaceContext</c> by way of the facade — never from the view model, which has no field for it.
/// </remarks>
public static class PromptSearchQueryFactory
{
    public static bool TryCreate(
        PromptSearchViewModel model,
        Guid workspaceId,
        out PromptSearchCriteria? criteria,
        out OperationError? error)
    {
        ArgumentNullException.ThrowIfNull(model);

        criteria = null;
        error = null;

        // Nothing to parse and nothing to refuse: both filters are strings this seam matches literally, so
        // unlike the recipe factory there is no enum or id list that can be mistyped. A channel key naming
        // nothing is an empty page, which is the read's answer rather than the parser's.
        var filters = new PromptSearchFilters(
            Search: PromptSearchPolicy.NormalizeSearch(model.Search),
            ChannelKey: PromptSearchPolicy.NormalizeChannel(model.Channel));

        var scope = PromptSearchScope.Build(workspaceId, filters);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            error = CursorRefused();

            return false;
        }

        PromptSearchPosition? position = null;
        if (cursor is not null && !PromptSearchPosition.TryCreate(cursor, out position))
        {
            // Structurally valid, bound to this exact scope, and still not a position in it — a cursor whose
            // tie-breaker is not an id or whose sort value is not a round-trip timestamp. Only reachable by
            // editing one, so it gets the same answer: start again without it.
            error = CursorRefused();

            return false;
        }

        criteria = new PromptSearchCriteria(
            filters,
            scope,
            position,
            model.Limit,
            IncludeTotal: model.IncludeTotal ?? true);

        return true;
    }

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            ContentErrorCodes.PromptCursorInvalid,
            "This cursor was issued for a different workspace or set of filters. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);
}
