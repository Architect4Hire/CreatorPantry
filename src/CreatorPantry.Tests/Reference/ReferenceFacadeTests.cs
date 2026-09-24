using CreatorPantry.Domain.Modules.Measurement.Business;
using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Modules.Ingredients.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// The facade's own responsibilities: validate, serve from cache, read through on a miss. Business is a
/// counter here, so "did this reach the database" is directly observable.
/// </summary>
public sealed class ReferenceFacadeTests
{
    private readonly FakeApplicationCache _cache = new();
    private readonly CountingReferenceBusiness _business = new();

    // --- Cache hit and miss ------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_read_misses_and_the_second_hits()
    {
        var vocabulary = Vocabulary();

        var first = await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);
        var second = await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(1, _business.Calls);
        Assert.Single(_cache.Writes);
        Assert.Equal(2, _cache.Reads.Count);
        Assert.Equal(first.Value!.Items.Single().Code, second.Value!.Items.Single().Code);
    }

    [Fact]
    public async Task A_disabled_cache_reads_through_every_time()
    {
        var vocabulary = Vocabulary();
        _cache.Disabled = true;

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);

        Assert.Equal(2, _business.Calls);
    }

    [Fact]
    public async Task Different_queries_do_not_share_a_cache_entry()
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: "italian"), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: "french"), TestContext.Current.CancellationToken);

        Assert.Equal(2, _business.Calls);
        Assert.Equal(2, _cache.Writes.Distinct().Count());
    }

    [Fact]
    public async Task Different_resources_do_not_share_a_cache_entry()
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);
        await vocabulary.ListCoursesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);

        Assert.Equal(2, _business.Calls);
        Assert.Equal(2, _cache.Writes.Distinct().Count());
    }

    /// <summary>
    /// Two requests that clamp to the same page size return the same rows, so they must share one entry
    /// rather than caching one answer twice.
    /// </summary>
    [Fact]
    public async Task Requests_that_clamp_to_the_same_page_share_a_cache_entry()
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Limit: 1000), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Limit: 100), TestContext.Current.CancellationToken);

        Assert.Equal(1, _business.Calls);
    }

    /// <summary>Casing and surrounding whitespace do not change which rows match, so they share an entry.</summary>
    [Fact]
    public async Task Searches_differing_only_by_case_or_padding_share_a_cache_entry()
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: "Italian"), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: "  ITALIAN  "), TestContext.Current.CancellationToken);

        Assert.Equal(1, _business.Calls);
    }

    /// <summary>
    /// The regression this file previously asserted backwards. <c>Tex-Mex</c> and <c>tex mex</c> normalize to
    /// one key but do <em>not</em> match the same rows — the repositories filter display names and codes on
    /// the raw term, and only the hyphenated spelling is a substring of "Tex-Mex". Sharing an entry meant one
    /// spelling served the other's answer, empty page included, for the whole cache lifetime.
    /// </summary>
    [Theory]
    [InlineData("Tex-Mex", "tex mex")]
    [InlineData("gluten-free", "gluten free")]
    [InlineData("main-course", "main course")]
    public async Task Searches_that_match_different_rows_never_share_a_cache_entry(string first, string second)
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: first), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: second), TestContext.Current.CancellationToken);

        Assert.Equal(2, _business.Calls);
        Assert.Equal(2, _cache.Writes.Distinct().Count());
    }

    /// <summary>
    /// The cursor is caller-controlled in the fullest sense — its fingerprint is unkeyed, so anyone can mint
    /// one carrying any tie-breaker they like, including one containing the key's own separators. Before the
    /// position was length-prefixed, these two requests produced the same cache key while running different
    /// queries.
    /// </summary>
    [Fact]
    public async Task A_forged_cursor_cannot_collide_with_another_querys_cache_key()
    {
        var vocabulary = Vocabulary();

        // Both cursors are minted for the scope they are then used with, so both pass validation.
        var first = new ReferenceQueryViewModel(
            Search: "cc",
            Cursor: ReferenceCursor.Encode(
                "x", "a|q=bb", ReferenceQueryKey.Scope("cuisines", ReferencePolicy.NormalizeSearch("cc"))));

        var second = new ReferenceQueryViewModel(
            Search: "bb|q=cc",
            Cursor: ReferenceCursor.Encode(
                "x", "a", ReferenceQueryKey.Scope("cuisines", ReferencePolicy.NormalizeSearch("bb|q=cc"))));

        Assert.True((await vocabulary.ListCuisinesAsync(first, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await vocabulary.ListCuisinesAsync(second, TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(2, _business.Calls);
        Assert.Equal(2, _cache.Writes.Distinct().Count());
    }

    /// <summary>
    /// The repositories' keyset predicates use both halves of a cursor, so a key naming only the tie-breaker
    /// stands for two different queries. That happens without an attacker: a deploy that renames a display
    /// name while its code stays put produces exactly this pair.
    /// </summary>
    [Fact]
    public void Two_cursors_differing_only_by_sort_value_do_not_share_a_key()
    {
        var scope = ReferenceQueryKey.Scope("cuisines", null);

        var before = ReferenceQueryKey.Build(null, new ReferenceCursor("Italian", "italian", scope), 25);
        var after = ReferenceQueryKey.Build(null, new ReferenceCursor("Italiana", "italian", scope), 25);

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Free text is the last segment of a key precisely so it cannot be read as another field. A search term
    /// that spells out the key's own syntax must not collide with the query it imitates.
    /// </summary>
    [Fact]
    public async Task A_search_term_cannot_forge_another_querys_cache_key()
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(
            new ReferenceQueryViewModel(Search: "x|limit=100|cursor="), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(
            new ReferenceQueryViewModel(Search: "x", Limit: 100), TestContext.Current.CancellationToken);

        Assert.Equal(2, _business.Calls);
        Assert.Equal(2, _cache.Writes.Distinct().Count());
    }

    /// <summary>A term below the length floor is ignored, so it caches as the unfiltered page it returns.</summary>
    [Fact]
    public async Task A_search_below_the_length_floor_shares_the_unfiltered_entry()
    {
        var vocabulary = Vocabulary();

        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(Search: "a"), TestContext.Current.CancellationToken);

        Assert.Equal(1, _business.Calls);
    }

    // --- Cache scoping -----------------------------------------------------------------------------------

    /// <summary>
    /// The restriction this seam exists under: reference pages are cached globally and never under a
    /// workspace. <c>CacheKeys.Global</c> has no workspace overload, so this is belt and braces — but it is
    /// the assertion that would catch someone hand-building a key later.
    /// </summary>
    [Fact]
    public async Task Every_cache_key_is_global_and_never_workspace_scoped()
    {
        var vocabulary = Vocabulary();

        await Ingredients().ListIngredientsAsync(new IngredientQueryViewModel(Search: "flour", Category: "baking"), TestContext.Current.CancellationToken);
        await Measurement().ListUnitsAsync(new MeasurementUnitQueryViewModel(Dimension: "Mass"), TestContext.Current.CancellationToken);
        await vocabulary.ListCuisinesAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);
        await vocabulary.ListAllergensAsync(new ReferenceQueryViewModel(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(_cache.Writes);
        Assert.All(_cache.Writes, key =>
        {
            Assert.StartsWith("global:reference:", key, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace", key, StringComparison.OrdinalIgnoreCase);
        });
    }

    // --- Validation --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_corrupt_cursor_is_rejected_with_its_own_code_and_never_reaches_business()
    {
        var vocabulary = Vocabulary();

        var result = await vocabulary.ListCuisinesAsync(
            new ReferenceQueryViewModel(Cursor: "not-a-cursor!!"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
        Assert.Empty(_cache.Writes);
    }

    [Fact]
    public async Task A_valid_cursor_is_accepted()
    {
        var vocabulary = Vocabulary();

        var result = await vocabulary.ListCuisinesAsync(
            new ReferenceQueryViewModel(Cursor: CursorFor("cuisines")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, _business.Calls);
    }

    // --- Cursor scope -------------------------------------------------------------------------------------

    /// <summary>
    /// A cursor names a position in one specific ordered set. Replayed against another resource it still
    /// decodes and still produces a page — just not the page it names, with rows silently skipped or repeated.
    /// Binding the query into the cursor turns a wrong answer into an error.
    /// </summary>
    [Fact]
    public async Task A_cursor_from_another_resource_is_refused()
    {
        var vocabulary = Vocabulary();

        var result = await vocabulary.ListAllergensAsync(
            new ReferenceQueryViewModel(Cursor: CursorFor("cuisines")), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("cursor"));
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>The likelier mistake in practice: a client keeps paging after changing the filter.</summary>
    [Fact]
    public async Task A_cursor_is_refused_once_the_search_changes()
    {
        var vocabulary = Vocabulary();
        var cursor = CursorFor("cuisines", ("q", "italian"));

        var sameSearch = await vocabulary.ListCuisinesAsync(
            new ReferenceQueryViewModel(Search: "italian", Cursor: cursor), TestContext.Current.CancellationToken);
        var changedSearch = await vocabulary.ListCuisinesAsync(
            new ReferenceQueryViewModel(Search: "french", Cursor: cursor), TestContext.Current.CancellationToken);

        Assert.True(sameSearch.Succeeded);
        Assert.False(changedSearch.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, changedSearch.Error!.Code);
    }

    [Fact]
    public async Task A_cursor_is_refused_once_a_filter_changes()
    {
        var vocabulary = Vocabulary();
        var cursor = CursorFor("ingredients", ("category", "baking"), ("q", null));

        var sameFilter = await Ingredients().ListIngredientsAsync(
            new IngredientQueryViewModel(Category: "baking", Cursor: cursor), TestContext.Current.CancellationToken);
        var changedFilter = await Ingredients().ListIngredientsAsync(
            new IngredientQueryViewModel(Category: "produce", Cursor: cursor), TestContext.Current.CancellationToken);

        Assert.True(sameFilter.Succeeded);
        Assert.False(changedFilter.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, changedFilter.Error!.Code);
    }

    /// <summary>
    /// Page size is deliberately outside the scope: a position is a position, so a client may legitimately ask
    /// for a different number of rows after it.
    /// </summary>
    [Fact]
    public async Task A_cursor_survives_a_change_of_page_size()
    {
        var result = await Vocabulary().ListCuisinesAsync(
            new ReferenceQueryViewModel(Cursor: CursorFor("cuisines"), Limit: 7),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    /// <summary>Mints the cursor a real page of <paramref name="resource"/> would have returned.</summary>
    private static string CursorFor(string resource, params (string Name, string? Value)[] filters)
    {
        var search = filters.FirstOrDefault(filter => filter.Name == "q").Value;
        var rest = filters.Where(filter => filter.Name != "q").ToArray();

        return ReferenceCursor.Encode(
            "Italian",
            "italian",
            ReferenceQueryKey.Scope(resource, ReferencePolicy.NormalizeSearch(search), rest));
    }

    [Fact]
    public async Task An_unknown_dimension_is_rejected_as_a_query_error()
    {
        var vocabulary = Vocabulary();

        var result = await Measurement().ListUnitsAsync(
            new MeasurementUnitQueryViewModel(Dimension: "furlongs"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.QueryInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("dimension"));
        Assert.Equal(0, _business.Calls);
    }

    [Theory]
    [InlineData("Mass")]
    [InlineData("mass")]
    [InlineData("VOLUME")]
    public async Task A_dimension_is_accepted_in_any_casing(string dimension)
    {
        var result = await Measurement().ListUnitsAsync(
            new MeasurementUnitQueryViewModel(Dimension: dimension), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// <c>Enum.TryParse</c> also accepts numeric strings and comma-delimited combinations, so an unguarded
    /// filter would answer <c>?dimension=99</c> with an empty 200 and <c>?dimension=Mass,Volume</c> with a
    /// silently ORed value. Both are refused, because narrowing an accepted value later is a breaking change.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("Mass,Volume")]
    [InlineData("furlongs")]
    public async Task A_dimension_that_is_not_a_declared_name_is_rejected(string dimension)
    {
        var result = await Measurement().ListUnitsAsync(
            new MeasurementUnitQueryViewModel(Dimension: dimension), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.QueryInvalid, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_malformed_category_code_is_rejected()
    {
        var result = await Ingredients().ListIngredientsAsync(
            new IngredientQueryViewModel(Category: "Not A Code"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.QueryInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("category"));
    }

    [Fact]
    public async Task An_over_long_search_term_is_rejected()
    {
        var result = await Vocabulary().ListCuisinesAsync(
            new ReferenceQueryViewModel(Search: new string('a', ReferencePolicy.SearchMaxLength + 1)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.Error!.FieldErrors.ContainsKey("search"));
    }

    /// <summary>An out-of-range page size is clamped, not refused — the one input that never fails a request.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000)]
    public async Task An_out_of_range_page_size_is_accepted(int limit)
    {
        var result = await Vocabulary().ListCuisinesAsync(
            new ReferenceQueryViewModel(Limit: limit), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    // --- Candidate resolution (7.3) -----------------------------------------------------------------------

    [Fact]
    public async Task Resolving_candidates_loads_the_index_once_and_caches_it_as_one_entry()
    {
        var ingredients = Ingredients();

        await ingredients.ResolveCandidatesAsync(["flour", "sugar"], TestContext.Current.CancellationToken);
        await ingredients.ResolveCandidatesAsync(["butter", "eggs"], TestContext.Current.CancellationToken);

        // One index load regardless of how many candidates, or how many separate calls, asked against it —
        // never one cache entry per candidate string.
        Assert.Equal(1, _business.Calls);
        Assert.Single(_cache.Writes);
    }

    [Fact]
    public async Task A_disabled_cache_reloads_the_index_every_call()
    {
        var units = Measurement();
        _cache.Disabled = true;

        await units.ResolveCandidatesAsync(["cups"], TestContext.Current.CancellationToken);
        await units.ResolveCandidatesAsync(["cups"], TestContext.Current.CancellationToken);

        Assert.Equal(2, _business.Calls);
    }

    [Fact]
    public async Task An_empty_candidate_list_succeeds_without_loading_the_index()
    {
        var result = await Ingredients().ResolveCandidatesAsync([], TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Too_many_candidates_are_rejected_without_loading_the_index()
    {
        var tooMany = Enumerable.Range(0, ReferencePolicy.MaxMatchCandidates + 1).Select(i => $"item{i}").ToArray();

        var result = await Ingredients().ResolveCandidatesAsync(tooMany, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CandidatesInvalid, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task An_over_long_candidate_is_rejected_without_loading_the_index()
    {
        var result = await Measurement().ResolveCandidatesAsync(
            [new string('a', ReferencePolicy.MaxMatchCandidateLength + 1)], TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CandidatesInvalid, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Ingredient_and_unit_candidate_indexes_do_not_share_a_cache_key()
    {
        await Ingredients().ResolveCandidatesAsync(["flour"], TestContext.Current.CancellationToken);
        await Measurement().ResolveCandidatesAsync(["cups"], TestContext.Current.CancellationToken);

        Assert.Equal(2, _cache.Writes.Count);
        Assert.Equal(2, _cache.Writes.Distinct().Count());
    }

    /// <remarks>
    /// The three facades share one <see cref="FakeApplicationCache"/> and one
    /// <see cref="CountingReferenceBusiness"/>, so a test can still assert that two requests to
    /// <em>different</em> modules do not collide on a cache key — which is the property the resource literal
    /// in each facade exists to guarantee.
    /// </remarks>
    private IVocabularyFacade Vocabulary() =>
        new VocabularyFacade(new ReferenceQueryViewModelValidator(), _business, new CachedPageReader(_cache));

    private IMeasurementFacade Measurement() =>
        new MeasurementFacade(new MeasurementUnitQueryViewModelValidator(), _business, _cache, new CachedPageReader(_cache));

    private IIngredientFacade Ingredients() =>
        new IngredientFacade(new IngredientQueryViewModelValidator(), _business, _cache, new CachedPageReader(_cache));
}
