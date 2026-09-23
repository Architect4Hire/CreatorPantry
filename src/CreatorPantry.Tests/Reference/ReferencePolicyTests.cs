using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Tests.Reference;

public sealed class ReferencePolicyTests
{
    /// <summary>
    /// Clamped, never rejected. Asking for more than the maximum is answered with the maximum, which is what
    /// keeps an over-eager client from turning a page size into a 400 it has to handle.
    /// </summary>
    [Theory]
    [InlineData(null, 25)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(1000, 100)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MinValue, 1)]
    public void Page_size_is_clamped_into_the_published_bounds(int? requested, int expected) =>
        Assert.Equal(expected, ReferencePolicy.ClampPageSize(requested));

    /// <summary>The bounds are not invented here; they restate the reviewed OpenAPI Limit parameter.</summary>
    [Fact]
    public void The_clamp_matches_the_documented_parameter()
    {
        Assert.Equal(1, ReferencePolicy.MinPageSize);
        Assert.Equal(100, ReferencePolicy.MaxPageSize);
        Assert.Equal(25, ReferencePolicy.DefaultPageSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData(" a ")]
    public void A_term_that_is_too_short_is_ignored_rather_than_rejected(string? search) =>
        Assert.Null(ReferencePolicy.NormalizeSearch(search));

    [Theory]
    [InlineData("Flour", "flour", "flour")]
    [InlineData("  Tex-Mex  ", "tex-mex", "tex mex")]
    [InlineData("Jalapeño", "jalapeño", "jalapeno")]
    [InlineData("All-Purpose Flour", "all-purpose flour", "all purpose flour")]
    public void A_term_is_kept_in_both_searchable_forms(string search, string raw, string normalized)
    {
        var result = ReferencePolicy.NormalizeSearch(search);

        Assert.NotNull(result);
        Assert.Equal(raw, result.Raw);
        Assert.Equal(normalized, result.Normalized);
    }

    /// <summary>
    /// The length floor applies to what was typed, not to what survived normalization: "a-b" is a two-letter
    /// search a creator meant, even though normalizing it leaves two letters and a space.
    /// </summary>
    [Fact]
    public void The_length_floor_is_measured_before_normalizing()
    {
        var result = ReferencePolicy.NormalizeSearch("a-b");

        Assert.NotNull(result);
        Assert.Equal("a b", result.Normalized);
    }

    /// <summary>
    /// Punctuation alone normalizes to nothing, and an empty key is a substring of every row. Falling back to
    /// the literal term is what stops a search that should match nothing from matching the whole catalogue.
    /// </summary>
    [Fact]
    public void A_term_that_normalizes_away_is_searched_literally()
    {
        var result = ReferencePolicy.NormalizeSearch("!!!");

        Assert.NotNull(result);
        Assert.Equal("!!!", result.Normalized);
        Assert.NotEqual(string.Empty, result.Normalized);
    }
}
