using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Turns a bound <see cref="TestRunHistoryViewModel"/> into the typed criteria the rest of the seam works in:
/// filters parsed, scope built, cursor checked and decoded, page size clamped.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It takes the workspace id and the recipe id, and neither comes from the model.</strong> Both are
/// route-resolved facts handed down by the facade — the workspace from <c>IWorkspaceContext</c>, the recipe from
/// the route segment the controller bound. They are here because the scope a cursor is bound to depends on both.
/// </para>
/// <para>
/// <strong>Filter errors are reported before the cursor is considered</strong>, for the reason
/// <see cref="RecipeSearchQueryFactory"/> gives: a caller told their cursor is stale when they in fact mistyped an
/// outcome would fix the wrong thing — and a mistyped filter genuinely does invalidate the cursor, because it
/// changes the scope, so reporting the cursor first would be true and useless.
/// </para>
/// <para>
/// <strong>The recipe is not checked for existence here.</strong> This runs before anything has touched the
/// database, and a cursor or filter refusal for a recipe the caller may not see would be the disclosure the 404
/// exists to prevent. The order is the same one the version history establishes: a malformed filter or cursor is a
/// bad request about the caller's own query and says nothing about which recipes exist, while everything that
/// could disclose one is settled below, where the answer is a uniform 404.
/// </para>
/// </remarks>
public static class TestRunHistoryQueryFactory
{
    public static bool TryCreate(
        TestRunHistoryViewModel model,
        Guid workspaceId,
        Guid recipeId,
        out TestRunHistoryCriteria? criteria,
        out OperationError? error)
    {
        criteria = null;
        error = null;

        var errors = new List<(string Field, string Error)>();

        var versionNumbers = QueryFilterParser.ParsePositiveInts(model.Version, "version", errors);
        var testerIds = QueryFilterParser.ParseIds(model.TestedBy, "testedBy", errors);
        var outcomes = QueryFilterParser.ParseEnums<TestRunOutcome>(model.Outcome, "outcome", errors);
        var issues = QueryFilterParser.ParseEnum<TestRunIssueFilter>(model.Issues, "issues", errors);

        if (errors.Count > 0)
        {
            error = OperationError.Validation(
                RecipeErrorCodes.TestRunInvalidRequest, "That test history cannot be read as described.", errors);

            return false;
        }

        var filters = new TestRunHistoryFilters(
            VersionNumbers: versionNumbers,
            TesterMembershipIds: testerIds,
            Outcomes: outcomes,
            TestedOnOrAfter: model.TestedFrom,
            TestedBefore: model.TestedBefore,
            Issues: issues);

        var scope = TestRunHistoryScope.Build(workspaceId, recipeId, filters);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            error = CursorRefused();

            return false;
        }

        TestRunHistoryPosition? position = null;
        if (cursor is not null && !TestRunHistoryPosition.TryCreate(cursor, out position))
        {
            // Structurally valid, bound to this exact recipe and these exact filters, and still not a position in
            // the set — a cursor whose tie-breaker is not an id or whose sort value is not a timestamp. Only
            // reachable by editing one, so it gets the same answer: start again without it.
            error = CursorRefused();

            return false;
        }

        criteria = new TestRunHistoryCriteria(
            recipeId,
            filters,
            scope,
            position,
            model.Limit,
            IncludeSummary: model.IncludeSummary ?? true);

        return true;
    }

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            RecipeErrorCodes.CursorInvalidRequest,
            "This cursor was issued for a different workspace, recipe or set of filters. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);
}
