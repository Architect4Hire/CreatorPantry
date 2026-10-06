using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The library search (DAM-002) against a real SQL Server: the filters, how they combine, the keyset paging,
/// and the two workspaces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>SQL Server rather than SQLite, deliberately</strong>, for the reason <c>SqlServerRecipeFixture</c>
/// gives at length: the questions here are whether the semi-joins, the correlated version read and the keyset
/// predicate <em>translate</em>, and whether the <c>WHERE</c> agrees with the <c>ORDER BY</c>. A query that
/// compiles in C# and throws, or that silently repeats a row across a page boundary, passes every test above
/// this layer.
/// </para>
/// <para>
/// Each test starts from an empty library, which is what lets them assert totals rather than only that a known
/// id turned up.
/// </para>
/// </remarks>
public sealed class MediaAssetSearchSqlServerTests(SqlServerMediaFixture fixture)
    : IClassFixture<SqlServerMediaFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerMediaFixture.Now;

    /// <summary>
    /// Stands in for the real cursor scope. Any string does, because the repository never reads it — a cursor
    /// is bound to a resource and filters a repository is not told about, so minting one is Business's job.
    /// These tests only need the scope they encode a position under to match the one they decode it with,
    /// which <see cref="PositionFrom"/> guarantees by using this same value.
    /// </summary>
    private const string TestScope = "t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        // Links and children before their principals. Done in SQL rather than through the change tracker,
        // which would have to load every row of all five tables to delete them.
        await SqlServerMediaFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM RecipeAssetLinks;
            DELETE FROM MediaAssetTags;
            DELETE FROM MediaAssetVersions;
            DELETE FROM Recipes;
            DELETE FROM MediaAssets;
            """,
            Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- Filters -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unfiltered_search_returns_every_live_asset_in_the_workspace()
    {
        await AddAsync("Olive oil cake");
        await AddAsync("Weeknight chilli");

        var (rows, hasMore, total) = await PageAsync(new MediaAssetSearchFilters(), limit: 100);

        Assert.Equal(2, rows.Count);
        Assert.False(hasMore);
        Assert.Equal(2, total);
    }

    /// <summary>
    /// The one filter that is not a filter: a soft-deleted asset is gone from the library whatever else was
    /// asked for. Both filtered indexes exclude tombstones in the index itself, so one never enters the seek.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_never_listed_however_well_it_matches()
    {
        await AddAsync(
            "Deleted hero",
            description: "soda bread",
            channelKey: "instagram",
            platformKey: "reels",
            day: DayOfWeek.Wednesday,
            styleKey: "overhead-linen",
            cuisineId: SqlServerMediaFixture.CuisineId,
            courseId: SqlServerMediaFixture.CourseId,
            tagIds: [SqlServerMediaFixture.TagIdA],
            deletedAt: Now);

        Assert.Empty(await SearchAsync(new MediaAssetSearchFilters()));
        Assert.Empty(await SearchAsync(new MediaAssetSearchFilters(Search: "soda")));
        Assert.Empty(await SearchAsync(new MediaAssetSearchFilters(ChannelKey: "instagram")));
        Assert.Empty(await SearchAsync(new MediaAssetSearchFilters(TagIds: [SqlServerMediaFixture.TagIdA])));

        var (_, _, total) = await PageAsync(new MediaAssetSearchFilters(), limit: 100);
        Assert.Equal(0, total);
    }

    /// <summary>
    /// Matched against the title and the description, case-insensitively, because the term is folded on both
    /// sides rather than left to the collation — so the search means the same thing here as on SQLite.
    /// </summary>
    [Fact]
    public async Task Free_text_matches_the_title_or_the_description_whatever_the_case()
    {
        await AddAsync("Soda Bread hero");
        await AddAsync("Untitled 4", description: "A loaf of SODA bread, overhead.");
        await AddAsync("Olive oil cake", description: "On a linen cloth.");

        var titles = await TitlesAsync(new MediaAssetSearchFilters(Search: "soda bread"));

        Assert.Equal(["Soda Bread hero", "Untitled 4"], titles.Order());
    }

    /// <summary>
    /// An asset with no description is matched on its title and not excluded by the null, which a predicate
    /// written without the null check would do on a three-valued-logic engine.
    /// </summary>
    [Fact]
    public async Task Free_text_matches_an_asset_with_no_description()
    {
        await AddAsync("Soda bread hero", description: null);

        Assert.Single(await SearchAsync(new MediaAssetSearchFilters(Search: "soda")));
    }

    [Fact]
    public async Task Free_text_that_matches_nothing_is_an_empty_page_rather_than_an_error()
    {
        await AddAsync("Olive oil cake");

        var (rows, hasMore, total) = await PageAsync(
            new MediaAssetSearchFilters(Search: "beef wellington"), limit: 100);

        Assert.Empty(rows);
        Assert.False(hasMore);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Each_opaque_key_filter_narrows_to_its_own_value()
    {
        await AddAsync("Instagram reel", channelKey: "instagram", platformKey: "reels", styleKey: "overhead");
        await AddAsync("Blog hero", channelKey: "blog", platformKey: "web", styleKey: "closeup");

        Assert.Equal(["Instagram reel"], await TitlesAsync(new MediaAssetSearchFilters(ChannelKey: "instagram")));
        Assert.Equal(["Blog hero"], await TitlesAsync(new MediaAssetSearchFilters(PlatformKey: "web")));
        Assert.Equal(["Instagram reel"], await TitlesAsync(new MediaAssetSearchFilters(StyleKey: "overhead")));
    }

    /// <summary>
    /// An asset that never said which channel it was for is excluded by a channel filter rather than treated
    /// as matching every one.
    /// </summary>
    [Fact]
    public async Task An_unsaid_key_is_not_a_match_for_any_key()
    {
        await AddAsync("Said", channelKey: "instagram");
        await AddAsync("Unsaid", channelKey: null);

        Assert.Equal(["Said"], await TitlesAsync(new MediaAssetSearchFilters(ChannelKey: "instagram")));
    }

    [Fact]
    public async Task A_day_filter_narrows_to_that_day()
    {
        await AddAsync("Wednesday bake", day: DayOfWeek.Wednesday);
        await AddAsync("Sunday roast", day: DayOfWeek.Sunday);
        await AddAsync("No day", day: null);

        Assert.Equal(["Wednesday bake"], await TitlesAsync(new MediaAssetSearchFilters(Day: DayOfWeek.Wednesday)));
    }

    /// <summary>
    /// Sunday is <c>DayOfWeek.Sunday == 0</c>, which a predicate treating the default as "unsaid" would drop.
    /// The filter is nullable precisely so that zero is a day like any other.
    /// </summary>
    [Fact]
    public async Task Sunday_is_a_day_and_not_an_absent_filter()
    {
        await AddAsync("Sunday roast", day: DayOfWeek.Sunday);
        await AddAsync("No day", day: null);

        Assert.Equal(["Sunday roast"], await TitlesAsync(new MediaAssetSearchFilters(Day: DayOfWeek.Sunday)));
    }

    [Fact]
    public async Task A_cuisine_or_course_filter_matches_any_of_the_ids_given()
    {
        await AddAsync("Italian dessert",
            cuisineId: SqlServerMediaFixture.CuisineId, courseId: SqlServerMediaFixture.CourseId);
        await AddAsync("Thai main",
            cuisineId: SqlServerMediaFixture.OtherCuisineId, courseId: SqlServerMediaFixture.OtherCourseId);
        await AddAsync("Unsaid");

        Assert.Equal(
            ["Italian dessert", "Thai main"],
            (await TitlesAsync(new MediaAssetSearchFilters(
                CuisineIds: [SqlServerMediaFixture.CuisineId, SqlServerMediaFixture.OtherCuisineId]))).Order());

        Assert.Equal(
            ["Italian dessert"],
            await TitlesAsync(new MediaAssetSearchFilters(CourseIds: [SqlServerMediaFixture.CourseId])));
    }

    /// <summary>
    /// An empty list is the absence of a filter, not a filter matching nothing — which is what a client that
    /// cleared its tag chips meant.
    /// </summary>
    [Fact]
    public async Task An_empty_list_filter_filters_nothing_out()
    {
        await AddAsync("Untagged");

        Assert.Single(await SearchAsync(new MediaAssetSearchFilters(TagIds: [], CuisineIds: [], CourseIds: [])));
    }

    [Fact]
    public async Task A_tag_filter_includes_an_asset_carrying_any_of_the_tags()
    {
        await AddAsync("Weeknight", tagIds: [SqlServerMediaFixture.TagIdA]);
        await AddAsync("Freezer", tagIds: [SqlServerMediaFixture.SecondTagIdA]);
        await AddAsync("Untagged");

        Assert.Equal(
            ["Freezer", "Weeknight"],
            (await TitlesAsync(new MediaAssetSearchFilters(
                TagIds: [SqlServerMediaFixture.TagIdA, SqlServerMediaFixture.SecondTagIdA]))).Order());
    }

    /// <summary>
    /// The filter is a semi-join, so an asset carrying two of the requested tags appears once. A plain join
    /// would return it twice, which would also corrupt the page size and the cursor that follows it.
    /// </summary>
    [Fact]
    public async Task An_asset_carrying_two_of_the_requested_tags_appears_once()
    {
        await AddAsync(
            "Both tags",
            tagIds: [SqlServerMediaFixture.TagIdA, SqlServerMediaFixture.SecondTagIdA]);

        var (rows, _, total) = await PageAsync(
            new MediaAssetSearchFilters(
                TagIds: [SqlServerMediaFixture.TagIdA, SqlServerMediaFixture.SecondTagIdA]),
            limit: 100);

        Assert.Single(rows);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task A_recipe_filter_includes_only_assets_linked_to_that_recipe()
    {
        var soda = await AddRecipeAsync("Soda bread");
        var cake = await AddRecipeAsync("Olive oil cake");

        await AddAsync("Soda hero", linkedRecipeId: soda);
        await AddAsync("Cake hero", linkedRecipeId: cake);
        await AddAsync("Unlinked");

        Assert.Equal(["Soda hero"], await TitlesAsync(new MediaAssetSearchFilters(RecipeId: soda)));
    }

    /// <summary>
    /// A semi-join here too: an asset linked twice to the same recipe — a hero and a gallery slot — is one
    /// asset in the library.
    /// </summary>
    [Fact]
    public async Task An_asset_linked_twice_to_one_recipe_appears_once()
    {
        var recipe = await AddRecipeAsync("Soda bread");
        var asset = await AddAsync("Soda hero", linkedRecipeId: recipe);

        await using (var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA))
        {
            var db = SqlServerMediaFixture.Db(scope);
            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe,
                MediaAssetId = asset,
                Role = RecipeAssetRole.Hero,
                SortOrder = 1,
            });
            await db.SaveChangesAsync(Ct);
        }

        var (rows, _, total) = await PageAsync(new MediaAssetSearchFilters(RecipeId: recipe), limit: 100);

        Assert.Single(rows);
        Assert.Equal(1, total);
    }

    /// <summary>
    /// The bounds are the asset's own created date, half-open: the lower inclusive and the upper exclusive, so
    /// adjacent ranges neither overlap nor leave a gap. Both boundary rows are asserted by name, because an
    /// off-by-one here is a row belonging to two months or to neither.
    /// </summary>
    [Fact]
    public async Task The_date_bounds_are_half_open_on_the_assets_created_date()
    {
        var first = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var next = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await AddAsync("March", createdAt: first.AddTicks(-1));
        await AddAsync("First moment of April", createdAt: first);
        await AddAsync("Mid April", createdAt: first.AddDays(14));
        await AddAsync("Last tick of April", createdAt: next.AddTicks(-1));
        await AddAsync("First moment of May", createdAt: next);

        var april = await TitlesAsync(new MediaAssetSearchFilters(
            CreatedOnOrAfter: first, CreatedBefore: next));

        Assert.Equal(["First moment of April", "Last tick of April", "Mid April"], april.Order());

        // The adjacent range picks up exactly the row April's upper bound excluded, and nothing else.
        Assert.Equal(
            ["First moment of May"],
            await TitlesAsync(new MediaAssetSearchFilters(CreatedOnOrAfter: next)));
    }

    // --- Combined filters --------------------------------------------------------------------------------

    /// <summary>
    /// Every filter at once, over a library where each is satisfied by a different set, so only the
    /// intersection can come back. What matters is not that the match is found but that the eleven near-misses
    /// are all excluded — one filter silently dropped would still return the match.
    /// </summary>
    [Fact]
    public async Task Every_filter_at_once_leaves_only_the_asset_that_satisfies_all_of_them()
    {
        var recipe = await AddRecipeAsync("Soda bread");
        var otherRecipe = await AddRecipeAsync("Olive oil cake");
        var aprilStart = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var mayStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await Candidate("The match", recipe, _ => { });
        await Candidate("Wrong text", recipe, f => f.Description = "olive oil cake on marble");
        await Candidate("Wrong channel", recipe, f => f.ChannelKey = "blog");
        await Candidate("Wrong platform", recipe, f => f.PlatformKey = "web");
        await Candidate("Wrong day", recipe, f => f.Day = DayOfWeek.Sunday);
        await Candidate("Wrong style", recipe, f => f.StyleKey = "closeup");
        await Candidate("Wrong cuisine", recipe, f => f.CuisineId = SqlServerMediaFixture.OtherCuisineId);
        await Candidate("Wrong course", recipe, f => f.CourseId = SqlServerMediaFixture.OtherCourseId);
        await Candidate("Wrong tag", recipe, f => f.TagId = SqlServerMediaFixture.SecondTagIdA);
        await Candidate("Wrong recipe", otherRecipe, _ => { });
        await Candidate("Too early", recipe, f => f.CreatedAt = aprilStart.AddTicks(-1));
        await Candidate("Too late", recipe, f => f.CreatedAt = mayStart);

        // And the match again as a tombstone, so the one clause that has to survive every other filter is
        // exercised in the combined case rather than only on its own.
        await Candidate("Tombstoned match", recipe, f => f.DeletedAt = Now);

        var everything = new MediaAssetSearchFilters(
            Search: "soda bread",
            ChannelKey: "instagram",
            PlatformKey: "reels",
            Day: DayOfWeek.Wednesday,
            StyleKey: "overhead-linen",
            CuisineIds: [SqlServerMediaFixture.CuisineId],
            CourseIds: [SqlServerMediaFixture.CourseId],
            TagIds: [SqlServerMediaFixture.TagIdA],
            RecipeId: recipe,
            CreatedOnOrAfter: aprilStart,
            CreatedBefore: mayStart);

        var (rows, hasMore, total) = await PageAsync(everything, limit: 100);

        Assert.Equal("The match", Assert.Single(rows).Title);
        Assert.False(hasMore);
        Assert.Equal(1, total);
    }

    /// <summary>
    /// Two filters that each match something, intersecting in nothing. An empty page and a zero total, not an
    /// error: a creator narrowing a library down to nothing has asked a legitimate question.
    /// </summary>
    [Fact]
    public async Task Filters_that_match_different_assets_intersect_in_an_empty_page()
    {
        await AddAsync("Instagram", channelKey: "instagram", styleKey: "overhead");
        await AddAsync("Blog", channelKey: "blog", styleKey: "closeup");

        var (rows, hasMore, total) = await PageAsync(
            new MediaAssetSearchFilters(ChannelKey: "instagram", StyleKey: "closeup"), limit: 100);

        Assert.Empty(rows);
        Assert.False(hasMore);
        Assert.Equal(0, total);
    }

    // --- Ordering and paging -----------------------------------------------------------------------------

    [Fact]
    public async Task The_default_ordering_is_newest_first()
    {
        await AddAsync("Oldest", createdAt: Now.AddDays(-2));
        await AddAsync("Newest", createdAt: Now);
        await AddAsync("Middle", createdAt: Now.AddDays(-1));

        Assert.Equal(
            ["Newest", "Middle", "Oldest"],
            await TitlesAsync(new MediaAssetSearchFilters()));
    }

    [Fact]
    public async Task The_title_ordering_is_alphabetical()
    {
        await AddAsync("Zucchini fritters", createdAt: Now);
        await AddAsync("Apple galette", createdAt: Now.AddDays(-2));
        await AddAsync("Mushroom ragu", createdAt: Now.AddDays(-1));

        Assert.Equal(
            ["Apple galette", "Mushroom ragu", "Zucchini fritters"],
            await TitlesAsync(new MediaAssetSearchFilters(), MediaAssetSearchSort.Title));
    }

    /// <summary>
    /// Thirteen assets read three at a time: every row exactly once, in the same order the unpaged read gives,
    /// and no fourteenth page. This is the test a keyset predicate that disagrees with its <c>ORDER BY</c>
    /// fails.
    /// </summary>
    [Theory]
    [InlineData(MediaAssetSearchSort.RecentlyAdded)]
    [InlineData(MediaAssetSearchSort.Title)]
    public async Task Paging_visits_every_asset_exactly_once(MediaAssetSearchSort sort)
    {
        for (var index = 0; index < 13; index++)
        {
            await AddAsync($"Asset {index:D2}", createdAt: Now.AddMinutes(-index));
        }

        var drained = await DrainAsync(new MediaAssetSearchFilters(), limit: 3, sort);
        var unpaged = await SearchAsync(new MediaAssetSearchFilters(), sort);

        Assert.Equal(13, drained.Count);
        Assert.Equal(unpaged.Select(row => row.Id), drained.Select(row => row.Id));
        Assert.Equal(13, drained.Select(row => row.Id).Distinct().Count());
    }

    /// <summary>
    /// Every asset created in the same tick, so the only thing separating them is the id in the tie-break.
    /// Without it in both the <c>WHERE</c> and the <c>ORDER BY</c>, a page boundary landing inside the tie
    /// either repeats rows or skips them — and this is the case that is impossible to hit by accident in
    /// production and trivial to hit on a seeded import.
    /// </summary>
    [Fact]
    public async Task Paging_through_assets_sharing_one_timestamp_neither_repeats_nor_skips()
    {
        for (var index = 0; index < 9; index++)
        {
            await AddAsync($"Tied {index}", createdAt: Now);
        }

        var drained = await DrainAsync(new MediaAssetSearchFilters(), limit: 2);

        Assert.Equal(9, drained.Count);
        Assert.Equal(9, drained.Select(row => row.Id).Distinct().Count());
    }

    /// <summary>Titles are not unique — two of a creator's photographs may share one — so the same holds there.</summary>
    [Fact]
    public async Task Paging_through_assets_sharing_one_title_neither_repeats_nor_skips()
    {
        for (var index = 0; index < 9; index++)
        {
            await AddAsync("Untitled", createdAt: Now.AddMinutes(-index));
        }

        var drained = await DrainAsync(new MediaAssetSearchFilters(), limit: 2, MediaAssetSearchSort.Title);

        Assert.Equal(9, drained.Count);
        Assert.Equal(9, drained.Select(row => row.Id).Distinct().Count());
    }

    /// <summary>
    /// The filters are carried through every page, so a cursor does not widen the set as it goes.
    /// </summary>
    [Fact]
    public async Task Paging_a_filtered_search_stays_filtered()
    {
        for (var index = 0; index < 7; index++)
        {
            await AddAsync($"Instagram {index}", channelKey: "instagram", createdAt: Now.AddMinutes(-index));
            await AddAsync($"Blog {index}", channelKey: "blog", createdAt: Now.AddMinutes(-index));
        }

        var drained = await DrainAsync(new MediaAssetSearchFilters(ChannelKey: "instagram"), limit: 2);

        Assert.Equal(7, drained.Count);
        Assert.All(drained, row => Assert.Equal("instagram", row.ChannelKey));
    }

    /// <summary>
    /// <c>hasMore</c> is answered by reading one row past the page rather than by comparing a count to a page
    /// size, so an exactly-full last page does not claim another page that does not exist.
    /// </summary>
    [Fact]
    public async Task An_exactly_full_page_does_not_claim_another()
    {
        await AddAsync("One", createdAt: Now);
        await AddAsync("Two", createdAt: Now.AddMinutes(-1));

        var (rows, hasMore, _) = await PageAsync(new MediaAssetSearchFilters(), limit: 2);

        Assert.Equal(2, rows.Count);
        Assert.False(hasMore);
    }

    /// <summary>
    /// The total counts the whole filtered set rather than the page, and is counted before the position is
    /// applied — otherwise it would shrink as a client paged through, which is not what "of how many" means.
    /// </summary>
    [Fact]
    public async Task The_total_counts_the_filtered_set_rather_than_the_page()
    {
        for (var index = 0; index < 5; index++)
        {
            await AddAsync($"Asset {index}", createdAt: Now.AddMinutes(-index));
        }

        var (first, hasMore, firstTotal) = await PageAsync(new MediaAssetSearchFilters(), limit: 2);

        Assert.Equal(2, first.Count);
        Assert.True(hasMore);
        Assert.Equal(5, firstTotal);

        var (_, _, secondTotal) = await PageAsync(
            new MediaAssetSearchFilters(), limit: 2,
            position: PositionFrom(first[^1], MediaAssetSearchSort.RecentlyAdded));

        Assert.Equal(5, secondTotal);
    }

    [Fact]
    public async Task A_caller_that_did_not_ask_for_a_total_is_not_given_one()
    {
        await AddAsync("One");

        var (rows, _, total) = await PageAsync(new MediaAssetSearchFilters(), limit: 2, includeTotal: false);

        Assert.Single(rows);
        Assert.Null(total);
    }

    // --- The projected row -------------------------------------------------------------------------------

    /// <summary>
    /// The current version's media facts come with the row, read through a correlated subquery on
    /// <c>(MediaAssetId, VersionNumber)</c>. A grid needs dimensions to lay out, and the alternative is a
    /// client fetching every asset to find out.
    /// </summary>
    [Fact]
    public async Task A_row_carries_the_current_versions_media_facts()
    {
        await AddAsync("Soda bread hero", description: "Overhead.", channelKey: "instagram");

        var row = Assert.Single(await SearchAsync(new MediaAssetSearchFilters()));

        Assert.Equal("image/jpeg", row.MediaType);
        Assert.Equal(1600, row.Width);
        Assert.Equal(1200, row.Height);
        Assert.Equal(204_800L, row.SizeBytes);
        Assert.Equal(1, row.CurrentVersionNumber);
    }

    /// <summary>
    /// The correlated read names the <em>current</em> version, so a later version does not change what an
    /// asset still pointing at an earlier one reports. A subquery ordered by version number rather than
    /// matched against <c>CurrentVersionNumber</c> would answer with the newest and be wrong here.
    /// </summary>
    [Fact]
    public async Task A_row_reports_the_current_version_rather_than_the_newest_one()
    {
        var asset = await AddAsync("Soda bread hero");

        await using (var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA))
        {
            var db = SqlServerMediaFixture.Db(scope);
            db.MediaAssetVersions.Add(new MediaAssetVersion
            {
                Id = Guid.NewGuid(),
                MediaAssetId = asset,
                VersionNumber = 2,
                MediaType = "image/png",
                SizeBytes = 999_999,
                Width = 400,
                Height = 300,
                ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                ObjectKey = $"assets/{asset:D}/2.png",
                Source = MediaAssetVersionSource.Upload,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Now.AddMinutes(1),
            });
            await db.SaveChangesAsync(Ct);
        }

        var row = Assert.Single(await SearchAsync(new MediaAssetSearchFilters()));

        Assert.Equal("image/jpeg", row.MediaType);
        Assert.Equal(1600, row.Width);
    }

    /// <summary>
    /// An asset whose current version cannot be read still lists, with its own metadata and no media facts.
    /// Disappearing from the library would be the less useful failure — a creator cannot repair an asset they
    /// cannot see.
    /// </summary>
    [Fact]
    public async Task An_asset_with_no_readable_version_still_lists()
    {
        await AddAsync("Version-less", withVersion: false);

        var row = Assert.Single(await SearchAsync(new MediaAssetSearchFilters()));

        Assert.Equal("Version-less", row.Title);
        Assert.Null(row.MediaType);
        Assert.Equal(0, row.Width);
    }

    /// <summary>
    /// The row carries the ordering it was fetched under, so the cursor half it publishes is the one the next
    /// page will parse. Under the alphabetical ordering that is the title, and under the default it is the
    /// round-trip timestamp.
    /// </summary>
    [Fact]
    public async Task A_row_publishes_the_cursor_half_of_the_ordering_it_was_fetched_under()
    {
        await AddAsync("Soda bread hero", createdAt: Now);

        var newest = Assert.Single(await SearchAsync(new MediaAssetSearchFilters()));
        var alphabetical = Assert.Single(
            await SearchAsync(new MediaAssetSearchFilters(), MediaAssetSearchSort.Title));

        Assert.Equal(newest.CreatedAt.ToString("O"), newest.SortValue);
        Assert.Equal("Soda bread hero", alphabetical.SortValue);
        Assert.Equal(newest.Id.ToString("D"), newest.TieBreaker);
    }

    // --- Isolation ---------------------------------------------------------------------------------------

    /// <summary>
    /// Workspace A and Workspace B, each holding assets of the same shape — the two-workspace coverage
    /// tenancy.md requires of every workspace-scoped feature. The totals matter as much as the rows: a count
    /// that escaped the query filter would leak how much work another creator has done even while returning
    /// none of it.
    /// </summary>
    [Fact]
    public async Task A_search_lists_and_counts_only_its_own_workspace()
    {
        await AddAsync("A one");
        await AddAsync("B one", workspaceId: SqlServerMediaFixture.WorkspaceB);
        await AddAsync("B two", workspaceId: SqlServerMediaFixture.WorkspaceB);

        var (inA, _, totalA) = await PageAsync(new MediaAssetSearchFilters(), limit: 100);
        var (inB, _, totalB) = await PageAsync(
            new MediaAssetSearchFilters(), limit: 100, workspaceId: SqlServerMediaFixture.WorkspaceB);

        Assert.Equal(["A one"], inA.Select(row => row.Title));
        Assert.Equal(["B one", "B two"], inB.Select(row => row.Title).Order());
        Assert.Equal(1, totalA);
        Assert.Equal(2, totalB);
        Assert.Empty(inA.Select(row => row.Id).Intersect(inB.Select(row => row.Id)));
    }

    /// <summary>
    /// Both workspaces have a tag named "Weeknight". Filtering by A's tag id from inside B must find nothing —
    /// not B's identically named tag, and certainly not A's asset.
    /// </summary>
    [Fact]
    public async Task A_tag_filter_cannot_reach_the_other_workspaces_identically_named_tag()
    {
        await AddAsync("Tagged in A", tagIds: [SqlServerMediaFixture.TagIdA]);
        await AddAsync(
            "Tagged in B",
            workspaceId: SqlServerMediaFixture.WorkspaceB,
            tagIds: [SqlServerMediaFixture.TagIdB]);

        Assert.Empty(await SearchAsync(
            new MediaAssetSearchFilters(TagIds: [SqlServerMediaFixture.TagIdA]),
            workspaceId: SqlServerMediaFixture.WorkspaceB));

        Assert.Equal(
            ["Tagged in B"],
            await TitlesAsync(
                new MediaAssetSearchFilters(TagIds: [SqlServerMediaFixture.TagIdB]),
                workspaceId: SqlServerMediaFixture.WorkspaceB));
    }

    /// <summary>
    /// A free-text term that matches an asset in the other workspace finds nothing, which is the search path
    /// specifically: a predicate added without the query filter in force would match across the boundary.
    /// </summary>
    [Fact]
    public async Task Free_text_cannot_reach_the_other_workspaces_assets()
    {
        await AddAsync("Soda bread hero", workspaceId: SqlServerMediaFixture.WorkspaceB);

        Assert.Empty(await SearchAsync(new MediaAssetSearchFilters(Search: "soda")));
    }

    /// <summary>
    /// A recipe id from the other workspace finds nothing rather than that workspace's linked assets. The
    /// filter is an id a client supplies, so the semi-join has to be inside the boundary as well.
    /// </summary>
    [Fact]
    public async Task A_recipe_filter_cannot_reach_the_other_workspaces_linked_assets()
    {
        var recipeInB = await AddRecipeAsync("Soda bread", SqlServerMediaFixture.WorkspaceB);

        await AddAsync("Linked in B", workspaceId: SqlServerMediaFixture.WorkspaceB, linkedRecipeId: recipeInB);
        await AddAsync("A's own");

        Assert.Empty(await SearchAsync(new MediaAssetSearchFilters(RecipeId: recipeInB)));
    }

    /// <summary>
    /// A position minted while reading one workspace, replayed against the other, pages that other workspace's
    /// rows and never reaches across. The scope fingerprint is what turns this into a refused cursor rather
    /// than a wrong page, and binding the workspace into that scope is the read seam's job — this is the
    /// evidence that the repository does not leak even when the check above it has been bypassed entirely.
    /// </summary>
    [Fact]
    public async Task A_position_from_one_workspace_pages_only_the_other_workspaces_rows()
    {
        // A's assets are strictly newer than B's, so a position taken from A's newest row is older than
        // nothing in B and the expected page is every row B has. Seeding both workspaces at the same instant
        // would make the answer depend on how the engine orders two GUIDs, which is not what this is about.
        await AddAsync("A one", createdAt: Now);
        await AddAsync("A two", createdAt: Now.AddMinutes(-1));
        await AddAsync("B one", workspaceId: SqlServerMediaFixture.WorkspaceB, createdAt: Now.AddMinutes(-10));
        await AddAsync("B two", workspaceId: SqlServerMediaFixture.WorkspaceB, createdAt: Now.AddMinutes(-11));

        var (firstInA, _, _) = await PageAsync(new MediaAssetSearchFilters(), limit: 1);
        var position = PositionFrom(firstInA[0], MediaAssetSearchSort.RecentlyAdded);

        var (rows, _, _) = await PageAsync(
            new MediaAssetSearchFilters(), limit: 100,
            position: position, workspaceId: SqlServerMediaFixture.WorkspaceB);

        // Both of B's rows, and the count is asserted as well as the prefix: Assert.All passes on an empty
        // page, so a query that returned nothing at all would have satisfied the prefix check vacuously.
        Assert.Equal(["B one", "B two"], rows.Select(row => row.Title).Order());
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private static IMediaAssetSearchRepository Search(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetSearchRepository>();

    private async Task<IReadOnlyList<MediaAssetSearchRecord>> SearchAsync(
        MediaAssetSearchFilters filters,
        MediaAssetSearchSort sort = MediaAssetSearchSort.RecentlyAdded,
        Guid? workspaceId = null)
    {
        var (rows, _, _) = await PageAsync(filters, limit: 100, sort, position: null, workspaceId);

        return rows;
    }

    private async Task<(IReadOnlyList<MediaAssetSearchRecord> Rows, bool HasMore, int? Total)> PageAsync(
        MediaAssetSearchFilters filters,
        int limit,
        MediaAssetSearchSort sort = MediaAssetSearchSort.RecentlyAdded,
        MediaAssetSearchPosition? position = null,
        Guid? workspaceId = null,
        bool includeTotal = true)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);

        return await Search(scope).SearchAsync(
            new MediaAssetSearchCriteria(filters, TestScope, sort, position, limit, includeTotal),
            Ct);
    }

    /// <summary>Every title the search returns, in the order it returned them.</summary>
    private async Task<string[]> TitlesAsync(
        MediaAssetSearchFilters filters,
        MediaAssetSearchSort sort = MediaAssetSearchSort.RecentlyAdded,
        Guid? workspaceId = null) =>
        [.. (await SearchAsync(filters, sort, workspaceId)).Select(row => row.Title)];

    /// <summary>
    /// Follows every page to the end, guarding against a query that never terminates.
    /// </summary>
    /// <remarks>
    /// The position is rebuilt by encoding the last row into a real cursor and decoding it back, which is the
    /// round trip the read seam actually performs. Building the position straight from the row would skip the
    /// wire format, and a sort value that could not survive it — a truncated timestamp, say — would then only
    /// fail in production.
    /// </remarks>
    private async Task<List<MediaAssetSearchRecord>> DrainAsync(
        MediaAssetSearchFilters filters,
        int limit,
        MediaAssetSearchSort sort = MediaAssetSearchSort.RecentlyAdded,
        Guid? workspaceId = null)
    {
        var all = new List<MediaAssetSearchRecord>();
        MediaAssetSearchPosition? position = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var (rows, hasMore, _) = await PageAsync(filters, limit, sort, position, workspaceId);
            all.AddRange(rows);

            if (!hasMore)
            {
                return all;
            }

            Assert.NotEmpty(rows);
            Assert.Equal(limit, rows.Count);
            position = PositionFrom(rows[^1], sort);
        }

        Assert.Fail("paging did not terminate");

        return all;
    }

    private static MediaAssetSearchPosition PositionFrom(
        MediaAssetSearchRecord row, MediaAssetSearchSort sort)
    {
        var encoded = ReferenceCursor.Encode(row.SortValue, row.TieBreaker, TestScope);

        Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
        Assert.True(MediaAssetSearchPosition.TryCreate(sort, cursor!, out var position));

        return position!;
    }

    /// <summary>
    /// Seeds one asset, and only the parts a test asked for. <c>WorkspaceId</c> is never set on anything — the
    /// ownership interceptor stamps it from the resolved context, and feature code assigning it is a defect.
    /// </summary>
    /// <param name="withVersion">
    /// Whether to write the current version. Defaults to true, because a real asset always has one; the tests
    /// about a library that lists an asset with no readable version pass false.
    /// </param>
    private async Task<Guid> AddAsync(
        string title,
        Guid? workspaceId = null,
        string? description = null,
        MediaAssetKind kind = MediaAssetKind.Original,
        string? channelKey = null,
        string? platformKey = null,
        DayOfWeek? day = null,
        string? styleKey = null,
        Guid? cuisineId = null,
        Guid? courseId = null,
        IReadOnlyList<Guid>? tagIds = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deletedAt = null,
        Guid? linkedRecipeId = null,
        bool withVersion = true)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);
        var actor = Guid.NewGuid();
        var at = createdAt ?? Now;

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Kind = kind,
            ChannelKey = channelKey,
            PlatformKey = platformKey,
            Day = day,
            StyleKey = styleKey,
            CuisineId = cuisineId,
            CourseId = courseId,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = at,
            UpdatedAt = at,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        if (withVersion)
        {
            asset.Versions.Add(new MediaAssetVersion
            {
                Id = Guid.NewGuid(),
                MediaAssetId = asset.Id,
                VersionNumber = 1,
                MediaType = "image/jpeg",
                SizeBytes = 204_800,
                Width = 1600,
                Height = 1200,
                ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                ObjectKey = $"assets/{asset.Id:D}/1.jpg",
                Source = MediaAssetVersionSource.Upload,
                CreatedByMembershipId = actor,
                CreatedAt = at,
            });
        }

        foreach (var tagId in tagIds ?? [])
        {
            asset.Tags.Add(new MediaAssetTag { MediaAssetId = asset.Id, WorkspaceTagId = tagId });
        }

        db.MediaAssets.Add(asset);

        if (linkedRecipeId is { } recipeId)
        {
            // A gallery slot at the next free position, never the hero one. Two unique indexes make that
            // necessary rather than tidy: one hero link per recipe, and one link per (recipe, sort order).
            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                MediaAssetId = asset.Id,
                Role = RecipeAssetRole.Gallery,
                SortOrder = await db.RecipeAssetLinks.CountAsync(link => link.RecipeId == recipeId, Ct),
            });
        }

        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    /// <summary>
    /// Seeds one asset that satisfies every filter of the combined-filter case, with one fact changed. Each
    /// case then names only what it changed, so a near-miss cannot accidentally differ in two ways.
    /// </summary>
    private async Task Candidate(string title, Guid recipeId, Action<CandidateFields> change)
    {
        var fields = new CandidateFields
        {
            Description = "soda bread on linen",
            ChannelKey = "instagram",
            PlatformKey = "reels",
            Day = DayOfWeek.Wednesday,
            StyleKey = "overhead-linen",
            CuisineId = SqlServerMediaFixture.CuisineId,
            CourseId = SqlServerMediaFixture.CourseId,
            TagId = SqlServerMediaFixture.TagIdA,
            CreatedAt = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero),
        };

        change(fields);

        await AddAsync(
            title,
            description: fields.Description,
            channelKey: fields.ChannelKey,
            platformKey: fields.PlatformKey,
            day: fields.Day,
            styleKey: fields.StyleKey,
            cuisineId: fields.CuisineId,
            courseId: fields.CourseId,
            tagIds: fields.TagId is { } tag ? [tag] : null,
            createdAt: fields.CreatedAt,
            deletedAt: fields.DeletedAt,
            linkedRecipeId: recipeId);
    }

    /// <summary>Mutable carrier for a combined-filter case, so each one names only what it changes.</summary>
    private sealed class CandidateFields
    {
        public string? Description { get; set; }

        public string? ChannelKey { get; set; }

        public string? PlatformKey { get; set; }

        public DayOfWeek? Day { get; set; }

        public string? StyleKey { get; set; }

        public Guid? CuisineId { get; set; }

        public Guid? CourseId { get; set; }

        public Guid? TagId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }
    }

    /// <summary>A recipe for the link filter to name. Nothing reads anything else about it.</summary>
    private async Task<Guid> AddRecipeAsync(string title, Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);
        var actor = Guid.NewGuid();

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(Ct);

        return recipe.Id;
    }
}
