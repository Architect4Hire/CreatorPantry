using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Edge validation for the history query, and the invariant the facade's single error code rests on.
/// </summary>
public sealed class RecipeVersionHistoryViewModelValidatorTests
{
    private readonly RecipeVersionHistoryViewModelValidator _validator = new();

    /// <summary>
    /// <strong>The assumption <c>RecipeFacade.GetVersionHistoryAsync</c> is built on.</strong> It answers every
    /// validation failure with <see cref="RecipeErrorCodes.CursorInvalidRequest"/>, which is right only while
    /// the cursor is the sole thing that can fail — the sibling search facade has to branch on the property
    /// name precisely because its validator does not have that property.
    /// <para>
    /// So this is not a test of the validator so much as a tripwire on the facade: the moment a second rule is
    /// added here, this fails and whoever added it has to decide what code that failure deserves, rather than
    /// discovering later that a paging client was told to throw away a perfectly good cursor.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_rule_is_about_the_cursor()
    {
        var properties = _validator.CreateDescriptor().Rules
            .Select(rule => rule.PropertyName)
            .Distinct();

        Assert.Equal(["cursor"], properties);
    }

    [Fact]
    public void A_query_with_neither_parameter_is_valid()
    {
        Assert.True(_validator.Validate(new RecipeVersionHistoryViewModel()).IsValid);
    }

    [Fact]
    public void A_well_formed_cursor_is_accepted_whatever_it_was_issued_for()
    {
        // Structure only. Whether this cursor belongs to the recipe being read is a question the validator
        // cannot ask — it knows neither the workspace nor the route — and the query factory owns that half.
        var cursor = ReferenceCursor.Encode("3", Guid.NewGuid().ToString("D"), "some other scope");

        Assert.True(_validator.Validate(new RecipeVersionHistoryViewModel(Cursor: cursor)).IsValid);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("%%%")]
    [InlineData("!!!!")]
    public void A_cursor_that_is_not_one_is_refused_naming_the_parameter(string cursor)
    {
        var result = _validator.Validate(new RecipeVersionHistoryViewModel(Cursor: cursor));

        Assert.False(result.IsValid);
        Assert.Equal("cursor", Assert.Single(result.Errors).PropertyName);
    }

    /// <summary>
    /// An out-of-range page size is clamped downstream rather than refused here, so that a client cannot fail a
    /// read by asking for too much — the rule every paged route in the platform follows.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000)]
    public void A_page_size_is_never_refused(int limit)
    {
        Assert.True(_validator.Validate(new RecipeVersionHistoryViewModel(Limit: limit)).IsValid);
    }
}
