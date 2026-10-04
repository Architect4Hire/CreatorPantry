using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The shape rules for a week replace: what a client gets told before anything reaches the database.
/// </summary>
public sealed class WeeklyThemeValidatorTests
{
    private static readonly ReplaceWeeklyThemesViewModelValidator Validator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WeeklyThemeInput Monday(
        string? key = "meat-free-monday", string? displayName = "Meat-free Monday", DayOfWeek? day = DayOfWeek.Monday) =>
        new() { Day = day, Key = key, DisplayName = displayName };

    private static async Task<IReadOnlyList<string>> FailuresFor(params WeeklyThemeInput?[] themes)
    {
        var result = await Validator.ValidateAsync(new ReplaceWeeklyThemesViewModel { Themes = themes }, Ct);

        return [.. result.Errors.Select(failure => failure.PropertyName)];
    }

    [Fact]
    public async Task A_week_of_seven_named_days_is_accepted()
    {
        var themes = Enum.GetValues<DayOfWeek>()
            .Select(day => new WeeklyThemeInput
            {
                Day = day,
                Key = day.ToString().ToLowerInvariant(),
                DisplayName = $"{day} theme",
                Description = "A day with a point of view.",
            })
            .ToArray();

        Assert.Empty(await FailuresFor(themes));
    }

    [Fact]
    public async Task An_empty_week_is_accepted()
    {
        // How a creator clears every day, and the state every workspace starts in.
        Assert.True((await Validator.ValidateAsync(new ReplaceWeeklyThemesViewModel { Themes = [] }, Ct)).IsValid);
        Assert.True((await Validator.ValidateAsync(new ReplaceWeeklyThemesViewModel(), Ct)).IsValid);
    }

    [Fact]
    public async Task An_eighth_theme_is_refused_before_the_entries_are_read()
    {
        var themes = Enumerable.Range(0, 8)
            .Select(index => new WeeklyThemeInput { Day = DayOfWeek.Monday, Key = $"t{index}", DisplayName = $"T{index}" })
            .ToArray();

        Assert.Equal(["Themes"], await FailuresFor(themes));
    }

    [Fact]
    public async Task A_day_may_hold_one_theme()
    {
        Assert.Equal(
            ["Themes[1].Day"],
            await FailuresFor(Monday(), Monday(key: "pasta-monday", displayName: "Pasta Monday")));
    }

    [Fact]
    public async Task A_theme_may_be_listed_once()
    {
        Assert.Equal(
            ["Themes[1].Key"],
            await FailuresFor(Monday(), Monday(day: DayOfWeek.Tuesday, displayName: "Meat-free Tuesday")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Meat-Free-Monday")]
    [InlineData("meat free monday")]
    [InlineData("meat_free_monday")]
    [InlineData("-meat-free-monday")]
    [InlineData("meat.free.monday")]
    public async Task A_key_that_is_not_a_lowercase_slug_is_refused(string? key) =>
        Assert.Equal(["Themes[0].Key"], await FailuresFor(Monday(key: key)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_theme_needs_a_name(string? displayName) =>
        Assert.Equal(["Themes[0].DisplayName"], await FailuresFor(Monday(displayName: displayName)));

    [Fact]
    public async Task A_missing_or_unknown_day_is_refused()
    {
        // Day is nullable for this reason: DayOfWeek's own default is Sunday, so an omitted day would otherwise
        // arrive looking like a real answer.
        Assert.Equal(["Themes[0].Day"], await FailuresFor(Monday(day: null)));
        Assert.Equal(["Themes[0].Day"], await FailuresFor(Monday(day: (DayOfWeek)9)));
    }

    [Fact]
    public async Task An_entry_that_is_not_there_at_all_is_refused_rather_than_skipped()
    {
        var failures = await FailuresFor(Monday(), null);

        Assert.Equal(["Themes[1].Day", "Themes[1].Key", "Themes[1].DisplayName"], failures);
    }

    [Fact]
    public async Task Over_long_text_is_refused()
    {
        var failures = await FailuresFor(new WeeklyThemeInput
        {
            Day = DayOfWeek.Monday,
            Key = new string('k', WeeklyThemePolicy.KeyMaxLength + 1),
            DisplayName = new string('n', WeeklyThemePolicy.DisplayNameMaxLength + 1),
            Description = new string('d', WeeklyThemePolicy.DescriptionMaxLength + 1),
        });

        Assert.Equal(["Themes[0].Key", "Themes[0].DisplayName", "Themes[0].Description"], failures);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_not_an_error()
    {
        // Trimmed, not refused: the creator's words are what they typed, minus the accident at either end.
        Assert.Empty(await FailuresFor(new WeeklyThemeInput
        {
            Day = DayOfWeek.Monday,
            Key = "  meat-free-monday  ",
            DisplayName = "  Meat-free Monday  ",
            Description = "   ",
        }));
    }

    [Fact]
    public async Task A_creator_name_is_never_policed_for_content()
    {
        // The server stores what the creator wrote. "Meatless Monday" is a registered mark, which is a reason
        // not to *suggest* it, not a reason to refuse a creator who types it.
        Assert.Empty(await FailuresFor(Monday(key: "meatless-monday", displayName: "Meatless Monday")));
    }
}
