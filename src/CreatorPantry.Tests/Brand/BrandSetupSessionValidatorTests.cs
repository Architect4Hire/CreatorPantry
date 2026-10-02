using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

public sealed class BrandSetupSessionValidatorTests
{
    private static readonly SaveBrandSetupSessionViewModelValidator Validator = new();

    private static SaveBrandSetupSessionViewModel Valid() => new()
    {
        CurrentStep = "style",
        FurthestStep = "examples",
        CompletedSteps = ["goals"],
        SkippedSteps = [],
        DraftJson = "{\"goals\":[\"warm\"]}",
    };

    private static IReadOnlyList<string> Fields(SaveBrandSetupSessionViewModel model) =>
        [.. Validator.Validate(model).Errors.Select(error => error.PropertyName)];

    [Fact]
    public void A_well_formed_session_is_valid() => Assert.True(Validator.Validate(Valid()).IsValid);

    [Fact]
    public void The_step_slugs_are_the_wizards_seven_in_order() =>
        Assert.Equal(["goals", "style", "examples", "review-text", "create", "edit", "finish"], BrandSetupSteps.All);

    [Theory]
    [InlineData("bogus")]
    [InlineData("Goals")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_current_step_is_refused(string? slug) =>
        Assert.Contains("CurrentStep", Fields(Valid() with { CurrentStep = slug }));

    [Fact]
    public void An_unknown_furthest_step_is_refused() =>
        Assert.Contains("FurthestStep", Fields(Valid() with { FurthestStep = "nope" }));

    [Fact]
    public void Furthest_cannot_come_before_current() =>
        Assert.Contains("FurthestStep", Fields(Valid() with { CurrentStep = "create", FurthestStep = "style" }));

    [Fact]
    public void Furthest_may_equal_current() =>
        Assert.True(Validator.Validate(Valid() with { CurrentStep = "style", FurthestStep = "style" }).IsValid);

    [Fact]
    public void Completed_steps_must_be_known_slugs() =>
        Assert.Contains("CompletedSteps", Fields(Valid() with { CompletedSteps = ["goals", "wat"] }));

    [Fact]
    public void Completed_steps_cannot_pass_furthest() =>
        Assert.Contains("CompletedSteps", Fields(Valid() with { CompletedSteps = ["create"] }));

    [Fact]
    public void Skipped_steps_cannot_pass_furthest() =>
        Assert.Contains("SkippedSteps", Fields(Valid() with { SkippedSteps = ["finish"] }));

    [Fact]
    public void Completed_and_skipped_must_be_disjoint() =>
        Assert.Contains("SkippedSteps", Fields(Valid() with { CompletedSteps = ["goals"], SkippedSteps = ["goals"] }));

    [Fact]
    public void A_step_cannot_repeat_within_a_set() =>
        Assert.Contains("CompletedSteps", Fields(Valid() with { CompletedSteps = ["goals", "goals"] }));

    [Fact]
    public void Missing_sets_are_refused() =>
        Assert.Equal(
            ["CompletedSteps", "SkippedSteps"],
            Fields(Valid() with { CompletedSteps = null, SkippedSteps = null }).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void The_draft_must_be_a_json_object(string? draft) =>
        Assert.Contains("DraftJson", Fields(Valid() with { DraftJson = draft }));

    [Fact]
    public void An_empty_object_is_a_valid_draft() =>
        Assert.True(Validator.Validate(Valid() with { DraftJson = "{}" }).IsValid);

    [Fact]
    public void The_draft_is_bounded_at_64_kilobytes()
    {
        var overhead = "{\"a\":\"\"}".Length;
        var atLimit = "{\"a\":\"" + new string('x', SaveBrandSetupSessionViewModelValidator.DraftMaxBytes - overhead) + "\"}";
        var over = atLimit + " ";

        Assert.Equal(SaveBrandSetupSessionViewModelValidator.DraftMaxBytes, atLimit.Length);
        Assert.True(Validator.Validate(Valid() with { DraftJson = atLimit }).IsValid);
        Assert.Contains("DraftJson", Fields(Valid() with { DraftJson = over }));
    }

    [Fact]
    public void The_bound_is_in_bytes_not_characters()
    {
        // 3-byte characters: 30000 of them is 90000 bytes but only 30000 chars.
        var draft = "{\"a\":\"" + new string('€', 30000) + "\"}";

        Assert.Contains("DraftJson", Fields(Valid() with { DraftJson = draft }));
    }
}
