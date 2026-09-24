using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The recipe search query against a real SQL Server. These are the tests that prove the keyset predicate, the
/// correlated subqueries and the filters actually translate and actually agree with the <c>ORDER BY</c> — a
/// query that compiles in C# and throws, or silently repeats a row, would pass every test above this layer.
/// </summary>
/// <remarks>
/// Each test starts from an empty recipe table, which is what lets them assert totals rather than only the
/// presence of a known id. Deleting is done in SQL because <c>RecipeVersion</c> is an immutable record and
/// <c>ImmutableRecordInterceptor</c> refuses to delete one through the change tracker — correctly, and the
/// alternative would be a test that could not clean up after itself.
/// </remarks>
public sealed class RecipeSearchRepositoryTests(SqlServerRecipeFixture fixture)
    : IClassFixture<SqlServerRecipeFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    /// <summary>
    /// Stands in for the real cursor scope. Any string does here, because the repository never reads it — a
    /// cursor is bound to a resource and filters that a repository is not told about, so minting one is
    /// Business's job. These tests only need the scope they encode a position under to match the one they decode
    /// it with, which <see cref="PositionFrom"/> guarantees by using this same value.
    /// </summary>
    private const string TestScope = "t";

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Snapshots and versions first: a version's reference to its recipe is Restrict, which is exactly the
        // constraint that makes history outlive an edit. Everything else cascades from Recipes.
        await SqlServerRecipeFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM RecipeVersionSnapshots;
            DELETE FROM RecipeVersions;
            DELETE FROM Recipes;
            """,
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- Filters -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unfiltered_search_returns_every_recipe_in_the_workspace()
    {
        await AddAsync(title: "Olive oil cake");
        await AddAsync(title: "Weeknight chilli");

        var rows = await SearchAsync(new RecipeSearchFilters());

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task A_status_filter_includes_only_the_named_statuses()
    {
        await AddAsync(title: "Draft one", status: RecipeStatus.Draft);
        await AddAsync(title: "Ready one", status: RecipeStatus.Ready);
        await AddAsync(title: "Archived one", status: RecipeStatus.Archived);

        var rows = await SearchAsync(new RecipeSearchFilters(
            Statuses: [RecipeStatus.Draft, RecipeStatus.Archived]));

        Assert.Equal(["Archived one", "Draft one"], rows.Select(row => row.Title).Order());
    }

    /// <summary>
    /// An empty status list is the absence of a filter, not a filter matching nothing — which is what a client
    /// that sent no status meant, archived recipes included.
    /// </summary>
    [Fact]
    public async Task An_empty_status_list_filters_nothing_out()
    {
        await AddAsync(title: "Archived one", status: RecipeStatus.Archived);

        var rows = await SearchAsync(new RecipeSearchFilters(Statuses: []));

        Assert.Single(rows);
    }

    [Fact]
    public async Task A_tag_filter_includes_only_recipes_carrying_one_of_the_tags()
    {
        await AddAsync(title: "Tagged", tagIds: [SqlServerRecipeFixture.TagIdA]);
        await AddAsync(title: "Other tag", tagIds: [SqlServerRecipeFixture.SecondTagIdA]);
        await AddAsync(title: "Untagged");

        var rows = await SearchAsync(new RecipeSearchFilters(TagIds: [SqlServerRecipeFixture.TagIdA]));

        Assert.Equal("Tagged", Assert.Single(rows).Title);
    }

    /// <summary>
    /// The filter is a semi-join, so a recipe carrying two of the requested tags appears once. A plain join
    /// would return it twice, which would also corrupt the page size and the cursor that follows it.
    /// </summary>
    [Fact]
    public async Task A_recipe_carrying_two_of_the_requested_tags_appears_once()
    {
        await AddAsync(
            title: "Both tags",
            tagIds: [SqlServerRecipeFixture.TagIdA, SqlServerRecipeFixture.SecondTagIdA]);

        var rows = await SearchAsync(new RecipeSearchFilters(
            TagIds: [SqlServerRecipeFixture.TagIdA, SqlServerRecipeFixture.SecondTagIdA]));

        Assert.Single(rows);
    }

    [Fact]
    public async Task A_cuisine_filter_excludes_the_other_cuisine()
    {
        await AddAsync(title: "Italian", cuisineId: SqlServerRecipeFixture.CuisineId);
        await AddAsync(title: "Thai", cuisineId: SqlServerRecipeFixture.OtherCuisineId);
        await AddAsync(title: "Unsaid", cuisineId: null);

        var rows = await SearchAsync(new RecipeSearchFilters(
            CuisineIds: [SqlServerRecipeFixture.CuisineId]));

        Assert.Equal("Italian", Assert.Single(rows).Title);
    }

    [Fact]
    public async Task A_course_filter_excludes_recipes_with_no_course()
    {
        await AddAsync(title: "Dessert", courseId: SqlServerRecipeFixture.CourseId);
        await AddAsync(title: "Unsaid", courseId: null);

        var rows = await SearchAsync(new RecipeSearchFilters(
            CourseIds: [SqlServerRecipeFixture.CourseId]));

        Assert.Equal("Dessert", Assert.Single(rows).Title);
    }

    /// <summary>
    /// The author filter reads <c>CreatedByMembershipId</c>. A later editor does not become the recipe's
    /// creator, so a recipe written by one member and last touched by another still belongs to the first.
    /// </summary>
    [Fact]
    public async Task An_author_filter_reads_the_creator_not_the_last_editor()
    {
        await AddAsync(
            title: "Written by one",
            authorId: SqlServerRecipeFixture.AuthorOne,
            lastEditorId: SqlServerRecipeFixture.AuthorTwo);
        await AddAsync(title: "Written by two", authorId: SqlServerRecipeFixture.AuthorTwo);

        var rows = await SearchAsync(new RecipeSearchFilters(
            CreatorMembershipIds: [SqlServerRecipeFixture.AuthorOne]));

        Assert.Equal("Written by one", Assert.Single(rows).Title);
    }

    /// <summary>
    /// The readiness filter reads the <em>latest</em> version. A recipe that was declared ready and has since
    /// been edited back into a draft version is not ready — and this is the case that separates "latest is
    /// ready" from "has ever had a ready version", which would have answered the opposite.
    /// </summary>
    [Fact]
    public async Task A_readiness_filter_reads_the_latest_version_not_any_version()
    {
        await AddAsync(
            title: "Ready then drafted",
            versionReadiness: [RecipeVersionReadiness.Ready, RecipeVersionReadiness.Draft]);
        await AddAsync(
            title: "Drafted then ready",
            versionReadiness: [RecipeVersionReadiness.Draft, RecipeVersionReadiness.Ready]);

        var rows = await SearchAsync(new RecipeSearchFilters(
            LatestVersionReadiness: RecipeVersionReadiness.Ready));

        Assert.Equal("Drafted then ready", Assert.Single(rows).Title);
    }

    [Fact]
    public async Task An_unmatched_ingredient_filter_finds_only_recipes_with_an_unresolved_line()
    {
        await AddAsync(title: "All matched", lineStatuses: [IngredientMatchStatus.Matched]);
        await AddAsync(
            title: "One ambiguous",
            lineStatuses: [IngredientMatchStatus.Matched, IngredientMatchStatus.Ambiguous]);

        var rows = await SearchAsync(new RecipeSearchFilters(
            IngredientReview: RecipeIngredientReviewFilter.HasUnmatched));

        Assert.Equal("One ambiguous", Assert.Single(rows).Title);
    }

    /// <summary>
    /// A recipe with no ingredient lines has nothing left to resolve, so it counts as fully matched. Vacuous,
    /// and the honest answer: the alternative would report an unresolved line the recipe does not have.
    /// </summary>
    [Fact]
    public async Task A_recipe_with_no_ingredient_lines_counts_as_fully_matched()
    {
        await AddAsync(title: "No lines");
        await AddAsync(title: "One unmatched", lineStatuses: [IngredientMatchStatus.NoMatch]);

        var rows = await SearchAsync(new RecipeSearchFilters(
            IngredientReview: RecipeIngredientReviewFilter.AllMatched));

        Assert.Equal("No lines", Assert.Single(rows).Title);
    }

    /// <summary>
    /// Half-open bounds: inclusive below, exclusive above, so adjacent ranges neither overlap nor leave a gap
    /// and a caller does not have to know the precision the column stores.
    /// </summary>
    [Fact]
    public async Task Date_bounds_are_inclusive_below_and_exclusive_above()
    {
        await AddAsync(title: "Before", updatedAt: Now.AddDays(-1));
        await AddAsync(title: "On the lower bound", updatedAt: Now);
        await AddAsync(title: "On the upper bound", updatedAt: Now.AddDays(1));

        var rows = await SearchAsync(new RecipeSearchFilters(
            UpdatedOnOrAfter: Now,
            UpdatedBefore: Now.AddDays(1)));

        Assert.Equal("On the lower bound", Assert.Single(rows).Title);
    }

    [Fact]
    public async Task Filters_from_different_dimensions_narrow_together()
    {
        await AddAsync(
            title: "Everything",
            status: RecipeStatus.Ready,
            cuisineId: SqlServerRecipeFixture.CuisineId,
            authorId: SqlServerRecipeFixture.AuthorOne,
            tagIds: [SqlServerRecipeFixture.TagIdA],
            versionReadiness: [RecipeVersionReadiness.Ready]);

        // Each of these differs from the row above in exactly one dimension, so a filter silently dropped from
        // the query shows up as one of them arriving in the result.
        await AddAsync(title: "Wrong status", status: RecipeStatus.Draft, cuisineId: SqlServerRecipeFixture.CuisineId, authorId: SqlServerRecipeFixture.AuthorOne, tagIds: [SqlServerRecipeFixture.TagIdA], versionReadiness: [RecipeVersionReadiness.Ready]);
        await AddAsync(title: "Wrong cuisine", status: RecipeStatus.Ready, cuisineId: SqlServerRecipeFixture.OtherCuisineId, authorId: SqlServerRecipeFixture.AuthorOne, tagIds: [SqlServerRecipeFixture.TagIdA], versionReadiness: [RecipeVersionReadiness.Ready]);
        await AddAsync(title: "Wrong author", status: RecipeStatus.Ready, cuisineId: SqlServerRecipeFixture.CuisineId, authorId: SqlServerRecipeFixture.AuthorTwo, tagIds: [SqlServerRecipeFixture.TagIdA], versionReadiness: [RecipeVersionReadiness.Ready]);
        await AddAsync(title: "Wrong tag", status: RecipeStatus.Ready, cuisineId: SqlServerRecipeFixture.CuisineId, authorId: SqlServerRecipeFixture.AuthorOne, tagIds: [SqlServerRecipeFixture.SecondTagIdA], versionReadiness: [RecipeVersionReadiness.Ready]);
        await AddAsync(title: "Wrong readiness", status: RecipeStatus.Ready, cuisineId: SqlServerRecipeFixture.CuisineId, authorId: SqlServerRecipeFixture.AuthorOne, tagIds: [SqlServerRecipeFixture.TagIdA], versionReadiness: [RecipeVersionReadiness.Draft]);

        var rows = await SearchAsync(new RecipeSearchFilters(
            Statuses: [RecipeStatus.Ready],
            CuisineIds: [SqlServerRecipeFixture.CuisineId],
            CreatorMembershipIds: [SqlServerRecipeFixture.AuthorOne],
            TagIds: [SqlServerRecipeFixture.TagIdA],
            LatestVersionReadiness: RecipeVersionReadiness.Ready));

        Assert.Equal("Everything", Assert.Single(rows).Title);
    }

    // --- Search terms ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_search_matches_the_title_or_the_description()
    {
        await AddAsync(title: "Olive oil cake");
        await AddAsync(title: "Plain loaf", description: "Uses olive oil.");
        await AddAsync(title: "Chilli", description: "No fat to speak of.");

        var rows = await SearchAsync(new RecipeSearchFilters(
            Search: RecipeSearchPolicy.NormalizeSearch("Olive Oil")));

        Assert.Equal(["Olive oil cake", "Plain loaf"], rows.Select(row => row.Title).Order());
    }

    /// <summary>
    /// A term is matched literally. If it became a <c>LIKE</c> pattern instead, a title containing a percent
    /// sign would be matched by any term at all, and a term containing one would match the whole library.
    /// </summary>
    [Fact]
    public async Task A_search_term_containing_a_wildcard_matches_literally()
    {
        await AddAsync(title: "100% rye");
        await AddAsync(title: "Sourdough");

        var wildcard = await SearchAsync(new RecipeSearchFilters(
            Search: RecipeSearchPolicy.NormalizeSearch("0% r")));

        Assert.Equal("100% rye", Assert.Single(wildcard).Title);

        var underscore = await SearchAsync(new RecipeSearchFilters(
            Search: RecipeSearchPolicy.NormalizeSearch("S_urdough")));

        Assert.Empty(underscore);
    }

    /// <summary>A single character is ignored rather than applied, so this is an unfiltered search.</summary>
    [Fact]
    public async Task A_term_below_the_length_floor_is_not_a_filter()
    {
        await AddAsync(title: "Olive oil cake");
        await AddAsync(title: "Chilli");

        var rows = await SearchAsync(new RecipeSearchFilters(
            Search: RecipeSearchPolicy.NormalizeSearch("o")));

        Assert.Equal(2, rows.Count);
    }

    // --- Ordering ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_order_is_most_recently_updated_first()
    {
        await AddAsync(title: "Oldest", updatedAt: Now.AddDays(-2));
        await AddAsync(title: "Newest", updatedAt: Now);
        await AddAsync(title: "Middle", updatedAt: Now.AddDays(-1));

        var rows = await SearchAsync(new RecipeSearchFilters());

        Assert.Equal(["Newest", "Middle", "Oldest"], rows.Select(row => row.Title));
    }

    [Fact]
    public async Task Title_order_is_alphabetical()
    {
        await AddAsync(title: "Cake", updatedAt: Now);
        await AddAsync(title: "Apple pie", updatedAt: Now.AddDays(-5));
        await AddAsync(title: "Bread", updatedAt: Now.AddDays(-1));

        var rows = await SearchAsync(new RecipeSearchFilters(), RecipeSearchSort.Title);

        Assert.Equal(["Apple pie", "Bread", "Cake"], rows.Select(row => row.Title));
    }

    /// <summary>
    /// The property the whole keyset depends on: the ordering is total, so walking it one row at a time visits
    /// exactly the same rows in exactly the same order as reading it in one page.
    /// </summary>
    /// <remarks>
    /// Every row here shares one <c>UpdatedAt</c> and one <c>Title</c>, so only the tie-break separates them.
    /// That is deliberately the hardest case: if the <c>WHERE</c> and the <c>ORDER BY</c> disagreed about the
    /// tie-break — by a column, a direction, or by the database ordering GUIDs differently than C# does — a row
    /// would be repeated or skipped here and nowhere else. The expected order is never written down in C#,
    /// because what matters is that the two agree with each other, not which of them a GUID sorts before.
    /// </remarks>
    [Theory]
    [InlineData(RecipeSearchSort.RecentlyUpdated)]
    [InlineData(RecipeSearchSort.Title)]
    public async Task Rows_that_tie_on_the_sort_value_still_have_one_total_order(RecipeSearchSort sort)
    {
        for (var index = 0; index < 5; index++)
        {
            await AddAsync(title: "Identical", updatedAt: Now);
        }

        var wholePage = await SearchAsync(new RecipeSearchFilters(), sort, limit: 100);
        var oneAtATime = await DrainAsync(new RecipeSearchFilters(), sort, limit: 1);

        Assert.Equal(5, wholePage.Count);
        Assert.Equal(wholePage.Select(row => row.Id), oneAtATime.Select(row => row.Id));
    }

    // --- Paging ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RecipeSearchSort.RecentlyUpdated)]
    [InlineData(RecipeSearchSort.Title)]
    public async Task Paging_to_the_end_yields_every_row_once_in_order(RecipeSearchSort sort)
    {
        // Recency ascends as the title ascends, so the two orderings are exact opposites of each other. A sort
        // key silently ignored — both branches returning whichever order the table happened to be in — would
        // otherwise pass under either expectation.
        for (var index = 0; index < 11; index++)
        {
            await AddAsync(title: $"Recipe {index:00}", updatedAt: Now.AddMinutes(index));
        }

        var all = await DrainAsync(new RecipeSearchFilters(), sort, limit: 4);

        Assert.Equal(11, all.Count);
        Assert.Equal(11, all.Select(row => row.Id).Distinct().Count());

        var expected = sort is RecipeSearchSort.Title
            ? all.Select(row => row.Title).Order(StringComparer.Ordinal)
            : all.Select(row => row.Title).OrderDescending(StringComparer.Ordinal);

        Assert.Equal(expected, all.Select(row => row.Title));
    }

    [Fact]
    public async Task The_last_page_has_no_next_position()
    {
        await AddAsync(title: "Only one");

        var (rows, hasMore) = await PageAsync(new RecipeSearchFilters(), limit: 25);

        Assert.Single(rows);
        Assert.False(hasMore);
    }

    /// <summary>
    /// A page that exactly fills the limit still reports nothing following. The probe row is what makes that
    /// distinguishable — without it a full page is ambiguous, and a client would ask for one page too many.
    /// </summary>
    [Fact]
    public async Task An_exactly_full_final_page_reports_nothing_following()
    {
        for (var index = 0; index < 3; index++)
        {
            await AddAsync(title: $"Recipe {index}", updatedAt: Now.AddMinutes(-index));
        }

        var (rows, hasMore) = await PageAsync(new RecipeSearchFilters(), limit: 3);

        Assert.Equal(3, rows.Count);
        Assert.False(hasMore);
    }

    /// <summary>
    /// The page size is clamped by the criteria itself, so no caller — a controller, a worker, an AI plugin —
    /// can ask the repository for an unbounded read.
    /// </summary>
    [Theory]
    [InlineData(null, ReferencePolicy.DefaultPageSize)]
    [InlineData(0, ReferencePolicy.MinPageSize)]
    [InlineData(-5, ReferencePolicy.MinPageSize)]
    [InlineData(10_000, ReferencePolicy.MaxPageSize)]
    [InlineData(7, 7)]
    public void A_requested_page_size_is_clamped(int? requested, int expected)
    {
        var criteria = new RecipeSearchCriteria(new RecipeSearchFilters(), TestScope, RequestedLimit: requested);

        Assert.Equal(expected, criteria.Limit);
    }

    /// <summary>
    /// Clamping cannot be got round by copying the criteria, because the limit is derived on read rather than
    /// stored — a record's copy constructor would have carried a stale one straight through.
    /// </summary>
    [Fact]
    public void A_copied_criteria_reclamps_its_page_size()
    {
        var criteria = new RecipeSearchCriteria(new RecipeSearchFilters(), TestScope, RequestedLimit: 10);

        Assert.Equal(ReferencePolicy.MaxPageSize, (criteria with { RequestedLimit = 10_000 }).Limit);
    }

    [Fact]
    public async Task A_page_size_clamped_up_from_zero_still_pages_to_the_end()
    {
        for (var index = 0; index < 4; index++)
        {
            await AddAsync(title: $"Recipe {index}", updatedAt: Now.AddMinutes(-index));
        }

        var all = await DrainAsync(new RecipeSearchFilters(), RecipeSearchSort.RecentlyUpdated, limit: 0);

        Assert.Equal(4, all.Count);
    }

    // --- Counting ----------------------------------------------------------------------------------------

    /// <summary>
    /// The count and the page are built from one filter, so they cannot describe different sets. This is the
    /// assertion that catches them drifting apart — the failure that would make a total worse than no total.
    /// </summary>
    [Fact]
    public async Task The_count_agrees_with_a_full_drain_under_every_filter()
    {
        await AddAsync(title: "Ready italian", status: RecipeStatus.Ready, cuisineId: SqlServerRecipeFixture.CuisineId, tagIds: [SqlServerRecipeFixture.TagIdA], versionReadiness: [RecipeVersionReadiness.Ready], lineStatuses: [IngredientMatchStatus.Matched]);
        await AddAsync(title: "Draft italian", status: RecipeStatus.Draft, cuisineId: SqlServerRecipeFixture.CuisineId, lineStatuses: [IngredientMatchStatus.NoMatch]);
        await AddAsync(title: "Ready thai", status: RecipeStatus.Ready, cuisineId: SqlServerRecipeFixture.OtherCuisineId, tagIds: [SqlServerRecipeFixture.SecondTagIdA]);
        await AddAsync(title: "Archived", status: RecipeStatus.Archived, updatedAt: Now.AddYears(-1));

        RecipeSearchFilters[] cases =
        [
            new(),
            new(Statuses: [RecipeStatus.Ready]),
            new(CuisineIds: [SqlServerRecipeFixture.CuisineId]),
            new(TagIds: [SqlServerRecipeFixture.TagIdA, SqlServerRecipeFixture.SecondTagIdA]),
            new(LatestVersionReadiness: RecipeVersionReadiness.Ready),
            new(IngredientReview: RecipeIngredientReviewFilter.HasUnmatched),
            new(Search: RecipeSearchPolicy.NormalizeSearch("italian")),
            new(UpdatedOnOrAfter: Now),
            new(Statuses: [RecipeStatus.Ready], CuisineIds: [SqlServerRecipeFixture.CuisineId]),
        ];

        foreach (var filters in cases)
        {
            var drained = await DrainAsync(filters, RecipeSearchSort.RecentlyUpdated, limit: 2);
            var counted = await CountAsync(filters);

            Assert.Equal(drained.Count, counted);
        }
    }

    /// <summary>
    /// A total is a total: it counts the whole filtered set, not what is left after the current page, and not
    /// what would fit on one.
    /// </summary>
    [Fact]
    public async Task The_count_ignores_the_position_and_the_page_size()
    {
        for (var index = 0; index < 6; index++)
        {
            await AddAsync(title: $"Recipe {index}", updatedAt: Now.AddMinutes(-index));
        }

        var (rows, _) = await PageAsync(new RecipeSearchFilters(), limit: 2);
        var position = PositionFrom(rows[^1], RecipeSearchSort.RecentlyUpdated);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var counted = await Search(scope).CountAsync(
            new RecipeSearchCriteria(new RecipeSearchFilters(), TestScope, Position: position, RequestedLimit: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, counted);
    }

    // --- Projection --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_summary_carries_the_latest_versions_number_and_readiness()
    {
        await AddAsync(
            title: "Three versions",
            versionReadiness: [RecipeVersionReadiness.Draft, RecipeVersionReadiness.Ready, RecipeVersionReadiness.Draft]);

        var row = Assert.Single(await SearchAsync(new RecipeSearchFilters()));

        Assert.Equal(3, row.LatestVersionNumber);
        Assert.Equal(RecipeVersionReadiness.Draft, row.LatestVersionReadiness);
    }

    /// <summary>A recipe with no versions reports none, rather than a version zero that never existed.</summary>
    [Fact]
    public async Task A_recipe_with_no_versions_reports_no_latest_version()
    {
        await AddAsync(title: "No history");

        var row = Assert.Single(await SearchAsync(new RecipeSearchFilters()));

        Assert.Null(row.LatestVersionNumber);
        Assert.Null(row.LatestVersionReadiness);
    }

    /// <summary>
    /// The unresolved-line flag is on every row, not only when it is filtered on, because a creator cannot act
    /// on a filter whose result they cannot see.
    /// </summary>
    [Fact]
    public async Task A_summary_reports_unresolved_ingredient_lines_without_being_asked_to_filter_on_them()
    {
        await AddAsync(title: "Clean", updatedAt: Now, lineStatuses: [IngredientMatchStatus.Matched]);
        await AddAsync(title: "Unresolved", updatedAt: Now.AddMinutes(-1), lineStatuses: [IngredientMatchStatus.Matched, IngredientMatchStatus.NoMatch]);

        var rows = await SearchAsync(new RecipeSearchFilters());

        Assert.False(rows[0].HasUnmatchedIngredients);
        Assert.True(rows[1].HasUnmatchedIngredients);
    }

    /// <summary>
    /// A recipe with many children still occupies one row. Nothing in the projection joins a collection — a
    /// search over a library must not read every ingredient line and instruction step it lists, and a join
    /// instead of a semi-join would also corrupt the page size and every cursor after it.
    /// </summary>
    [Fact]
    public async Task A_recipe_with_many_children_is_one_row()
    {
        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.Recipes.Add(SqlServerRecipeFixture.NewRecipe("Fully populated", SqlServerRecipeFixture.TagIdA));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Single(await SearchAsync(new RecipeSearchFilters()));
        Assert.Equal(1, await CountAsync(new RecipeSearchFilters()));
    }

    // --- Workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// Both workspaces hold a recipe with the same title, so isolation cannot pass by the two being
    /// distinguishable. Another workspace's recipe is not filtered out of the answer — it is not visible to the
    /// query at all, which is what lets the read seam above answer 404 without disclosing that it is real.
    /// </summary>
    [Fact]
    public async Task Each_workspace_searches_and_counts_only_its_own_recipes()
    {
        await AddAsync(title: "Shared title");
        await AddAsync(title: "Shared title", workspaceId: SqlServerRecipeFixture.WorkspaceB);
        await AddAsync(title: "Also in B", workspaceId: SqlServerRecipeFixture.WorkspaceB);

        var inA = await SearchAsync(new RecipeSearchFilters());
        var inB = await SearchAsync(new RecipeSearchFilters(), workspaceId: SqlServerRecipeFixture.WorkspaceB);

        Assert.Single(inA);
        Assert.Equal(2, inB.Count);
        Assert.Equal(1, await CountAsync(new RecipeSearchFilters()));
        Assert.Equal(2, await CountAsync(new RecipeSearchFilters(), SqlServerRecipeFixture.WorkspaceB));
        Assert.Empty(inA.Select(row => row.Id).Intersect(inB.Select(row => row.Id)));
    }

    /// <summary>
    /// Both workspaces have a tag named "Weeknight". Filtering by A's tag id from inside B must find nothing —
    /// not B's identically named tag, and certainly not A's recipe.
    /// </summary>
    [Fact]
    public async Task A_tag_filter_cannot_reach_the_other_workspaces_identically_named_tag()
    {
        await AddAsync(title: "Tagged in A", tagIds: [SqlServerRecipeFixture.TagIdA]);
        await AddAsync(
            title: "Tagged in B",
            workspaceId: SqlServerRecipeFixture.WorkspaceB,
            tagIds: [SqlServerRecipeFixture.TagIdB]);

        var fromB = await SearchAsync(
            new RecipeSearchFilters(TagIds: [SqlServerRecipeFixture.TagIdA]),
            workspaceId: SqlServerRecipeFixture.WorkspaceB);

        Assert.Empty(fromB);
    }

    /// <summary>
    /// A position minted while reading one workspace, replayed against the other, pages that other workspace's
    /// rows and never reaches across. The scope fingerprint is what turns this into a rejected cursor rather
    /// than a wrong page, and binding the workspace into that scope is the read seam's job — this is the
    /// evidence that the repository does not leak even when the check above it has been bypassed entirely.
    /// </summary>
    [Fact]
    public async Task A_position_from_one_workspace_pages_only_the_other_workspaces_rows()
    {
        await AddAsync(title: "A one", updatedAt: Now);
        await AddAsync(title: "A two", updatedAt: Now.AddMinutes(-1));
        await AddAsync(title: "B one", workspaceId: SqlServerRecipeFixture.WorkspaceB, updatedAt: Now);
        await AddAsync(title: "B two", workspaceId: SqlServerRecipeFixture.WorkspaceB, updatedAt: Now.AddMinutes(-1));

        var (firstInA, _) = await PageAsync(new RecipeSearchFilters(), limit: 1);
        var position = PositionFrom(firstInA[0], RecipeSearchSort.RecentlyUpdated);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);

        var (rows, _) = await Search(scope).SearchAsync(
            new RecipeSearchCriteria(new RecipeSearchFilters(), TestScope, Position: position, RequestedLimit: 25),
            TestContext.Current.CancellationToken);

        Assert.All(rows, row => Assert.StartsWith("B ", row.Title));
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private static IRecipeSearchRepository Search(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeSearchRepository>();

    private async Task<IReadOnlyList<RecipeSummaryRecord>> SearchAsync(
        RecipeSearchFilters filters,
        RecipeSearchSort sort = RecipeSearchSort.RecentlyUpdated,
        int limit = 25,
        Guid? workspaceId = null)
    {
        var (rows, _) = await PageAsync(filters, limit, sort, position: null, workspaceId);

        return rows;
    }

    private async Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore)> PageAsync(
        RecipeSearchFilters filters,
        int limit,
        RecipeSearchSort sort = RecipeSearchSort.RecentlyUpdated,
        RecipeSearchPosition? position = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await Search(scope).SearchAsync(
            new RecipeSearchCriteria(filters, TestScope, sort, position, limit),
            TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync(RecipeSearchFilters filters, Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await Search(scope).CountAsync(
            new RecipeSearchCriteria(filters, TestScope),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Follows every page to the end, guarding against a query that never terminates.
    /// </summary>
    /// <remarks>
    /// The position is rebuilt by encoding the last row into a real cursor and decoding it back, which is the
    /// round trip Business and the read seam actually perform. Building the position directly from the row would
    /// skip the wire format, and a sort value that could not survive it — a truncated timestamp, say — would
    /// then only fail in production.
    /// </remarks>
    private async Task<List<RecipeSummaryRecord>> DrainAsync(
        RecipeSearchFilters filters,
        RecipeSearchSort sort,
        int limit)
    {
        var all = new List<RecipeSummaryRecord>();
        RecipeSearchPosition? position = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var (rows, hasMore) = await PageAsync(filters, limit, sort, position);
            all.AddRange(rows);

            if (!hasMore)
            {
                return all;
            }

            Assert.NotEmpty(rows);
            position = PositionFrom(rows[^1], sort);
        }

        Assert.Fail("paging did not terminate");

        return all;
    }

    private static RecipeSearchPosition PositionFrom(RecipeSummaryRecord row, RecipeSearchSort sort)
    {
        var encoded = ReferenceCursor.Encode(row.SortValue, row.TieBreaker, scope: "t");

        Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
        Assert.True(RecipeSearchPosition.TryCreate(sort, cursor!, out var position));

        return position!;
    }

    /// <summary>
    /// Seeds one recipe, and only the parts a test asked for. <c>WorkspaceId</c> is never set on anything —
    /// the ownership interceptor stamps it from the resolved context, and feature code assigning it is a defect.
    /// </summary>
    private async Task<Guid> AddAsync(
        string title,
        Guid? workspaceId = null,
        RecipeStatus status = RecipeStatus.Draft,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? createdAt = null,
        Guid? cuisineId = null,
        Guid? courseId = null,
        Guid? authorId = null,
        Guid? lastEditorId = null,
        string? description = null,
        IReadOnlyList<Guid>? tagIds = null,
        IReadOnlyList<IngredientMatchStatus>? lineStatuses = null,
        IReadOnlyList<RecipeVersionReadiness>? versionReadiness = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var author = authorId ?? SqlServerRecipeFixture.AuthorOne;

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Status = status,
            CuisineId = cuisineId,
            CourseId = courseId,
            CreatedByMembershipId = author,
            UpdatedByMembershipId = lastEditorId ?? author,
            CreatedAt = createdAt ?? updatedAt ?? Now,
            UpdatedAt = updatedAt ?? Now,
        };

        if (lineStatuses is { Count: > 0 })
        {
            var group = new RecipeIngredientGroup
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                SortOrder = 0,
            };

            for (var index = 0; index < lineStatuses.Count; index++)
            {
                var matched = lineStatuses[index] is IngredientMatchStatus.Matched;

                group.Ingredients.Add(new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    RecipeIngredientGroupId = group.Id,
                    SortOrder = index,
                    DisplayText = $"line {index}",
                    // CK_RecipeIngredients_Match_Status: matched means matched to something, and anything else
                    // means matched to nothing.
                    IngredientId = matched ? SqlServerRecipeFixture.FlourId : null,
                    MatchStatus = lineStatuses[index],
                });
            }

            recipe.IngredientGroups.Add(group);
        }

        foreach (var tagId in tagIds ?? [])
        {
            recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = tagId });
        }

        db.Recipes.Add(recipe);

        // Deliberately no snapshot on any of these. A search must never read the archive, and a version without
        // one is the cheapest way for that to be visible if it ever starts.
        Guid? parentId = null;
        for (var index = 0; index < (versionReadiness?.Count ?? 0); index++)
        {
            var version = new RecipeVersion
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                VersionNumber = index + 1,
                Source = RecipeVersionSource.CreatorEdit,
                Readiness = versionReadiness![index],
                CreatedByMembershipId = author,
                CreatedAt = recipe.CreatedAt.AddMinutes(index),
                ParentVersionId = parentId,
                SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            };

            parentId = version.Id;
            db.RecipeVersions.Add(version);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe.Id;
    }
}
