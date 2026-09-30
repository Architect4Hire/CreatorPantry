using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using FluentValidation.Results;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Shape validation for the test-history query: the two things settled at the edge, and the several deliberately
/// left to <see cref="TestRunHistoryQueryFactory"/>.
/// </summary>
public sealed class TestRunHistoryViewModelValidatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TestRunHistoryViewModelValidator _validator = new();

    [Fact]
    public void An_empty_query_is_valid()
    {
        Assert.True(Validate(new TestRunHistoryViewModel()).IsValid);
    }

    [Fact]
    public void A_cursor_that_is_not_encoded_is_refused_by_the_parameter_a_caller_sent()
    {
        var result = Validate(new TestRunHistoryViewModel(Cursor: "not base64 at all"));

        Assert.Equal("cursor", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void A_well_formed_cursor_passes_the_edge()
    {
        var cursor = ReferenceCursor.Encode("2026-09-20T12:00:00.0000000+00:00", Guid.NewGuid().ToString("D"), "any");

        Assert.True(Validate(new TestRunHistoryViewModel(Cursor: cursor)).IsValid);
    }

    /// <summary>
    /// Whether that cursor belongs to <em>this</em> recipe under <em>these</em> filters is not a question a
    /// validator can ask — it knows neither the resolved workspace nor the route. The factory owns that half, and
    /// its own tests cover it.
    /// </summary>
    [Fact]
    public void A_cursor_for_some_other_scope_still_passes_the_edge()
    {
        var cursor = ReferenceCursor.Encode(
            "2026-09-20T12:00:00.0000000+00:00", Guid.NewGuid().ToString("D"), "a completely different scope");

        Assert.True(Validate(new TestRunHistoryViewModel(Cursor: cursor)).IsValid);
    }

    [Fact]
    public void An_inverted_tested_range_is_refused_by_name()
    {
        var result = Validate(new TestRunHistoryViewModel(
            TestedFrom: Noon, TestedBefore: Noon.AddDays(-1)));

        Assert.Equal("testedBefore", Assert.Single(result.Errors).PropertyName);
    }

    /// <summary>
    /// Equal bounds are inverted too, because the range is half-open: <c>from == before</c> selects nothing, which
    /// is legal and never what a caller meant.
    /// </summary>
    [Fact]
    public void An_empty_tested_range_is_refused()
    {
        Assert.False(Validate(new TestRunHistoryViewModel(TestedFrom: Noon, TestedBefore: Noon)).IsValid);
    }

    [Fact]
    public void One_bound_on_its_own_is_valid()
    {
        Assert.True(Validate(new TestRunHistoryViewModel(TestedFrom: Noon)).IsValid);
        Assert.True(Validate(new TestRunHistoryViewModel(TestedBefore: Noon)).IsValid);
    }

    /// <summary>
    /// A page size is clamped rather than refused, matching every other paged route: a client cannot fail a read by
    /// asking for too much. Validating it would turn a clamp into a rejection.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100_000)]
    public void An_out_of_range_page_size_is_not_a_validation_failure(int limit)
    {
        Assert.True(Validate(new TestRunHistoryViewModel(Limit: limit)).IsValid);
    }

    /// <summary>
    /// The filters are not validated here either. Each is a value this module has to parse into a typed filter, and
    /// a validator that only said "that is not an outcome" would leave the factory parsing it again — so the
    /// factory owns both the parse and the refusal, and names the parameter in it.
    /// </summary>
    [Fact]
    public void A_malformed_filter_is_left_for_the_factory_to_refuse()
    {
        var result = Validate(new TestRunHistoryViewModel(
            Version: "not a number", TestedBy: "not an id", Outcome: "Burnt", Issues: "Maybe"));

        Assert.True(result.IsValid);
    }

    private ValidationResult Validate(TestRunHistoryViewModel model) => _validator.Validate(model);
}
