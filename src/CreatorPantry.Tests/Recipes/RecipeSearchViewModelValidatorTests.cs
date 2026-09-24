using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The three things the search validator settles on its own, and the one it deliberately does not.
/// </summary>
public sealed class RecipeSearchViewModelValidatorTests
{
    private static readonly RecipeSearchViewModelValidator Validator = new();

    [Fact]
    public void An_empty_query_is_valid()
    {
        Assert.True(Validate(new RecipeSearchViewModel()).IsValid);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void A_search_term_is_bounded(int length, bool valid)
    {
        Assert.Equal(valid, Validate(new RecipeSearchViewModel(Search: new string('a', length))).IsValid);
    }

    /// <summary>
    /// Structural only. Whether a well-formed cursor belongs to <em>this</em> query is a question a validator
    /// cannot ask — it does not know which workspace was resolved or which filters arrived — and
    /// <see cref="RecipeSearchQueryFactory"/> owns that half.
    /// </summary>
    [Fact]
    public void A_cursor_that_is_not_even_decodable_is_refused_here()
    {
        var failure = Assert.Single(Validate(new RecipeSearchViewModel(Cursor: "%%%not base64%%%")).Errors);

        // The property name is what tells the facade to answer with the cursor's own code rather than the
        // generic one, so it is pinned as the query parameter a caller actually sent.
        Assert.Equal("cursor", failure.PropertyName);
    }

    [Fact]
    public void A_well_formed_cursor_passes_the_validator_whatever_it_was_issued_for()
    {
        var cursor = ReferenceCursor.Encode("2026-01-01T00:00:00.0000000+00:00", $"{Guid.NewGuid():D}", "some other scope");

        Assert.True(Validate(new RecipeSearchViewModel(Cursor: cursor)).IsValid);
    }

    /// <summary>
    /// An inverted range matches nothing, which is legal and almost never intended — and "no recipes" is an
    /// answer a creator cannot debug. Naming the mistake is kinder than returning an empty library.
    /// </summary>
    [Fact]
    public void An_inverted_date_range_is_refused()
    {
        var later = DateTimeOffset.UnixEpoch.AddDays(1);

        Assert.Equal(
            "updatedBefore",
            Assert.Single(Validate(new RecipeSearchViewModel(
                UpdatedFrom: later, UpdatedBefore: DateTimeOffset.UnixEpoch)).Errors).PropertyName);

        Assert.Equal(
            "createdBefore",
            Assert.Single(Validate(new RecipeSearchViewModel(
                CreatedFrom: later, CreatedBefore: DateTimeOffset.UnixEpoch)).Errors).PropertyName);
    }

    [Fact]
    public void An_empty_range_is_refused_because_it_can_match_nothing()
    {
        Assert.False(Validate(new RecipeSearchViewModel(
            UpdatedFrom: DateTimeOffset.UnixEpoch, UpdatedBefore: DateTimeOffset.UnixEpoch)).IsValid);
    }

    [Fact]
    public void One_bound_on_its_own_is_valid()
    {
        Assert.True(Validate(new RecipeSearchViewModel(UpdatedFrom: DateTimeOffset.UnixEpoch)).IsValid);
        Assert.True(Validate(new RecipeSearchViewModel(UpdatedBefore: DateTimeOffset.UnixEpoch)).IsValid);
    }

    /// <summary>
    /// An out-of-range page size is clamped, not refused, matching every other paged route: a client cannot fail
    /// a read by asking for too much. Validating it here would turn the clamp into a rejection.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_000)]
    public void The_page_size_is_not_validated(int limit)
    {
        Assert.True(Validate(new RecipeSearchViewModel(Limit: limit)).IsValid);
    }

    /// <summary>
    /// Filter values are not validated here either, deliberately: a validator that only said "that is not a
    /// status" would leave the factory parsing it a second time to find out which one it was.
    /// </summary>
    [Fact]
    public void Filter_values_are_left_to_the_factory()
    {
        Assert.True(Validate(new RecipeSearchViewModel(Status: "Published", Tag: "not-a-guid")).IsValid);
    }

    private static FluentValidation.Results.ValidationResult Validate(RecipeSearchViewModel model) =>
        Validator.Validate(model);
}
