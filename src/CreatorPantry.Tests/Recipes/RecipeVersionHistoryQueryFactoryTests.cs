using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What a bound history query becomes before it reaches the database: which cursors are accepted, what they
/// are bound to, and where the page size is settled.
/// </summary>
public sealed class RecipeVersionHistoryQueryFactoryTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static readonly Guid OtherWorkspace = Guid.NewGuid();

    private static readonly Guid Recipe = Guid.NewGuid();

    private static readonly Guid OtherRecipe = Guid.NewGuid();

    [Fact]
    public void A_query_with_no_cursor_is_the_first_page()
    {
        Assert.True(TryCreate(new RecipeVersionHistoryViewModel(), out var criteria, out _));

        Assert.Null(criteria!.Position);
        Assert.Equal(Recipe, criteria.RecipeId);
        Assert.Equal(RecipeVersionHistoryScope.Build(Workspace, Recipe), criteria.Scope);
    }

    [Fact]
    public void A_cursor_from_this_recipes_history_resumes_it()
    {
        var cursor = CursorFor(Workspace, Recipe, versionNumber: 12);

        Assert.True(TryCreate(new RecipeVersionHistoryViewModel(Cursor: cursor), out var criteria, out _));

        Assert.Equal(12, criteria!.Position!.VersionNumber);
    }

    /// <summary>
    /// The reason the recipe is in the scope at all. Without it this cursor would decode cleanly and page the
    /// wrong recipe's history from version 12 downwards — a wrong answer rather than an error.
    /// </summary>
    [Fact]
    public void A_cursor_from_another_recipes_history_is_refused()
    {
        var cursor = CursorFor(Workspace, OtherRecipe, versionNumber: 12);

        Assert.False(TryCreate(new RecipeVersionHistoryViewModel(Cursor: cursor), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
        Assert.True(error.FieldErrors.ContainsKey("cursor"));
    }

    [Fact]
    public void A_cursor_from_the_same_recipe_id_in_another_workspace_is_refused()
    {
        // Not reachable through the routes — a recipe id is unique across workspaces — but the scope says the
        // workspace rather than relying on that, and this is the assertion that keeps it saying so.
        var cursor = CursorFor(OtherWorkspace, Recipe, versionNumber: 12);

        Assert.False(TryCreate(new RecipeVersionHistoryViewModel(Cursor: cursor), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
    }

    /// <summary>
    /// Bound to the right history and still not a position in it. Only reachable by editing a cursor, so it
    /// gets the same answer: start again without one.
    /// </summary>
    [Fact]
    public void A_cursor_whose_sort_value_is_not_a_version_number_is_refused()
    {
        var cursor = ReferenceCursor.Encode(
            "2026-09-24T12:00:00.0000000+00:00",
            Guid.NewGuid().ToString("D"),
            RecipeVersionHistoryScope.Build(Workspace, Recipe));

        Assert.False(TryCreate(new RecipeVersionHistoryViewModel(Cursor: cursor), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
    }

    [Theory]
    [InlineData(null, ReferencePolicy.DefaultPageSize)]
    [InlineData(10, 10)]
    [InlineData(0, ReferencePolicy.MinPageSize)]
    [InlineData(-5, ReferencePolicy.MinPageSize)]
    [InlineData(100_000, ReferencePolicy.MaxPageSize)]
    public void The_page_size_is_clamped_rather_than_refused(int? requested, int expected)
    {
        Assert.True(TryCreate(new RecipeVersionHistoryViewModel(Limit: requested), out var criteria, out _));

        Assert.Equal(expected, criteria!.Limit);
    }

    /// <summary>
    /// The clamp is on the derived property, not on a stored one — so a <c>with</c> expression cannot smuggle
    /// an unclamped page size past it.
    /// </summary>
    [Fact]
    public void An_unclamped_page_size_is_unrepresentable()
    {
        var criteria = new RecipeVersionHistoryCriteria(Recipe, "scope") with { RequestedLimit = 10_000 };

        Assert.Equal(ReferencePolicy.MaxPageSize, criteria.Limit);
    }

    private static bool TryCreate(
        RecipeVersionHistoryViewModel model,
        out RecipeVersionHistoryCriteria? criteria,
        out CreatorPantry.Domain.Managers.Results.OperationError? error) =>
        RecipeVersionHistoryQueryFactory.TryCreate(model, Workspace, Recipe, out criteria, out error);

    private static string CursorFor(Guid workspaceId, Guid recipeId, int versionNumber) =>
        ReferenceCursor.Encode(
            versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("D"),
            RecipeVersionHistoryScope.Build(workspaceId, recipeId));
}
