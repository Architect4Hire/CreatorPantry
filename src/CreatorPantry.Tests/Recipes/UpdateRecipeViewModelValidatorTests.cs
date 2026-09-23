using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The validation matrix for <see cref="UpdateRecipeViewModel"/>, boundary by boundary.
/// </summary>
/// <remarks>
/// Shape and format only, as on a create — but with one rule of its own that is the whole point of the
/// type: <strong>a field the request did not submit is never validated.</strong> Most of what follows is
/// that claim, checked field by field, because the failure it guards against is silent and total — a limit
/// tightened after a recipe was written would otherwise make that recipe uneditable forever.
/// </remarks>
public sealed class UpdateRecipeViewModelValidatorTests
{
    private readonly UpdateRecipeViewModelValidator _validator = new();

    /// <summary>Eight bytes, base64 — the shape a real concurrency token has.</summary>
    private const string Token = "AQIDBAUGBwg=";

    private static UpdateRecipeViewModel Empty() => new() { ExpectedConcurrencyToken = Token };

    private static PatchField<T> Set<T>(T value) => PatchField<T>.Submitted(value);

    private IReadOnlyList<string> ErrorsFor(UpdateRecipeViewModel model, string property) =>
    [
        .. _validator.Validate(model).Errors
            .Where(failure => failure.PropertyName == property)
            .Select(failure => failure.ErrorMessage)
    ];

    // ---- The empty edit ----

    [Fact]
    public void An_edit_that_submits_nothing_but_a_token_is_valid() =>
        Assert.True(_validator.Validate(Empty()).IsValid);

    [Fact]
    public void A_fully_populated_edit_is_valid()
    {
        var model = new UpdateRecipeViewModel
        {
            ExpectedConcurrencyToken = Token,
            Reason = "Cut the sugar back.",
            Title = Set<string?>("Olive oil cake"),
            Description = Set<string?>("A plain cake."),
            Headnote = Set<string?>("The one my grandmother made."),
            Notes = Set<string?>("Halve the sugar if the plums are ripe."),
            StorageNotes = Set<string?>("Keeps three days under a cloth."),
            AttributionText = Set<string?>("Adapted from my grandmother's card."),
            SourceUrl = Set<string?>("https://example.com/olive-oil-cake"),
            CuisineId = Set<Guid?>(Guid.NewGuid()),
            CourseId = Set<Guid?>(Guid.NewGuid()),
            PrimaryTechniqueId = Set<Guid?>(Guid.NewGuid()),
            PrepTimeMinutes = Set<int?>(20),
            CookTimeMinutes = Set<int?>(25),
            RestTimeMinutes = Set<int?>(60),
            TotalTimeMinutes = Set<int?>(75),
            YieldText = Set<string?>("makes 12 muffins"),
            YieldQuantity = Set<decimal?>(12m),
            YieldUnitId = Set<Guid?>(Guid.NewGuid()),
            Tags = Set<IReadOnlyList<string?>?>(["Weeknight", "Freezer friendly"]),
            Status = Set<SettableRecipeStatusViewModel?>(SettableRecipeStatusViewModel.Ready),
        };

        Assert.True(_validator.Validate(model).IsValid);
    }

    /// <summary>
    /// Every clearable field, cleared at once. None of these is refused — clearing is a legitimate edit, and
    /// the two fields that cannot be cleared have tests of their own below.
    /// </summary>
    [Fact]
    public void Clearing_every_clearable_field_is_valid()
    {
        var model = Empty() with
        {
            Description = Set<string?>(null),
            Headnote = Set<string?>(null),
            Notes = Set<string?>(null),
            StorageNotes = Set<string?>(null),
            AttributionText = Set<string?>(null),
            SourceUrl = Set<string?>(null),
            CuisineId = Set<Guid?>(null),
            CourseId = Set<Guid?>(null),
            PrimaryTechniqueId = Set<Guid?>(null),
            PrepTimeMinutes = Set<int?>(null),
            CookTimeMinutes = Set<int?>(null),
            RestTimeMinutes = Set<int?>(null),
            TotalTimeMinutes = Set<int?>(null),
            YieldText = Set<string?>(null),
            YieldQuantity = Set<decimal?>(null),
            YieldUnitId = Set<Guid?>(null),
            Tags = Set<IReadOnlyList<string?>?>(null),
        };

        Assert.True(_validator.Validate(model).IsValid);
    }

    /// <summary>
    /// The claim the whole contract rests on. Every one of these values would be refused if it were
    /// submitted; none of them is, so none of them is looked at.
    /// </summary>
    [Fact]
    public void Nothing_unsubmitted_is_ever_validated() =>
        Assert.True(_validator.Validate(Empty()).IsValid);

    // ---- The concurrency token ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_edit_without_a_token_is_refused(string? token)
    {
        var errors = ErrorsFor(new UpdateRecipeViewModel { ExpectedConcurrencyToken = token }, "ExpectedConcurrencyToken");

        Assert.Equal(["Send the recipe's concurrency token with your edit."], errors);
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("AQID")]              // four bytes: too short to be a rowversion
    [InlineData("AQIDBAUGBwgJ")]      // nine bytes: too long
    [InlineData("   ")]
    public void A_token_that_could_never_have_been_issued_is_a_field_error(string token)
    {
        // Deliberately a 400 and not a 409: reporting a client bug as a conflict would send a creator
        // looking for a collaborator who does not exist.
        var errors = ErrorsFor(new UpdateRecipeViewModel { ExpectedConcurrencyToken = token }, "ExpectedConcurrencyToken");

        Assert.Single(errors);
    }

    [Fact]
    public void A_well_formed_token_that_is_simply_wrong_passes_validation()
    {
        // Whether it is *current* is not a shape question, and answering it here would need the recipe.
        Assert.True(_validator.Validate(new UpdateRecipeViewModel { ExpectedConcurrencyToken = "CAcGBQQDAgE=" }).IsValid);
    }

    // ---- Title: changeable, never clearable ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_title_cannot_be_cleared(string? title)
    {
        var errors = ErrorsFor(Empty() with { Title = Set(title) }, "Title");

        Assert.Equal(["A recipe must keep a title."], errors);
    }

    [Fact]
    public void A_title_is_bounded()
    {
        var errors = ErrorsFor(Empty() with { Title = Set<string?>(new string('x', RecipePolicy.TitleMaxLength + 1)) }, "Title");

        Assert.Single(errors);
    }

    [Fact]
    public void A_title_is_measured_after_trimming() =>
        Assert.True(_validator.Validate(
            Empty() with { Title = Set<string?>($"  {new string('x', RecipePolicy.TitleMaxLength)}  ") }).IsValid);

    // ---- Text fields ----

    [Fact]
    public void Long_text_is_bounded_when_submitted()
    {
        var model = Empty() with { Headnote = Set<string?>(new string('x', RecipePolicy.LongTextMaxLength + 1)) };

        Assert.Single(ErrorsFor(model, "Headnote"));
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com")]
    public void A_source_url_must_be_an_absolute_web_address(string url) =>
        Assert.Single(ErrorsFor(Empty() with { SourceUrl = Set<string?>(url) }, "SourceUrl"));

    [Fact]
    public void A_source_url_may_be_cleared() =>
        Assert.True(_validator.Validate(Empty() with { SourceUrl = Set<string?>(null) }).IsValid);

    // ---- Numbers ----

    [Theory]
    [InlineData(-1)]
    [InlineData(RecipePolicy.MaxTimeMinutes + 1)]
    public void A_time_outside_the_plausible_range_is_refused(int minutes) =>
        Assert.Single(ErrorsFor(Empty() with { PrepTimeMinutes = Set<int?>(minutes) }, "PrepTimeMinutes"));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_yield_must_be_greater_than_zero(int quantity) =>
        Assert.Single(ErrorsFor(Empty() with { YieldQuantity = Set<decimal?>(quantity) }, "YieldQuantity"));

    [Fact]
    public void A_yield_unit_without_a_quantity_is_not_refused_here()
    {
        // Unlike on a create. Either half of the pair may be the half that is not submitted, so the pairing
        // can only be judged against the merged recipe — which is Business's job.
        Assert.True(_validator.Validate(Empty() with { YieldUnitId = Set<Guid?>(Guid.NewGuid()) }).IsValid);
    }

    [Fact]
    public void An_empty_guid_is_not_a_reference() =>
        Assert.Single(ErrorsFor(Empty() with { CuisineId = Set<Guid?>(Guid.Empty) }, "CuisineId"));

    // ---- Status: changeable, never clearable ----

    [Fact]
    public void A_status_cannot_be_cleared()
    {
        var errors = ErrorsFor(Empty() with { Status = Set<SettableRecipeStatusViewModel?>(null) }, "Status");

        Assert.Equal(["A recipe must keep an editorial state."], errors);
    }

    [Fact]
    public void An_undefined_status_is_refused() =>
        Assert.Single(ErrorsFor(Empty() with { Status = Set<SettableRecipeStatusViewModel?>((SettableRecipeStatusViewModel)99) }, "Status"));

    [Fact]
    public void A_recipe_may_be_archived()
    {
        // The rule that differs from a create: archiving is a state a recipe is moved to, and this is the
        // route that moves it.
        var model = Empty() with { Status = Set<SettableRecipeStatusViewModel?>(SettableRecipeStatusViewModel.Archived) };

        Assert.True(_validator.Validate(model).IsValid);
    }

    // ---- Tags ----

    [Fact]
    public void Tags_are_bounded_in_number()
    {
        var tags = Enumerable.Range(0, TagPolicy.MaxTagsPerRecipe + 1).Select(index => (string?)$"tag-{index}").ToList();

        Assert.Single(ErrorsFor(Empty() with { Tags = Set<IReadOnlyList<string?>?>(tags) }, "Tags"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_tag_is_refused(string? tag) =>
        Assert.Single(ErrorsFor(Empty() with { Tags = Set<IReadOnlyList<string?>?>([tag]) }, "Tags"));

    [Fact]
    public void A_tag_is_bounded_in_length() =>
        Assert.Single(ErrorsFor(
            Empty() with { Tags = Set<IReadOnlyList<string?>?>([new string('x', TagPolicy.NameMaxLength + 1)]) },
            "Tags"));

    [Fact]
    public void The_same_tag_twice_is_refused()
    {
        // Normalized, because "Weeknight" and "weeknight" are one tag, and applying it twice is what the
        // primary key would refuse further down with a far worse message.
        var errors = ErrorsFor(Empty() with { Tags = Set<IReadOnlyList<string?>?>(["Weeknight", "weeknight"]) }, "Tags");

        Assert.Equal(["The same tag is listed more than once."], errors);
    }

    [Fact]
    public void Every_tag_may_be_removed_at_once() =>
        Assert.True(_validator.Validate(Empty() with { Tags = Set<IReadOnlyList<string?>?>([]) }).IsValid);

    // ---- Reason ----

    [Fact]
    public void A_reason_is_bounded() =>
        Assert.Single(ErrorsFor(Empty() with { Reason = new string('x', RecipePolicy.NoteMaxLength + 1) }, "Reason"));

    [Fact]
    public void A_reason_is_optional() =>
        Assert.True(_validator.Validate(Empty() with { Reason = null }).IsValid);
}
