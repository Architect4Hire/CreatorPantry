using System.Reflection;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The validation matrix for <see cref="CreateRecipeViewModel"/>, boundary by boundary.
/// </summary>
/// <remarks>
/// Shape and format only. Whether a cuisine id names a row that exists, whether a yield unit is a
/// temperature, and whether a stated total time is coherent with its parts are all domain questions that
/// belong to Business — asserting them here would mean asserting behaviour this type does not have.
/// </remarks>
public sealed class CreateRecipeViewModelValidatorTests
{
    private readonly CreateRecipeViewModelValidator _validator = new();

    private static CreateRecipeViewModel Valid(Action<CreateRecipeViewModel>? _ = null) =>
        new() { Title = "Olive oil cake" };

    [Fact]
    public void A_title_alone_is_a_valid_recipe() =>
        Assert.True(_validator.Validate(Valid()).IsValid);

    [Fact]
    public void A_fully_populated_request_is_valid()
    {
        var model = new CreateRecipeViewModel
        {
            Title = "Olive oil cake",
            Description = "A plain cake.",
            Headnote = "The one my grandmother made.",
            Notes = "Halve the sugar if the plums are ripe.",
            StorageNotes = "Keeps three days under a cloth.",
            AttributionText = "Adapted from my grandmother's card.",
            SourceUrl = "https://example.com/olive-oil-cake",
            CuisineId = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            PrimaryTechniqueId = Guid.NewGuid(),
            PrepTimeMinutes = 20,
            CookTimeMinutes = 25,
            RestTimeMinutes = 60,
            TotalTimeMinutes = 75,
            YieldText = "makes 12 muffins",
            YieldQuantity = 12m,
            YieldUnitId = Guid.NewGuid(),
            Tags = ["Weeknight", "Freezer friendly"],
            Status = SettableRecipeStatusViewModel.Ready,
        };

        Assert.True(_validator.Validate(model).IsValid);
    }

    // ---- Title ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_missing_or_blank_title_is_invalid(string? title) =>
        Assert.False(_validator.Validate(new CreateRecipeViewModel { Title = title }).IsValid);

    [Fact]
    public void A_title_at_the_limit_is_valid() =>
        Assert.True(_validator.Validate(
            new CreateRecipeViewModel { Title = new string('a', RecipePolicy.TitleMaxLength) }).IsValid);

    [Fact]
    public void A_title_over_the_limit_is_invalid() =>
        Assert.False(_validator.Validate(
            new CreateRecipeViewModel { Title = new string('a', RecipePolicy.TitleMaxLength + 1) }).IsValid);

    [Fact]
    public void A_title_that_is_only_long_because_of_padding_is_valid() =>
        // Measured after trimming, so a client that pads a field does not get a length error about
        // characters the creator never typed.
        Assert.True(_validator.Validate(new CreateRecipeViewModel
        {
            Title = "  " + new string('a', RecipePolicy.TitleMaxLength) + "  ",
        }).IsValid);

    // ---- Long text ----

    [Theory]
    [InlineData(nameof(CreateRecipeViewModel.Description), RecipePolicy.DescriptionMaxLength)]
    [InlineData(nameof(CreateRecipeViewModel.Headnote), RecipePolicy.LongTextMaxLength)]
    [InlineData(nameof(CreateRecipeViewModel.Notes), RecipePolicy.LongTextMaxLength)]
    [InlineData(nameof(CreateRecipeViewModel.StorageNotes), RecipePolicy.LongTextMaxLength)]
    [InlineData(nameof(CreateRecipeViewModel.AttributionText), RecipePolicy.AttributionMaxLength)]
    [InlineData(nameof(CreateRecipeViewModel.YieldText), RecipePolicy.YieldTextMaxLength)]
    public void Optional_text_is_valid_at_its_limit_and_invalid_past_it(string property, int limit)
    {
        Assert.True(_validator.Validate(WithText(property, new string('a', limit))).IsValid, $"{property} at limit");
        Assert.False(_validator.Validate(WithText(property, new string('a', limit + 1))).IsValid, $"{property} past limit");
        Assert.True(_validator.Validate(WithText(property, null)).IsValid, $"{property} omitted");
    }

    // ---- SourceUrl ----

    [Theory]
    [InlineData("https://example.com/recipe")]
    [InlineData("http://example.com")]
    public void A_web_address_is_valid(string url) =>
        Assert.True(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", SourceUrl = url }).IsValid);

    [Theory]
    [InlineData("/recipes/cake")]
    [InlineData("example.com/cake")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    public void A_non_web_address_is_invalid(string url) =>
        // Rejected at the edge so a scheme that becomes dangerous when rendered as a link is never stored in
        // the first place.
        Assert.False(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", SourceUrl = url }).IsValid);

    // ---- Times ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(525_600)]
    public void A_plausible_time_is_valid(int minutes) =>
        Assert.True(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", PrepTimeMinutes = minutes }).IsValid);

    [Theory]
    [InlineData(-1)]
    [InlineData(525_601)]
    public void A_negative_or_absurd_time_is_invalid(int minutes) =>
        Assert.False(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", CookTimeMinutes = minutes }).IsValid);

    [Fact]
    public void A_total_time_smaller_than_its_parts_is_accepted_here()
    {
        // Not an oversight. Prep overlaps cooking and resting is unattended, so a creator's stated total is a
        // fact in its own right; judging it is a domain question for Business, not a format one.
        var model = new CreateRecipeViewModel
        {
            Title = "Cake",
            PrepTimeMinutes = 30,
            CookTimeMinutes = 30,
            RestTimeMinutes = 30,
            TotalTimeMinutes = 45,
        };

        Assert.True(_validator.Validate(model).IsValid);
    }

    // ---- Yield ----

    [Fact]
    public void A_yield_with_no_unit_is_valid() =>
        Assert.True(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", YieldText = "makes 12", YieldQuantity = 12m }).IsValid);

    [Fact]
    public void A_unit_with_no_yield_quantity_is_invalid() =>
        // Mirrors CK_Recipes_YieldUnit_RequiresQuantity, so the failure is a readable 400 rather than a
        // database error arriving as a 500.
        Assert.False(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", YieldUnitId = Guid.NewGuid() }).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_yield_is_invalid(int quantity) =>
        Assert.False(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", YieldQuantity = quantity }).IsValid);

    // ---- References ----

    [Theory]
    [InlineData(nameof(CreateRecipeViewModel.CuisineId))]
    [InlineData(nameof(CreateRecipeViewModel.CourseId))]
    [InlineData(nameof(CreateRecipeViewModel.PrimaryTechniqueId))]
    public void An_empty_guid_reference_is_invalid(string property) =>
        Assert.False(_validator.Validate(WithGuid(property, Guid.Empty)).IsValid);

    [Theory]
    [InlineData(nameof(CreateRecipeViewModel.CuisineId))]
    [InlineData(nameof(CreateRecipeViewModel.CourseId))]
    [InlineData(nameof(CreateRecipeViewModel.PrimaryTechniqueId))]
    public void An_omitted_reference_is_valid(string property) =>
        Assert.True(_validator.Validate(WithGuid(property, null)).IsValid);

    // ---- Status ----

    [Theory]
    [InlineData(SettableRecipeStatusViewModel.Draft)]
    [InlineData(SettableRecipeStatusViewModel.Ready)]
    public void A_recipe_may_start_as_draft_or_ready(SettableRecipeStatusViewModel status) =>
        Assert.True(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", Status = status }).IsValid);

    [Fact]
    public void A_recipe_cannot_start_out_archived() =>
        Assert.False(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", Status = SettableRecipeStatusViewModel.Archived }).IsValid);

    [Fact]
    public void An_undeclared_status_value_is_invalid() =>
        // A cast accepts 99 happily. Accepting it now and refusing it later would be the breaking change
        // api-contract.md warns about, so the narrow reading ships first.
        Assert.False(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", Status = (SettableRecipeStatusViewModel)99 }).IsValid);

    [Fact]
    public void An_omitted_status_is_valid() =>
        Assert.True(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", Status = null }).IsValid);

    // ---- Tags ----

    [Fact]
    public void No_tags_is_valid()
    {
        Assert.True(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", Tags = null }).IsValid);
        Assert.True(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", Tags = [] }).IsValid);
    }

    [Fact]
    public void Tags_at_the_count_limit_are_valid() =>
        Assert.True(_validator.Validate(new CreateRecipeViewModel
        {
            Title = "Cake",
            Tags = [.. Enumerable.Range(0, TagPolicy.MaxTagsPerRecipe).Select(index => $"tag{index}")],
        }).IsValid);

    [Fact]
    public void Too_many_tags_are_invalid() =>
        Assert.False(_validator.Validate(new CreateRecipeViewModel
        {
            Title = "Cake",
            Tags = [.. Enumerable.Range(0, TagPolicy.MaxTagsPerRecipe + 1).Select(index => $"tag{index}")],
        }).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_tag_is_invalid(string? tag) =>
        Assert.False(_validator.Validate(new CreateRecipeViewModel { Title = "Cake", Tags = [tag] }).IsValid);

    [Fact]
    public void A_tag_over_the_length_limit_is_invalid() =>
        Assert.False(_validator.Validate(new CreateRecipeViewModel
        {
            Title = "Cake",
            Tags = [new string('a', TagPolicy.NameMaxLength + 1)],
        }).IsValid);

    [Theory]
    [InlineData("Weeknight", "weeknight")]
    [InlineData("Freezer Friendly", "freezer friendly")]
    [InlineData("Weeknight", "  WEEKNIGHT  ")]
    public void The_same_tag_twice_in_any_casing_is_invalid(string first, string second) =>
        // Caught here rather than at the primary key, which would refuse it with a far worse message.
        Assert.False(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", Tags = [first, second] }).IsValid);

    [Fact]
    public void Two_genuinely_different_tags_are_valid() =>
        Assert.True(_validator.Validate(
            new CreateRecipeViewModel { Title = "Cake", Tags = ["Weeknight", "Freezer friendly"] }).IsValid);

    // ---- The contract's own shape ----

    [Fact]
    public void The_request_carries_no_ownership_audit_or_version_field()
    {
        // The restriction this contract exists under, asserted rather than trusted to review. Any of these
        // appearing as a bindable property would mean a client could name its own workspace, author, or
        // version — which is the one class of field that must always be server-derived.
        var forbidden = new[]
        {
            "WorkspaceId", "OwnerId", "CreatedByMembershipId", "UpdatedByMembershipId",
            "VersionNumber", "CreatedAt", "UpdatedAt", "RowVersion",
            // Not ownership, but equally not the client's: it exists only so a composite foreign key can pin
            // a unit's dimension, and a client able to send it could contradict the unit it names.
            "YieldUnitDimension",
        };

        var present = typeof(CreateRecipeViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Intersect(forbidden)
            .ToList();

        Assert.True(present.Count == 0, $"the create contract exposes server-owned fields: {string.Join(", ", present)}");
    }

    private static CreateRecipeViewModel WithText(string property, string? value) => property switch
    {
        nameof(CreateRecipeViewModel.Description) => new() { Title = "Cake", Description = value },
        nameof(CreateRecipeViewModel.Headnote) => new() { Title = "Cake", Headnote = value },
        nameof(CreateRecipeViewModel.Notes) => new() { Title = "Cake", Notes = value },
        nameof(CreateRecipeViewModel.StorageNotes) => new() { Title = "Cake", StorageNotes = value },
        nameof(CreateRecipeViewModel.AttributionText) => new() { Title = "Cake", AttributionText = value },
        nameof(CreateRecipeViewModel.YieldText) => new() { Title = "Cake", YieldText = value },
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "unmapped property"),
    };

    private static CreateRecipeViewModel WithGuid(string property, Guid? value) => property switch
    {
        nameof(CreateRecipeViewModel.CuisineId) => new() { Title = "Cake", CuisineId = value },
        nameof(CreateRecipeViewModel.CourseId) => new() { Title = "Cake", CourseId = value },
        nameof(CreateRecipeViewModel.PrimaryTechniqueId) => new() { Title = "Cake", PrimaryTechniqueId = value },
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "unmapped property"),
    };
}
