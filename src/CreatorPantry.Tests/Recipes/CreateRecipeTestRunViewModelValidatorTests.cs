using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Shape and format rules for a submitted test run — everything decidable without a database or a clock.
/// </summary>
/// <remarks>
/// What is deliberately not here: whether the version exists, whether the recipe is visible, and whether
/// <c>testedAt</c> is in the past. The first two need a lookup and the third needs the clock, so all three are
/// <c>RecipeTestRunBusinessTests</c>' business.
/// </remarks>
public sealed class CreateRecipeTestRunViewModelValidatorTests
{
    private readonly CreateRecipeTestRunViewModelValidator _validator = new();

    private static CreateRecipeTestRunViewModel Valid() => new()
    {
        SourceVersionNumber = 1,
        TestedAt = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero),
    };

    private IReadOnlyList<string> ErrorsFor(CreateRecipeTestRunViewModel model) =>
        [.. _validator.Validate(model).Errors.Select(failure => failure.PropertyName)];

    [Fact]
    public void A_version_number_and_a_date_are_all_that_is_required()
    {
        // Everything else about a test is optional: a creator who cooked it and has nothing to add beyond
        // "I made this" has recorded something worth keeping.
        Assert.Empty(ErrorsFor(Valid()));
    }

    [Fact]
    public void A_test_must_say_which_version_was_cooked()
    {
        // The difference from the duplicate route, where omitting it means "as it currently stands". A test
        // against an unnamed version is evidence about nothing in particular.
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.SourceVersionNumber),
            ErrorsFor(Valid() with { SourceVersionNumber = null }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_version_number_below_the_first_is_refused(int versionNumber) =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.SourceVersionNumber),
            ErrorsFor(Valid() with { SourceVersionNumber = versionNumber }));

    [Fact]
    public void A_test_must_say_when_it_was_run() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.TestedAt),
            ErrorsFor(Valid() with { TestedAt = null }));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void A_rating_outside_the_scale_is_refused(int rating) =>
        Assert.Contains(nameof(CreateRecipeTestRunViewModel.Rating), ErrorsFor(Valid() with { Rating = rating }));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Both_ends_of_the_scale_are_accepted(int rating) =>
        Assert.Empty(ErrorsFor(Valid() with { Rating = rating }));

    [Fact]
    public void An_actual_yield_of_zero_is_refused() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.ActualYieldQuantity),
            ErrorsFor(Valid() with { ActualYieldQuantity = 0m }));

    [Fact]
    public void A_yield_unit_with_nothing_to_measure_is_refused() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.ActualYieldUnitId),
            ErrorsFor(Valid() with { ActualYieldUnitId = Guid.NewGuid() }));

    [Fact]
    public void A_yield_with_no_unit_is_accepted() =>
        // The reverse of the rule above, and what a tester actually writes: "got 10, not 12" needs no unit.
        Assert.Empty(ErrorsFor(Valid() with { ActualYieldText = "got 10, not 12", ActualYieldQuantity = 10m }));

    [Fact]
    public void An_empty_unit_reference_is_refused() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.ActualYieldUnitId),
            ErrorsFor(Valid() with { ActualYieldUnitId = Guid.Empty, ActualYieldQuantity = 10m }));

    [Fact]
    public void A_time_beyond_a_year_is_refused() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.ActualCookTimeMinutes),
            ErrorsFor(Valid() with { ActualCookTimeMinutes = RecipePolicy.MaxTimeMinutes + 1 }));

    [Fact]
    public void A_negative_time_is_refused() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.ActualPrepTimeMinutes),
            ErrorsFor(Valid() with { ActualPrepTimeMinutes = -1 }));

    [Fact]
    public void A_total_below_the_sum_of_its_parts_is_accepted() =>
        // Nothing sums them, here or anywhere: a kitchen that overlaps prep with cooking produces exactly this.
        Assert.Empty(ErrorsFor(Valid() with
        {
            ActualPrepTimeMinutes = 30,
            ActualCookTimeMinutes = 40,
            ActualTotalTimeMinutes = 50,
        }));

    [Fact]
    public void A_blank_observation_is_refused_at_its_own_position()
    {
        var errors = ErrorsFor(Valid() with
        {
            Observations =
            [
                new TestObservationInputViewModel { Text = "The crumb was close." },
                new TestObservationInputViewModel { Text = "   " },
            ],
        });

        // The position matters more than the rule: "An observation cannot be blank." about a test with a dozen
        // notes is a sentence no creator can act on.
        Assert.Contains("Observations[1].text", errors);
        Assert.DoesNotContain("Observations[0].text", errors);
    }

    [Fact]
    public void An_over_long_observation_is_refused_at_its_own_position() =>
        Assert.Contains(
            "Observations[0].text",
            ErrorsFor(Valid() with
            {
                Observations = [new TestObservationInputViewModel
                {
                    Text = new string('x', TestRunPolicy.ObservationTextMaxLength + 1),
                }],
            }));

    [Fact]
    public void An_issue_must_state_its_severity() =>
        // No default to fall back on: CK_TestIssues_Severity_Specified refuses zero at the database, because a
        // missing severity landing on Minor would under-report a blocking problem.
        Assert.Contains(
            "Issues[0].severity",
            ErrorsFor(Valid() with
            {
                Issues = [new TestIssueInputViewModel { Title = "Crumb too dense" }],
            }));

    [Fact]
    public void An_issue_must_have_a_title() =>
        Assert.Contains(
            "Issues[0].title",
            ErrorsFor(Valid() with
            {
                Issues = [new TestIssueInputViewModel { Severity = TestIssueSeverity.Major }],
            }));

    [Fact]
    public void An_issue_may_point_at_one_of_this_requests_observations() =>
        Assert.Empty(ErrorsFor(Valid() with
        {
            Observations = [new TestObservationInputViewModel { Text = "The crumb was close." }],
            Issues =
            [
                new TestIssueInputViewModel
                {
                    Severity = TestIssueSeverity.Major,
                    Title = "Crumb too dense",
                    ObservationIndex = 0,
                },
            ],
        }));

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void An_issue_pointing_past_the_observations_is_refused(int observationIndex) =>
        // An index the write could not resolve would file the issue against nothing, silently.
        Assert.Contains(
            "Issues[0].observationIndex",
            ErrorsFor(Valid() with
            {
                Observations = [new TestObservationInputViewModel { Text = "The crumb was close." }],
                Issues =
                [
                    new TestIssueInputViewModel
                    {
                        Severity = TestIssueSeverity.Major,
                        Title = "Crumb too dense",
                        ObservationIndex = observationIndex,
                    },
                ],
            }));

    [Fact]
    public void An_issue_pointing_at_an_observation_when_none_were_sent_is_refused() =>
        Assert.Contains(
            "Issues[0].observationIndex",
            ErrorsFor(Valid() with
            {
                Issues =
                [
                    new TestIssueInputViewModel
                    {
                        Severity = TestIssueSeverity.Major,
                        Title = "Crumb too dense",
                        ObservationIndex = 0,
                    },
                ],
            }));

    [Fact]
    public void More_observations_than_one_test_may_carry_is_refused_on_the_list() =>
        // On the list's own key rather than at a position: a total is not about any one note.
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.Observations),
            ErrorsFor(Valid() with
            {
                Observations =
                [
                    .. Enumerable
                        .Range(0, TestRunPolicy.MaxObservationsPerRun + 1)
                        .Select(index => new TestObservationInputViewModel { Text = $"note {index}" }),
                ],
            }));

    [Fact]
    public void A_null_entry_in_a_list_is_refused_on_the_list() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.Issues),
            ErrorsFor(Valid() with { Issues = [null] }));

    [Fact]
    public void An_unknown_severity_is_refused() =>
        Assert.Contains(
            "Issues[0].severity",
            ErrorsFor(Valid() with
            {
                Issues =
                [
                    new TestIssueInputViewModel
                    {
                        Severity = (TestIssueSeverity)99,
                        Title = "Crumb too dense",
                    },
                ],
            }));

    [Fact]
    public void An_unknown_outcome_is_refused() =>
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.Outcome),
            ErrorsFor(Valid() with { Outcome = (TestRunOutcome)99 }));
}
