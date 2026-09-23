using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Ingredients.Seeding;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// The reference repositories against a real database holding the real seeded catalogue. These are the tests
/// that prove the keyset predicates actually translate to SQL — a query that compiles in C# and throws at
/// runtime would pass every test above this layer.
/// </summary>
public sealed class ReferenceRepositoryTests : IAsyncLifetime
{
    private ReferenceCatalogFixture _catalog = null!;

    public async ValueTask InitializeAsync() => _catalog = await ReferenceCatalogFixture.StartAsync();

    public async ValueTask DisposeAsync() => await _catalog.DisposeAsync();

    // --- Paging ------------------------------------------------------------------------------------------

    /// <summary>
    /// Walking the cursor to the end returns every cuisine exactly once, in order. This is the property that
    /// matters: a keyset that drifted would show up here as a duplicate or a gap, not as an exception.
    /// </summary>
    [Fact]
    public async Task Paging_to_the_end_yields_every_row_once_in_order()
    {
        var expected = CatalogueSeedData.Cuisines().Count;

        var all = await DrainAsync(cursor => Vocabularies().ListCuisinesAsync(
            new ReferenceQuery(null, cursor, Limit: 4, Scope: "t"), TestContext.Current.CancellationToken));

        Assert.Equal(expected, all.Count);
        Assert.Equal(all.Select(entry => entry.Code).Distinct().Count(), all.Count);
        Assert.Equal(all.Select(entry => entry.DisplayName).Order(StringComparer.Ordinal), all.Select(entry => entry.DisplayName));
    }

    /// <summary>The last page reports no cursor, which is how a client knows to stop rather than by counting.</summary>
    [Fact]
    public async Task The_last_page_has_no_next_cursor()
    {
        var (rows, hasMore) = await Catalog().ListAllergensAsync(
            new ReferenceQuery(null, null, Limit: 100, Scope: "t"), TestContext.Current.CancellationToken);

        Assert.Equal(CatalogueSeedData.Allergens().Count, rows.Count);
        Assert.False(hasMore);
    }

    /// <summary>
    /// A page that exactly fills the limit still reports no cursor when nothing follows. The probe row is what
    /// makes that distinguishable — without it, a full page is ambiguous.
    /// </summary>
    [Fact]
    public async Task An_exactly_full_final_page_reports_no_next_cursor()
    {
        var total = CatalogueSeedData.Allergens().Count;

        var (rows, hasMore) = await Catalog().ListAllergensAsync(
            new ReferenceQuery(null, null, Limit: total, Scope: "t"), TestContext.Current.CancellationToken);

        Assert.Equal(total, rows.Count);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task Ingredients_page_to_the_end_without_repeating()
    {
        var expected = SampleIngredientSeedData.Ingredients().Count;

        var all = await DrainAsync(cursor => Ingredients().ListAsync(
            new IngredientQuery(null, null, cursor, Limit: 7, Scope: "t"), TestContext.Current.CancellationToken));

        Assert.Equal(expected, all.Count);
        Assert.Equal(all.Select(row => row.NormalizedName).Distinct().Count(), all.Count);
    }

    // --- Search ------------------------------------------------------------------------------------------

    /// <summary>
    /// The payoff for seeding aliases: a creator searching the word they use finds the catalogue's own name
    /// for it.
    /// </summary>
    [Theory]
    [InlineData("scallion", "green onion")]
    [InlineData("plain flour", "all-purpose flour")]
    [InlineData("cornflour", "cornstarch")]
    public async Task An_ingredient_is_found_by_its_alias(string term, string expectedCanonicalName)
    {
        var (rows, _) = await Ingredients().ListAsync(
            new IngredientQuery(ReferencePolicy.NormalizeSearch(term), null, null, Limit: 25, Scope: "t"),
            TestContext.Current.CancellationToken);

        Assert.Contains(rows, row => row.CanonicalName == expectedCanonicalName);
    }

    [Fact]
    public async Task A_cuisine_is_found_by_its_alias()
    {
        var (rows, _) = await Vocabularies().ListCuisinesAsync(
            new ReferenceQuery(ReferencePolicy.NormalizeSearch("USA"), null, Limit: 25, Scope: "t"),
            TestContext.Current.CancellationToken);

        Assert.Equal("american", Assert.Single(rows).Code);
    }

    /// <summary>
    /// Unit aliases normalize differently from everything else — separators are stripped outright, so
    /// "deg c" is stored as "degc". Searching them with the ingredient rule, which turns a separator into a
    /// space, would look for "deg c" and silently match nothing.
    /// </summary>
    [Fact]
    public async Task A_unit_is_found_by_an_alias_that_normalizes_differently()
    {
        var (rows, _) = await Units().ListAsync(
            new MeasurementUnitQuery(ReferencePolicy.NormalizeSearch("deg c"), null, null, Limit: 25, Scope: "t"),
            TestContext.Current.CancellationToken);

        Assert.Equal("celsius", Assert.Single(rows).Code);
    }

    /// <summary>
    /// A creator searching "pint" still <em>finds</em> the US pint — search is a picker, and showing someone
    /// their options is not the hazard. What must not exist is an alias that resolves the bare word silently,
    /// which is the mechanism a recipe-line importer uses. The display name says "US pint" so the choice is
    /// made with open eyes.
    /// </summary>
    [Theory]
    [InlineData("pint", "pint-us")]
    [InlineData("quart", "quart-us")]
    [InlineData("gallon", "gallon-us")]
    public async Task An_ambiguous_unit_name_is_discoverable_but_labelled_by_tradition(string term, string expectedCode)
    {
        var (rows, _) = await Units().ListAsync(
            new MeasurementUnitQuery(ReferencePolicy.NormalizeSearch(term), null, null, Limit: 25, Scope: "t"),
            TestContext.Current.CancellationToken);

        var only = Assert.Single(rows);
        Assert.Equal(expectedCode, only.Code);
        Assert.StartsWith("US ", only.DisplayName, StringComparison.Ordinal);
    }

    /// <summary>Case must not decide a match, on either database: SQL Server folds it by collation, SQLite does not.</summary>
    [Theory]
    [InlineData("ITALIAN")]
    [InlineData("italian")]
    [InlineData("Italian")]
    public async Task Search_is_case_insensitive(string term)
    {
        var (rows, _) = await Vocabularies().ListCuisinesAsync(
            new ReferenceQuery(ReferencePolicy.NormalizeSearch(term), null, Limit: 25, Scope: "t"),
            TestContext.Current.CancellationToken);

        Assert.Contains(rows, row => row.Code == "italian");
    }

    [Fact]
    public async Task Search_combines_with_paging()
    {
        var all = await DrainAsync(cursor => Ingredients().ListAsync(
            new IngredientQuery(ReferencePolicy.NormalizeSearch("flour"), null, cursor, Limit: 1, Scope: "t"),
            TestContext.Current.CancellationToken));

        Assert.True(all.Count >= 2, "expected at least the two flours");
        Assert.Equal(all.Select(row => row.NormalizedName).Distinct().Count(), all.Count);
    }

    // --- Filtering ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingredients_filter_by_food_category()
    {
        var (rows, _) = await Ingredients().ListAsync(
            new IngredientQuery(null, "dairy-and-eggs", null, Limit: 100, Scope: "t"), TestContext.Current.CancellationToken);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal("dairy-and-eggs", row.FoodCategoryCode));
    }

    [Fact]
    public async Task An_unknown_category_matches_nothing_rather_than_everything()
    {
        var (rows, _) = await Ingredients().ListAsync(
            new IngredientQuery(null, "not-a-category", null, Limit: 100, Scope: "t"), TestContext.Current.CancellationToken);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Units_filter_by_dimension()
    {
        var (rows, _) = await Units().ListAsync(
            new MeasurementUnitQuery(null, MeasurementDimension.Mass, null, Limit: 100, Scope: "t"),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(MeasurementDimension.Mass, row.Dimension));
    }

    // --- Projection --------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ingredient_carries_its_category_unit_and_aliases()
    {
        var (rows, _) = await Ingredients().ListAsync(
            new IngredientQuery(ReferencePolicy.NormalizeSearch("garlic"), null, null, Limit: 25, Scope: "t"),
            TestContext.Current.CancellationToken);

        var garlic = Assert.Single(rows, row => row.CanonicalName == "garlic");
        Assert.Equal("produce", garlic.FoodCategoryCode);
        Assert.Equal("clove", garlic.DefaultCountUnitCode);
        Assert.Contains("garlic cloves", garlic.Aliases);
    }

    /// <summary>The flag exists so a caution can be required; a read that dropped it would silently disarm that.</summary>
    [Fact]
    public async Task Techniques_carry_the_safety_caution_flag()
    {
        var (rows, _) = await Vocabularies().ListTechniquesAsync(
            new ReferenceQuery(null, null, Limit: 100, Scope: "t"), TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(rows, row => row.Code == "water-bath-canning").RequiresSafetyCaution);
        Assert.False(Assert.Single(rows, row => row.Code == "bake").RequiresSafetyCaution);
    }

    [Fact]
    public async Task Allergens_carry_the_description_that_defines_what_they_cover()
    {
        var (rows, _) = await Catalog().ListAllergensAsync(
            new ReferenceQuery(null, null, Limit: 100, Scope: "t"), TestContext.Current.CancellationToken);

        var treeNuts = Assert.Single(rows, row => row.Code == "tree-nuts");
        Assert.Contains("coconut", treeNuts.Description, StringComparison.OrdinalIgnoreCase);
    }

    // --- Cancellation ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_token_stops_the_query()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Vocabularies().ListCuisinesAsync(new ReferenceQuery(null, null, Limit: 25, Scope: "t"), cancellation.Token));
    }

    [Fact]
    public async Task A_cancelled_token_stops_an_ingredient_query()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Ingredients().ListAsync(new IngredientQuery(null, null, null, Limit: 25, Scope: "t"), cancellation.Token));
    }

    // --- Harness -----------------------------------------------------------------------------------------

    /// <summary>Follows every cursor to the end, guarding against a query that never terminates.</summary>
    /// <summary>
    /// Follows every page to the end, guarding against a query that never terminates.
    /// </summary>
    /// <remarks>
    /// The cursor is built here from the last row, the way Business does it — a repository reports only
    /// whether another page follows, because a cursor is bound to a resource and filters that a repository is
    /// never told about.
    /// </remarks>
    private static async Task<List<T>> DrainAsync<T>(
        Func<ReferenceCursor?, Task<(IReadOnlyList<T> Rows, bool HasMore)>> page)
        where T : IReferenceRow
    {
        var all = new List<T>();
        ReferenceCursor? cursor = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var (rows, hasMore) = await page(cursor);
            all.AddRange(rows);

            if (!hasMore)
            {
                return all;
            }

            Assert.NotEmpty(rows);
            cursor = new ReferenceCursor(rows[^1].SortValue, rows[^1].TieBreaker, ScopeFingerprint: "t");
        }

        Assert.Fail("paging did not terminate");

        return all;
    }

    private IIngredientRepository Ingredients() => new IngredientRepository(_catalog.CreateContext());

    private IMeasurementUnitRepository Units() => new MeasurementUnitRepository(_catalog.CreateContext());

    private IControlledVocabularyRepository Vocabularies() => new ControlledVocabularyRepository(_catalog.CreateContext());

    private IReferenceCatalogRepository Catalog() => new ReferenceCatalogRepository(_catalog.CreateContext());
}
