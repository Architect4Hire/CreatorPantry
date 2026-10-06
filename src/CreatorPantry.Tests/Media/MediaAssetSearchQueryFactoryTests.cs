using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The library search's query string, before any database is involved: what parses, what is refused by name,
/// and what a cursor is bound to.
/// </summary>
/// <remarks>
/// These are the tests that keep the published contract honest. A filter that silently fails to parse is a
/// search that quietly returns the wrong set, and a cursor whose scope does not cover a filter is a page that
/// resumes in a different set than it started in — neither of which a repository test can see, because by
/// then the mistake has already been made.
/// </remarks>
public sealed class MediaAssetSearchQueryFactoryTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid OtherWorkspace = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // --- Parsing -----------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_query_searches_the_whole_library_newest_first()
    {
        var criteria = Created(new MediaAssetSearchViewModel());

        Assert.Equal(MediaAssetSearchSort.RecentlyAdded, criteria.Sort);
        Assert.Null(criteria.Position);
        Assert.Equal(ReferencePolicy.DefaultPageSize, criteria.Limit);
        Assert.True(criteria.IncludeTotal);
        Assert.Equal(new MediaAssetSearchFilters(), criteria.Filters);
    }

    [Fact]
    public void Every_filter_parses()
    {
        var cuisine = Guid.NewGuid();
        var course = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var recipe = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var criteria = Created(new MediaAssetSearchViewModel(
            Search: "Soda Bread",
            Channel: "instagram",
            Platform: "reels",
            Day: "wednesday",
            Style: "overhead-linen",
            Cuisine: $"{cuisine:D}",
            Course: $"{course:D}",
            Tag: $"{tag:D}",
            RecipeId: recipe,
            CreatedFrom: from,
            CreatedBefore: before,
            Sort: "title"));

        Assert.Equal(MediaAssetSearchSort.Title, criteria.Sort);
        Assert.Equal("instagram", criteria.Filters.ChannelKey);
        Assert.Equal("reels", criteria.Filters.PlatformKey);
        Assert.Equal(DayOfWeek.Wednesday, criteria.Filters.Day);
        Assert.Equal("overhead-linen", criteria.Filters.StyleKey);
        Assert.Equal([cuisine], criteria.Filters.CuisineIds);
        Assert.Equal([course], criteria.Filters.CourseIds);
        Assert.Equal([tag], criteria.Filters.TagIds);
        Assert.Equal(recipe, criteria.Filters.RecipeId);
        Assert.Equal(from, criteria.Filters.CreatedOnOrAfter);
        Assert.Equal(before, criteria.Filters.CreatedBefore);
    }

    /// <summary>
    /// The term is folded to lower case here rather than in the predicate, so that two searches differing only
    /// in case are one scope. Were it folded later, their cursors would bind to different scopes while
    /// returning identical rows, and following one would be refused for no reason the client could see.
    /// </summary>
    [Fact]
    public void A_search_term_is_trimmed_and_lowered_so_case_does_not_change_the_scope()
    {
        var upper = Created(new MediaAssetSearchViewModel(Search: "  Soda Bread "));
        var lower = Created(new MediaAssetSearchViewModel(Search: "soda bread"));

        Assert.Equal("soda bread", upper.Filters.Search);
        Assert.Equal(lower.Scope, upper.Scope);
    }

    [Fact]
    public void Opaque_keys_are_trimmed_and_an_empty_one_is_no_filter_at_all()
    {
        var criteria = Created(new MediaAssetSearchViewModel(
            Channel: "  instagram  ", Platform: "   ", Style: ""));

        Assert.Equal("instagram", criteria.Filters.ChannelKey);
        Assert.Null(criteria.Filters.PlatformKey);
        Assert.Null(criteria.Filters.StyleKey);
    }

    [Fact]
    public void A_bad_id_is_refused_by_the_field_that_carried_it()
    {
        var error = Refused(new MediaAssetSearchViewModel(Tag: "not-an-id"));

        Assert.Equal(MediaErrorCodes.AssetSearchInvalidRequest, error.Code);
        Assert.Equal(["'not-an-id' is not an id."], error.FieldErrors!["tag"]);
    }

    /// <summary>
    /// A query naming three bad filters hears about all three, rather than being corrected one round trip at a
    /// time.
    /// </summary>
    [Fact]
    public void Several_bad_filters_are_all_named()
    {
        var error = Refused(new MediaAssetSearchViewModel(
            Cuisine: "nope", Day: "Thorsday", Sort: "oldest"));

        Assert.Equal(["cuisine", "day", "sort"], error.FieldErrors!.Keys.Order());
    }

    /// <summary>
    /// The numeric form of an enum is refused outright. A sort of "1" would otherwise succeed against the
    /// member's current number, which is an implementation detail and never part of the published contract.
    /// </summary>
    [Fact]
    public void A_numeric_sort_is_not_a_sort()
    {
        var error = Refused(new MediaAssetSearchViewModel(Sort: "1"));

        Assert.Equal(["sort"], error.FieldErrors!.Keys);
    }

    // --- Clamping ----------------------------------------------------------------------------------------

    /// <summary>
    /// Asking for too much is answered, not refused — a client cannot fail a read by being greedy.
    /// </summary>
    [Theory]
    [InlineData(10_000, ReferencePolicy.MaxPageSize)]
    [InlineData(0, ReferencePolicy.MinPageSize)]
    [InlineData(-5, ReferencePolicy.MinPageSize)]
    [InlineData(10, 10)]
    public void A_page_size_is_clamped_rather_than_refused(int asked, int expected)
    {
        Assert.Equal(expected, Created(new MediaAssetSearchViewModel(Limit: asked)).Limit);
    }

    /// <summary>
    /// The clamp is computed on read, so it cannot be got round by copying the record — which a stored field
    /// would allow, because a record's copy constructor duplicates fields without re-running initializers.
    /// </summary>
    [Fact]
    public void A_copied_criteria_cannot_carry_an_unclamped_limit()
    {
        var criteria = Created(new MediaAssetSearchViewModel(Limit: 10));

        Assert.Equal(ReferencePolicy.MaxPageSize, (criteria with { RequestedLimit = 10_000 }).Limit);
    }

    // --- Cursor scope ------------------------------------------------------------------------------------

    [Fact]
    public void A_cursor_round_trips_into_the_position_it_encoded()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.Zero);
        var criteria = Created(new MediaAssetSearchViewModel(Cursor: CursorFor(createdAt, id)));

        var position = Assert.IsType<MediaAssetSearchPosition>(criteria.Position);
        Assert.Equal(id, position.MediaAssetId);
        Assert.Equal(createdAt, position.CreatedAt);
    }

    /// <summary>
    /// The page size and the total are <em>not</em> in the scope: changing how many rows come after a position
    /// is legitimate, and asking for a count does not change which rows follow.
    /// </summary>
    [Fact]
    public void Changing_the_page_size_or_the_total_keeps_the_cursor()
    {
        var cursor = CursorFor(DateTimeOffset.UnixEpoch, Guid.NewGuid());

        var criteria = Created(new MediaAssetSearchViewModel(
            Cursor: cursor, Limit: 7, IncludeTotal: false));

        Assert.NotNull(criteria.Position);
        Assert.Equal(7, criteria.Limit);
        Assert.False(criteria.IncludeTotal);
    }

    /// <summary>
    /// Every filter is in the scope, so none of them can be changed under a cursor. Each case here is a page
    /// that would otherwise resume at a position taken from a different set — the same row count, the wrong
    /// rows.
    /// </summary>
    [Theory]
    [InlineData("channel")]
    [InlineData("platform")]
    [InlineData("day")]
    [InlineData("style")]
    [InlineData("cuisine")]
    [InlineData("course")]
    [InlineData("tag")]
    [InlineData("recipe")]
    [InlineData("createdFrom")]
    [InlineData("createdBefore")]
    [InlineData("search")]
    [InlineData("sort")]
    public void Changing_any_filter_refuses_the_cursor(string changed)
    {
        var model = new MediaAssetSearchViewModel(
            Cursor: CursorFor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

        var moved = changed switch
        {
            "channel" => model with { Channel = "instagram" },
            "platform" => model with { Platform = "reels" },
            "day" => model with { Day = "Friday" },
            "style" => model with { Style = "overhead" },
            "cuisine" => model with { Cuisine = $"{Guid.NewGuid():D}" },
            "course" => model with { Course = $"{Guid.NewGuid():D}" },
            "tag" => model with { Tag = $"{Guid.NewGuid():D}" },
            "recipe" => model with { RecipeId = Guid.NewGuid() },
            "createdFrom" => model with { CreatedFrom = DateTimeOffset.UnixEpoch },
            "createdBefore" => model with { CreatedBefore = DateTimeOffset.UnixEpoch },
            "search" => model with { Search = "bread" },
            _ => model with { Sort = "Title" },
        };

        Assert.Equal(MediaErrorCodes.AssetCursorInvalidRequest, Refused(moved).Code);
    }

    /// <summary>
    /// A cursor minted while reading one workspace is refused in another, which is the first of the two
    /// defences: the scope carries the workspace, and the repository's query filter carries it again.
    /// </summary>
    [Fact]
    public void A_cursor_from_another_workspace_is_refused()
    {
        var model = new MediaAssetSearchViewModel(Cursor: CursorFor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

        Assert.True(MediaAssetSearchQueryFactory.TryCreate(model, Workspace, out _, out _));
        Assert.False(MediaAssetSearchQueryFactory.TryCreate(model, OtherWorkspace, out _, out var error));
        Assert.Equal(MediaErrorCodes.AssetCursorInvalidRequest, error!.Code);
    }

    /// <summary>
    /// A list filter's order is not part of the set it describes, so reordering it must not cost a client its
    /// cursor — which it would, if the ids were folded in as written.
    /// </summary>
    [Fact]
    public void Reordering_a_list_filter_keeps_the_scope()
    {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        Assert.Equal(
            Created(new MediaAssetSearchViewModel(Tag: $"{one:D},{two:D}")).Scope,
            Created(new MediaAssetSearchViewModel(Tag: $"{two:D},{one:D}")).Scope);
    }

    /// <summary>
    /// A term containing the separators the scope is assembled from cannot be made to look like another
    /// field's value, because the term is folded in last and nothing follows it.
    /// </summary>
    [Fact]
    public void A_search_term_cannot_forge_another_field_in_the_scope()
    {
        var forged = Created(new MediaAssetSearchViewModel(Search: "x|channel=instagram"));
        var honest = Created(new MediaAssetSearchViewModel(Channel: "instagram"));

        Assert.NotEqual(honest.Scope, forged.Scope);
    }

    /// <summary>
    /// Structurally valid, bound to this exact scope, and still not a position — a tie-breaker that is not an
    /// id. Only reachable by editing a cursor, and answered the same way.
    /// </summary>
    [Fact]
    public void A_cursor_whose_tie_breaker_is_not_an_id_is_refused()
    {
        var scope = MediaAssetSearchScope.Build(
            Workspace, MediaAssetSearchSort.RecentlyAdded, new MediaAssetSearchFilters());
        var cursor = ReferenceCursor.Encode(DateTimeOffset.UnixEpoch.ToString("O"), "not-an-id", scope);

        Assert.Equal(
            MediaErrorCodes.AssetCursorInvalidRequest,
            Refused(new MediaAssetSearchViewModel(Cursor: cursor)).Code);
    }

    /// <summary>
    /// A timestamp ordering needs a timestamp. Under the alphabetical ordering the same value is a perfectly
    /// good title, which is why the check is per ordering rather than on the cursor alone.
    /// </summary>
    [Fact]
    public void A_sort_value_that_is_not_a_timestamp_is_refused_only_where_one_is_needed()
    {
        var id = Guid.NewGuid().ToString("D");

        foreach (var sort in AllSorts)
        {
            var scope = MediaAssetSearchScope.Build(Workspace, sort, new MediaAssetSearchFilters());
            var model = new MediaAssetSearchViewModel(
                Cursor: ReferenceCursor.Encode("Olive oil cake", id, scope),
                Sort: sort.ToString());

            Assert.Equal(
                sort is MediaAssetSearchSort.Title,
                MediaAssetSearchQueryFactory.TryCreate(model, Workspace, out _, out _));
        }
    }

    // --- Published shape ---------------------------------------------------------------------------------

    /// <summary>
    /// Nothing a library page publishes is or becomes an address, and nothing is a byte. Checked by name
    /// because the alternative is noticing in review that a convenient URL was added.
    /// </summary>
    [Fact]
    public void A_summary_names_no_address_and_no_bytes()
    {
        string[] banned =
            ["Url", "Uri", "Href", "Link", "Address", "Cdn", "Sas", "ObjectKey", "Checksum", "Content"];

        var properties = typeof(MediaAssetSummaryServiceModel).GetProperties();

        Assert.Empty(properties
            .Select(property => property.Name)

            // MediaType is the one allowed "Content"-adjacent fact: a grid needs to know it is a JPEG, and a
            // media type is not an address. The ban is on names that would carry or produce bytes.
            .Where(name => name != "MediaType")
            .Where(name => banned.Any(word => name.Contains(word, StringComparison.Ordinal))));

        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(byte[]));
    }

    /// <summary>
    /// A row computes both halves of its own cursor, so the value a page mints and the value the next page
    /// parses cannot drift apart — and the round trip has to survive every digit of the timestamp, which a
    /// truncating format would not.
    /// </summary>
    [Fact]
    public void A_row_publishes_a_position_that_decodes_back_to_itself()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 891, TimeSpan.Zero).AddTicks(2345);

        foreach (var sort in AllSorts)
        {
            var row = Row(id, createdAt, "Olive oil cake", sort);
            var encoded = ReferenceCursor.Encode(row.SortValue, row.TieBreaker, scope: "t");

            Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
            Assert.True(MediaAssetSearchPosition.TryCreate(sort, cursor!, out var position));
            Assert.Equal(id, position!.MediaAssetId);

            if (sort is MediaAssetSearchSort.Title)
            {
                Assert.Equal("Olive oil cake", position.Title);
            }
            else
            {
                Assert.Equal(createdAt, position.CreatedAt);
            }
        }
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private static MediaAssetSearchSort[] AllSorts =>
        [MediaAssetSearchSort.RecentlyAdded, MediaAssetSearchSort.Title];

    private static MediaAssetSearchRecord Row(
        Guid id, DateTimeOffset createdAt, string title, MediaAssetSearchSort sort) =>
        new(id, title, null, MediaAssetKind.Original, null, null, null, null, null, null, null, 1,
            "image/jpeg", 1200, 800, 1024L, createdAt, createdAt, sort);

    private static string CursorFor(DateTimeOffset createdAt, Guid id)
    {
        var scope = MediaAssetSearchScope.Build(
            Workspace, MediaAssetSearchSort.RecentlyAdded, new MediaAssetSearchFilters());

        return ReferenceCursor.Encode(createdAt.ToString("O"), id.ToString("D"), scope);
    }

    private static MediaAssetSearchCriteria Created(MediaAssetSearchViewModel model)
    {
        Assert.True(
            MediaAssetSearchQueryFactory.TryCreate(model, Workspace, out var criteria, out var error),
            $"expected the query to parse, got {error?.Code}");

        return criteria!;
    }

    private static OperationError Refused(MediaAssetSearchViewModel model)
    {
        Assert.False(MediaAssetSearchQueryFactory.TryCreate(model, Workspace, out _, out var error));

        return error!;
    }
}
