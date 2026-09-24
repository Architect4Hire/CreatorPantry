using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The translation from a bound query string into typed criteria: what each filter parses to, what is refused
/// and how, and — the part with teeth — exactly which changes to a query invalidate a cursor issued for it.
/// </summary>
public sealed class RecipeSearchQueryFactoryTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid Membership = Guid.NewGuid();

    // ---- Absent filters ----

    /// <summary>
    /// An empty query string is not a set of filters that match nothing — it is the whole library, ordered the
    /// way the library opens, with a total.
    /// </summary>
    [Fact]
    public void An_absent_filter_is_not_a_filter()
    {
        var criteria = Created(new RecipeSearchViewModel());

        Assert.Null(criteria.Filters.Search);
        Assert.Null(criteria.Filters.TagIds);
        Assert.Null(criteria.Filters.CreatorMembershipIds);
        Assert.Null(criteria.Filters.LatestVersionReadiness);
        Assert.Null(criteria.Position);
        Assert.Equal(RecipeSearchSort.RecentlyUpdated, criteria.Sort);
        Assert.True(criteria.IncludeTotal);
    }

    /// <summary>
    /// <c>?status=</c> is what a client sends when it has just cleared a filter. Treating it as a filter matching
    /// nothing would make clearing one an error — it means "no status of my own", which is the default library.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void An_empty_filter_value_is_treated_as_absent(string status)
    {
        Assert.Equal(
            RecipePolicy.DefaultSearchStatuses,
            Created(new RecipeSearchViewModel(Status: status)).Filters.Statuses);
    }

    // ---- The default library ----

    /// <summary>
    /// A caller who named no status gets their library, not their archive (REC-006). Expressed in the
    /// criteria rather than hidden in the repository, so the predicate the query runs is the one the cursor
    /// is bound to.
    /// </summary>
    [Fact]
    public void A_search_with_no_status_excludes_archived_recipes()
    {
        var statuses = Created(new RecipeSearchViewModel()).Filters.Statuses;

        Assert.NotNull(statuses);
        Assert.DoesNotContain(RecipeStatus.Archived, statuses!);
    }

    /// <summary>
    /// Every other state is included, and the default is derived rather than listed — a state added to
    /// <see cref="RecipeStatus"/> tomorrow appears in the library automatically instead of silently vanishing
    /// from it.
    /// </summary>
    [Fact]
    public void The_default_includes_every_state_but_archived() =>
        Assert.Equal(
            Enum.GetValues<RecipeStatus>().Where(status => status != RecipeStatus.Archived),
            Created(new RecipeSearchViewModel()).Filters.Statuses!);

    /// <summary>
    /// And asking for the archive reaches it: the exclusion is the default only, never a permanent one, or
    /// archiving would lose a creator's work rather than shelving it.
    /// </summary>
    [Fact]
    public void Asking_for_archived_recipes_overrides_the_default() =>
        Assert.Equal(
            [RecipeStatus.Archived],
            Created(new RecipeSearchViewModel(Status: "Archived")).Filters.Statuses!);

    /// <summary>
    /// The default changes the cursor's scope, which is correct rather than incidental: a cursor minted for
    /// the library must not resume inside a search that also asked for archived recipes.
    /// </summary>
    [Fact]
    public void The_default_and_an_explicit_archived_filter_scope_cursors_differently() =>
        Assert.NotEqual(
            Created(new RecipeSearchViewModel()).Scope,
            Created(new RecipeSearchViewModel(Status: "Draft,Ready,Archived")).Scope);

    // ---- Parsing ----

    [Fact]
    public void A_comma_separated_list_is_split_and_trimmed()
    {
        var criteria = Created(new RecipeSearchViewModel(Status: " Draft , Ready "));

        Assert.Equal([RecipeStatus.Draft, RecipeStatus.Ready], criteria.Filters.Statuses);
    }

    [Fact]
    public void Enum_names_are_matched_without_regard_to_case()
    {
        var criteria = Created(new RecipeSearchViewModel(Readiness: "ready", Sort: "TITLE"));

        Assert.Equal(RecipeVersionReadiness.Ready, criteria.Filters.LatestVersionReadiness);
        Assert.Equal(RecipeSearchSort.Title, criteria.Sort);
    }

    [Fact]
    public void Ids_are_parsed_into_the_filter()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var criteria = Created(new RecipeSearchViewModel(Tag: $"{first:D},{second:D}"));

        Assert.Equal([first, second], criteria.Filters.TagIds);
    }

    /// <summary>
    /// Authorship is taken from the resolved context, never from the request. No route publishes another
    /// member's membership id, so an id-valued author filter would be one nobody could populate — and accepting
    /// one would invite a client to guess at other people's.
    /// </summary>
    [Fact]
    public void Mine_filters_on_the_callers_own_membership()
    {
        Assert.Equal(
            [Membership],
            Created(new RecipeSearchViewModel(Mine: true)).Filters.CreatorMembershipIds);

        Assert.Null(Created(new RecipeSearchViewModel(Mine: false)).Filters.CreatorMembershipIds);
        Assert.Null(Created(new RecipeSearchViewModel()).Filters.CreatorMembershipIds);
    }

    [Fact]
    public void A_term_below_the_length_floor_is_dropped_rather_than_refused()
    {
        Assert.Null(Created(new RecipeSearchViewModel(Search: "o")).Filters.Search);
        Assert.Equal("olive", Created(new RecipeSearchViewModel(Search: " Olive ")).Filters.Search);
    }

    [Theory]
    [InlineData(null, ReferencePolicy.DefaultPageSize)]
    [InlineData(0, ReferencePolicy.MinPageSize)]
    [InlineData(10_000, ReferencePolicy.MaxPageSize)]
    public void The_page_size_is_clamped_rather_than_refused(int? limit, int expected)
    {
        Assert.Equal(expected, Created(new RecipeSearchViewModel(Limit: limit)).Limit);
    }

    [Fact]
    public void A_total_is_asked_for_unless_the_caller_declines_it()
    {
        Assert.True(Created(new RecipeSearchViewModel()).IncludeTotal);
        Assert.True(Created(new RecipeSearchViewModel(IncludeTotal: true)).IncludeTotal);
        Assert.False(Created(new RecipeSearchViewModel(IncludeTotal: false)).IncludeTotal);
    }

    // ---- Refusals ----

    [Fact]
    public void An_unknown_enum_value_is_refused_and_names_what_is_accepted()
    {
        var error = Refused(new RecipeSearchViewModel(Status: "Published"));

        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, error.Code);

        var message = Assert.Single(error.FieldErrors["status"]);
        Assert.Contains("Published", message, StringComparison.Ordinal);
        Assert.Contains("Draft, Ready, Archived", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The numbers behind an enum are an implementation detail, never part of the published contract — so a
    /// numeric form is refused even when it would have landed on a real member.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("-1")]
    public void A_numeric_enum_value_is_refused(string readiness)
    {
        Assert.Equal(
            RecipeErrorCodes.SearchInvalidRequest,
            Refused(new RecipeSearchViewModel(Readiness: readiness)).Code);
    }

    [Fact]
    public void A_malformed_id_is_refused_naming_the_parameter_it_arrived_on()
    {
        var error = Refused(new RecipeSearchViewModel(Cuisine: "not-a-guid"));

        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, error.Code);
        Assert.True(error.FieldErrors.ContainsKey("cuisine"));
    }

    /// <summary>
    /// Every bad filter is reported at once. Reporting one at a time would make a client with three mistakes
    /// discover them over three round trips.
    /// </summary>
    [Fact]
    public void Every_bad_filter_is_reported_together()
    {
        var error = Refused(new RecipeSearchViewModel(
            Status: "Published", Tag: "nope", IngredientReview: "Whatever"));

        Assert.Equal(
            (string[])["ingredientReview", "status", "tag"],
            error.FieldErrors.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A mistyped filter changes the scope, so the cursor really is invalid too — which is exactly why saying so
    /// would be true and useless. The caller is told about the filter they can fix.
    /// </summary>
    [Fact]
    public void A_bad_filter_is_reported_before_the_cursor()
    {
        var error = Refused(new RecipeSearchViewModel(Status: "Published", Cursor: "not-a-cursor"));

        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, error.Code);
        Assert.True(error.FieldErrors.ContainsKey("status"));
    }

    // ---- Scope ----

    /// <summary>
    /// The same query written two ways is one scope. Without this, a client that reordered its own filter values
    /// between pages would have its cursor refused for a reason it could not see.
    /// </summary>
    [Fact]
    public void Reordering_a_list_filter_does_not_change_the_scope()
    {
        Assert.Equal(
            Created(new RecipeSearchViewModel(Status: "Draft,Ready")).Scope,
            Created(new RecipeSearchViewModel(Status: "Ready,Draft")).Scope);
    }

    /// <summary>
    /// A position is a position: how many rows a caller wants after it, and whether they also want a count, do
    /// not change which rows follow.
    /// </summary>
    [Fact]
    public void Neither_the_page_size_nor_the_total_is_part_of_the_scope()
    {
        var baseline = Created(new RecipeSearchViewModel()).Scope;

        Assert.Equal(baseline, Created(new RecipeSearchViewModel(Limit: 100)).Scope);
        Assert.Equal(baseline, Created(new RecipeSearchViewModel(IncludeTotal: false)).Scope);
    }

    [Fact]
    public void The_workspace_the_sort_and_every_filter_change_the_scope()
    {
        var baseline = Created(new RecipeSearchViewModel()).Scope;

        // The workspace is the one nothing in the shared paging kernel would have put here, and the one whose
        // absence would let a cursor page another creator's library.
        Assert.NotEqual(baseline, Created(new RecipeSearchViewModel(), workspaceId: Guid.NewGuid()).Scope);

        (string Name, RecipeSearchViewModel Model)[] variations =
        [
            ("sort", new RecipeSearchViewModel(Sort: "Title")),
            ("search", new RecipeSearchViewModel(Search: "olive")),
            ("status", new RecipeSearchViewModel(Status: "Draft")),
            ("tag", new RecipeSearchViewModel(Tag: $"{Guid.NewGuid():D}")),
            ("cuisine", new RecipeSearchViewModel(Cuisine: $"{Guid.NewGuid():D}")),
            ("course", new RecipeSearchViewModel(Course: $"{Guid.NewGuid():D}")),
            ("mine", new RecipeSearchViewModel(Mine: true)),
            ("readiness", new RecipeSearchViewModel(Readiness: "Ready")),
            ("ingredientReview", new RecipeSearchViewModel(IngredientReview: "HasUnmatched")),
            ("updatedFrom", new RecipeSearchViewModel(UpdatedFrom: DateTimeOffset.UnixEpoch)),
            ("updatedBefore", new RecipeSearchViewModel(UpdatedBefore: DateTimeOffset.UnixEpoch)),
            ("createdFrom", new RecipeSearchViewModel(CreatedFrom: DateTimeOffset.UnixEpoch)),
            ("createdBefore", new RecipeSearchViewModel(CreatedBefore: DateTimeOffset.UnixEpoch)),
        ];

        foreach (var (name, model) in variations)
        {
            Assert.NotEqual(baseline, Created(model).Scope);
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    // ---- Cursors ----

    [Fact]
    public void A_cursor_from_the_same_query_resumes_it()
    {
        var recipeId = Guid.NewGuid();
        var model = new RecipeSearchViewModel(Status: "Draft");
        var cursor = CursorFor(model, DateTimeOffset.UnixEpoch, recipeId);

        var criteria = Created(model with { Cursor = cursor });

        Assert.NotNull(criteria.Position);
        Assert.Equal(recipeId, criteria.Position!.RecipeId);
        Assert.Equal(DateTimeOffset.UnixEpoch, criteria.Position.UpdatedAt);
    }

    /// <summary>
    /// The case the shared paging kernel would not have caught, because reference data has no workspace to bind:
    /// the same filters and ordering in a different workspace name a different ordered set, and a cursor that
    /// crossed would produce a plausible page of the wrong creator's recipes.
    /// </summary>
    [Fact]
    public void A_cursor_from_another_workspace_is_refused()
    {
        var model = new RecipeSearchViewModel();
        var cursor = CursorFor(model, DateTimeOffset.UnixEpoch, Guid.NewGuid());

        var error = Refused(model with { Cursor = cursor }, workspaceId: Guid.NewGuid());

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, error.Code);
        Assert.True(error.FieldErrors.ContainsKey("cursor"));
    }

    [Fact]
    public void A_cursor_from_another_ordering_is_refused()
    {
        var model = new RecipeSearchViewModel();
        var cursor = CursorFor(model, DateTimeOffset.UnixEpoch, Guid.NewGuid());

        Assert.Equal(
            RecipeErrorCodes.CursorInvalidRequest,
            Refused(model with { Cursor = cursor, Sort = "Title" }).Code);
    }

    [Fact]
    public void A_cursor_from_another_set_of_filters_is_refused()
    {
        var model = new RecipeSearchViewModel(Status: "Draft");
        var cursor = CursorFor(model, DateTimeOffset.UnixEpoch, Guid.NewGuid());

        Assert.Equal(
            RecipeErrorCodes.CursorInvalidRequest,
            Refused(model with { Cursor = cursor, Status = "Ready" }).Code);
    }

    /// <summary>
    /// Bound to this exact query and still not a position in it. Only reachable by editing a cursor by hand, and
    /// answered the same way: start again without one, rather than with an exception from a query.
    /// </summary>
    [Fact]
    public void A_cursor_that_is_not_a_position_in_this_ordering_is_refused()
    {
        var model = new RecipeSearchViewModel();
        var scope = RecipeSearchScope.Build(Workspace, RecipeSearchSort.RecentlyUpdated, Created(model).Filters);

        // A tie-breaker that is not an id, under an ordering whose sort value must be a timestamp.
        var cursor = ReferenceCursor.Encode("not-a-timestamp", "not-an-id", scope);

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, Refused(model with { Cursor = cursor }).Code);
    }

    // ---- Helpers ----

    private static RecipeSearchCriteria Created(RecipeSearchViewModel model, Guid? workspaceId = null)
    {
        Assert.True(
            RecipeSearchQueryFactory.TryCreate(
                model, workspaceId ?? Workspace, Membership, out var criteria, out var error),
            $"expected the query to translate, but it was refused: {error?.Code}");

        Assert.Null(error);

        return criteria!;
    }

    private static Domain.Managers.Results.OperationError Refused(
        RecipeSearchViewModel model, Guid? workspaceId = null)
    {
        Assert.False(
            RecipeSearchQueryFactory.TryCreate(
                model, workspaceId ?? Workspace, Membership, out var criteria, out var error),
            "expected the query to be refused, but it translated");

        Assert.Null(criteria);

        return error!;
    }

    /// <summary>
    /// Mints the cursor this query would have issued, the way Business does — from the scope the factory builds
    /// for the same model, so the test cannot accidentally agree with itself about a scope the product does not
    /// produce.
    /// </summary>
    private static string CursorFor(RecipeSearchViewModel model, DateTimeOffset updatedAt, Guid recipeId)
    {
        var criteria = Created(model);

        var row = new RecipeSummaryRecord(
            recipeId, "Anything", null, RecipeStatus.Draft, null, null, Guid.NewGuid(), Guid.NewGuid(),
            updatedAt, updatedAt, null, null, false, criteria.Sort);

        return ReferenceCursor.Encode(row.SortValue, row.TieBreaker, criteria.Scope);
    }
}
