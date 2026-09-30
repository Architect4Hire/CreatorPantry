using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What a bound test-history query becomes before it reaches the database: which filters are accepted, which
/// cursors are, what they are bound to, and where the page size is settled.
/// </summary>
public sealed class TestRunHistoryQueryFactoryTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static readonly Guid OtherWorkspace = Guid.NewGuid();

    private static readonly Guid Recipe = Guid.NewGuid();

    private static readonly Guid OtherRecipe = Guid.NewGuid();

    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    // ---- Filters ----

    [Fact]
    public void An_empty_query_filters_nothing_and_asks_for_the_summary()
    {
        Assert.True(TryCreate(new TestRunHistoryViewModel(), out var criteria, out _));

        Assert.Equal(Recipe, criteria!.RecipeId);
        Assert.Equal(new TestRunHistoryFilters(), criteria.Filters);
        Assert.Null(criteria.Position);
        Assert.True(criteria.IncludeSummary);
    }

    [Fact]
    public void Comma_separated_filters_are_split_into_typed_lists()
    {
        var tester = Guid.NewGuid();

        Assert.True(
            TryCreate(
                new TestRunHistoryViewModel(
                    Version: "1, 3,7",
                    TestedBy: tester.ToString("D"),
                    Outcome: "Failed,succeededWithIssues",
                    Issues: "hasUnresolved",
                    TestedFrom: Noon,
                    TestedBefore: Noon.AddDays(1)),
                out var criteria,
                out _));

        var filters = criteria!.Filters;

        Assert.Equal([1, 3, 7], filters.VersionNumbers);
        Assert.Equal([tester], filters.TesterMembershipIds);

        // Case-insensitive by name, which is what a client typing a filter into an address bar produces.
        Assert.Equal([TestRunOutcome.Failed, TestRunOutcome.SucceededWithIssues], filters.Outcomes);
        Assert.Equal(TestRunIssueFilter.HasUnresolved, filters.Issues);
        Assert.Equal(Noon, filters.TestedOnOrAfter);
        Assert.Equal(Noon.AddDays(1), filters.TestedBefore);
    }

    /// <summary>
    /// An empty value is what a client sends when it has cleared a filter, so it is the absence of one rather than a
    /// filter matching nothing. A value that is nothing but separators means the same thing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void A_cleared_filter_is_not_a_filter(string raw)
    {
        Assert.True(
            TryCreate(new TestRunHistoryViewModel(Version: raw, TestedBy: raw, Outcome: raw), out var criteria, out _));

        Assert.Null(criteria!.Filters.VersionNumbers);
        Assert.Null(criteria.Filters.TesterMembershipIds);
        Assert.Null(criteria.Filters.Outcomes);
    }

    [Fact]
    public void A_malformed_filter_is_refused_naming_the_parameter_that_carried_it()
    {
        Assert.False(
            TryCreate(
                new TestRunHistoryViewModel(Version: "twelve", TestedBy: "nope", Outcome: "Burnt", Issues: "Maybe"),
                out _,
                out var error));

        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, error!.Code);

        // Every mistake at once, not the first one: a caller who mistyped four filters should have to fix them
        // once rather than four times.
        Assert.Equal(["issues", "outcome", "testedBy", "version"], error.FieldErrors.Keys.Order());
    }

    /// <summary>
    /// A version number is positive by check constraint, so zero and negatives name nothing. Refused by name rather
    /// than silently matched against nothing, which is an answer a creator cannot debug.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void A_version_number_that_cannot_exist_is_refused(string raw)
    {
        Assert.False(TryCreate(new TestRunHistoryViewModel(Version: raw), out _, out var error));

        Assert.True(error!.FieldErrors.ContainsKey("version"));
    }

    /// <summary>
    /// The numbers behind an enum are an implementation detail and never part of the published contract, so a
    /// numeric outcome is refused even when it happens to land on a member. Without this, <c>?outcome=9</c> would
    /// become a filter comparing against a value no member has.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    public void A_numeric_outcome_is_refused(string raw)
    {
        Assert.False(TryCreate(new TestRunHistoryViewModel(Outcome: raw), out _, out var error));

        Assert.True(error!.FieldErrors.ContainsKey("outcome"));
    }

    /// <summary>
    /// A filter mistake is reported before the cursor is considered. The cursor really is invalid here — a changed
    /// filter changes the scope — so naming it would be true and useless, and the caller would fix the wrong thing.
    /// </summary>
    [Fact]
    public void A_bad_filter_is_reported_ahead_of_a_stale_cursor()
    {
        var stale = CursorFor(Workspace, OtherRecipe, Noon);

        Assert.False(
            TryCreate(new TestRunHistoryViewModel(Outcome: "Burnt", Cursor: stale), out _, out var error));

        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, error!.Code);
    }

    [Fact]
    public void The_summary_can_be_turned_off()
    {
        Assert.True(TryCreate(new TestRunHistoryViewModel(IncludeSummary: false), out var criteria, out _));

        Assert.False(criteria!.IncludeSummary);
    }

    // ---- Cursors ----

    [Fact]
    public void A_cursor_from_this_recipes_history_resumes_it()
    {
        var id = Guid.NewGuid();

        Assert.True(
            TryCreate(
                new TestRunHistoryViewModel(Cursor: CursorFor(Workspace, Recipe, Noon, id)), out var criteria, out _));

        Assert.Equal(Noon, criteria!.Position!.TestedAt);
        Assert.Equal(id, criteria.Position.TestRunId);
    }

    /// <summary>
    /// The reason the recipe is in the scope at all. Without it this cursor would decode cleanly and page the wrong
    /// recipe's tests from the same moment onwards — a wrong answer rather than an error, and one that would look
    /// entirely plausible, since both recipes belong to the same creator.
    /// </summary>
    [Fact]
    public void A_cursor_from_another_recipes_history_is_refused()
    {
        Assert.False(
            TryCreate(new TestRunHistoryViewModel(Cursor: CursorFor(Workspace, OtherRecipe, Noon)), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
        Assert.True(error.FieldErrors.ContainsKey("cursor"));
    }

    [Fact]
    public void A_cursor_from_the_same_recipe_id_in_another_workspace_is_refused()
    {
        // Not reachable through the routes — a recipe id is unique across workspaces — but the scope says the
        // workspace rather than relying on that, and this is the assertion that keeps it saying so.
        Assert.False(
            TryCreate(new TestRunHistoryViewModel(Cursor: CursorFor(OtherWorkspace, Recipe, Noon)), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
    }

    /// <summary>
    /// A cursor is a position in one ordered set, and the filters decide which set that is. Following a cursor into
    /// a different filter would resume from a row that may not even be in the new set.
    /// </summary>
    [Fact]
    public void A_cursor_minted_under_different_filters_is_refused()
    {
        var unfiltered = CursorFor(Workspace, Recipe, Noon);

        Assert.False(
            TryCreate(new TestRunHistoryViewModel(Outcome: "Failed", Cursor: unfiltered), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
    }

    /// <summary>
    /// The same filters written in a different order are the same query, because the scope sorts its lists before
    /// folding them in. Without that, a client that reordered its own parameters between pages would have its
    /// cursor refused for no reason it could see.
    /// </summary>
    [Fact]
    public void Reordering_a_multi_valued_filter_does_not_invalidate_a_cursor()
    {
        Assert.True(TryCreate(new TestRunHistoryViewModel(Outcome: "Failed,Succeeded", Version: "2,10"), out var first, out _));

        var cursor = ReferenceCursor.Encode(
            Noon.ToString("O", CultureInfo.InvariantCulture), Guid.NewGuid().ToString("D"), first!.Scope);

        Assert.True(
            TryCreate(
                new TestRunHistoryViewModel(Outcome: "Succeeded,Failed", Version: "10,2", Cursor: cursor),
                out _,
                out _));
    }

    /// <summary>
    /// Asking to be counted does not change which rows follow, so it is not in the scope — a client that turned the
    /// summary off on page two must not have its cursor refused for it.
    /// </summary>
    [Fact]
    public void Turning_the_summary_off_does_not_invalidate_a_cursor()
    {
        var cursor = CursorFor(Workspace, Recipe, Noon);

        Assert.True(
            TryCreate(new TestRunHistoryViewModel(IncludeSummary: false, Cursor: cursor), out _, out _));
    }

    /// <summary>
    /// Bound to the right history and still not a position in it — only reachable by editing a cursor, so it gets
    /// the same answer: start again without one.
    /// </summary>
    [Fact]
    public void A_cursor_whose_sort_value_is_not_a_timestamp_is_refused()
    {
        var cursor = ReferenceCursor.Encode(
            "12", Guid.NewGuid().ToString("D"), TestRunHistoryScope.Build(Workspace, Recipe, new TestRunHistoryFilters()));

        Assert.False(TryCreate(new TestRunHistoryViewModel(Cursor: cursor), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
    }

    [Fact]
    public void A_cursor_whose_tie_breaker_is_not_an_id_is_refused()
    {
        var cursor = ReferenceCursor.Encode(
            Noon.ToString("O", CultureInfo.InvariantCulture),
            "not-an-id",
            TestRunHistoryScope.Build(Workspace, Recipe, new TestRunHistoryFilters()));

        Assert.False(TryCreate(new TestRunHistoryViewModel(Cursor: cursor), out _, out var error));

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error!.Code);
    }

    // ---- Page size ----

    [Theory]
    [InlineData(null, ReferencePolicy.DefaultPageSize)]
    [InlineData(10, 10)]
    [InlineData(0, ReferencePolicy.MinPageSize)]
    [InlineData(-5, ReferencePolicy.MinPageSize)]
    [InlineData(100_000, ReferencePolicy.MaxPageSize)]
    public void The_page_size_is_clamped_rather_than_refused(int? requested, int expected)
    {
        Assert.True(TryCreate(new TestRunHistoryViewModel(Limit: requested), out var criteria, out _));

        Assert.Equal(expected, criteria!.Limit);
    }

    /// <summary>
    /// The clamp is on the derived property, not a stored one — so a <c>with</c> expression cannot smuggle an
    /// unclamped page size past it.
    /// </summary>
    [Fact]
    public void An_unclamped_page_size_is_unrepresentable()
    {
        var criteria = new TestRunHistoryCriteria(Recipe, new TestRunHistoryFilters(), "scope")
            with { RequestedLimit = 10_000 };

        Assert.Equal(ReferencePolicy.MaxPageSize, criteria.Limit);
    }

    private static bool TryCreate(
        TestRunHistoryViewModel model,
        out TestRunHistoryCriteria? criteria,
        out OperationError? error) =>
        TestRunHistoryQueryFactory.TryCreate(model, Workspace, Recipe, out criteria, out error);

    private static string CursorFor(Guid workspaceId, Guid recipeId, DateTimeOffset testedAt, Guid? testRunId = null) =>
        ReferenceCursor.Encode(
            testedAt.ToString("O", CultureInfo.InvariantCulture),
            (testRunId ?? Guid.NewGuid()).ToString("D"),
            TestRunHistoryScope.Build(workspaceId, recipeId, new TestRunHistoryFilters()));
}
