using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Turns a bound <see cref="RecipeVersionHistoryViewModel"/> into the typed criteria the rest of the seam works
/// in: scope built, cursor checked and decoded, page size clamped.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It takes the workspace id and the recipe id, and neither comes from the model.</strong> Both are
/// route-resolved facts handed down by the facade — the workspace from <c>IWorkspaceContext</c>, the recipe
/// from the route segment the controller bound. They are here because the scope a cursor is bound to depends on
/// both, which is the one place the shared paging pattern does not carry over from the reference routes.
/// </para>
/// <para>
/// <strong>The recipe is not checked for existence here.</strong> This runs before anything has touched the
/// database, and a cursor refusal for a recipe the caller may not see would be the disclosure the 404 exists to
/// prevent. The order is deliberate: a malformed cursor is a bad request about the caller's own previous page
/// and says nothing about which recipes exist, while everything that could disclose one is settled below, where
/// the answer is a uniform 404.
/// </para>
/// </remarks>
public static class RecipeVersionHistoryQueryFactory
{
    public static bool TryCreate(
        RecipeVersionHistoryViewModel model,
        Guid workspaceId,
        Guid recipeId,
        out RecipeVersionHistoryCriteria? criteria,
        out OperationError? error)
    {
        criteria = null;
        error = null;

        var scope = RecipeVersionHistoryScope.Build(workspaceId, recipeId);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            error = CursorRefused();

            return false;
        }

        RecipeVersionHistoryPosition? position = null;
        if (cursor is not null && !RecipeVersionHistoryPosition.TryCreate(cursor, out position))
        {
            // Structurally valid, bound to this exact recipe's history, and still not a position in it — a
            // cursor whose sort value is not a version number. Only reachable by editing one, so it gets the
            // same answer: start again without it.
            error = CursorRefused();

            return false;
        }

        criteria = new RecipeVersionHistoryCriteria(recipeId, scope, position, model.Limit);

        return true;
    }

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            RecipeErrorCodes.CursorInvalidRequest,
            "This cursor was issued for a different workspace or recipe. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);
}
