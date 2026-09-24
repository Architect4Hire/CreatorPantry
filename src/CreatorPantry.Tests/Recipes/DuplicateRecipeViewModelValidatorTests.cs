using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The validation matrix for <see cref="DuplicateRecipeViewModel"/>, and what its canonical form makes of a
/// request.
/// </summary>
/// <remarks>
/// Two fields, so the interesting part is mostly what the body cannot carry: no content, and no concurrency
/// token. The last test pins that, because a field added here without a decision would be a way to smuggle
/// content into a copy that is supposed to come entirely from the archive.
/// </remarks>
public sealed class DuplicateRecipeViewModelValidatorTests
{
    private readonly DuplicateRecipeViewModelValidator _validator = new();

    private IReadOnlyList<string> ErrorsFor(DuplicateRecipeViewModel model, string property) =>
    [
        .. _validator.Validate(model).Errors
            .Where(failure => failure.PropertyName == property)
            .Select(failure => failure.ErrorMessage)
    ];

    [Fact]
    public void A_title_alone_is_a_valid_duplicate() =>
        Assert.True(_validator.Validate(new DuplicateRecipeViewModel { Title = "Olive oil and rosemary cake" }).IsValid);

    [Fact]
    public void A_title_and_a_source_version_are_valid() =>
        Assert.True(_validator.Validate(new DuplicateRecipeViewModel
        {
            Title = "Olive oil and rosemary cake",
            SourceVersionNumber = 2,
        }).IsValid);

    // ---- The title ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_copy_without_a_title_is_refused(string? title) =>
        Assert.Equal(
            ["Give the copy a title."],
            ErrorsFor(new DuplicateRecipeViewModel { Title = title }, nameof(DuplicateRecipeViewModel.Title)));

    [Fact]
    public void A_title_at_the_limit_is_accepted() =>
        Assert.True(_validator.Validate(new DuplicateRecipeViewModel
        {
            Title = new string('x', RecipePolicy.TitleMaxLength),
        }).IsValid);

    [Fact]
    public void A_title_past_the_limit_is_refused() =>
        Assert.Single(ErrorsFor(
            new DuplicateRecipeViewModel { Title = new string('x', RecipePolicy.TitleMaxLength + 1) },
            nameof(DuplicateRecipeViewModel.Title)));

    /// <summary>
    /// Measured after trimming, so surrounding whitespace cannot push an acceptable title over the limit —
    /// and cannot be used to smuggle a longer one under it, because the canonical form trims too.
    /// </summary>
    [Fact]
    public void A_title_is_measured_trimmed() =>
        Assert.True(_validator.Validate(new DuplicateRecipeViewModel
        {
            Title = "   " + new string('x', RecipePolicy.TitleMaxLength) + "   ",
        }).IsValid);

    /// <summary>One message, not two: a blank title is not also reported as being too short.</summary>
    [Fact]
    public void A_missing_title_is_reported_once() =>
        Assert.Single(ErrorsFor(
            new DuplicateRecipeViewModel { Title = null }, nameof(DuplicateRecipeViewModel.Title)));

    // ---- The source version ----

    [Fact]
    public void An_absent_source_version_is_not_validated() =>
        Assert.Empty(ErrorsFor(
            new DuplicateRecipeViewModel { Title = "Copy" },
            nameof(DuplicateRecipeViewModel.SourceVersionNumber)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_source_version_below_one_is_refused(int versionNumber) =>
        Assert.Equal(
            ["A version number starts at 1."],
            ErrorsFor(
                new DuplicateRecipeViewModel { Title = "Copy", SourceVersionNumber = versionNumber },
                nameof(DuplicateRecipeViewModel.SourceVersionNumber)));

    [Fact]
    public void Version_one_is_accepted() =>
        Assert.True(_validator.Validate(new DuplicateRecipeViewModel
        {
            Title = "Copy",
            SourceVersionNumber = RecipePolicy.FirstVersionNumber,
        }).IsValid);

    // ---- The canonical form ----

    [Fact]
    public void A_title_is_canonicalized_trimmed() =>
        Assert.Equal(
            "Olive oil and rosemary cake",
            CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel
            {
                Title = "  Olive oil and rosemary cake  ",
            }).Title);

    /// <summary>
    /// <c>null</c> survives rather than being resolved to a number: resolving needs a read the facade does
    /// not make, and the distinction matters to the fingerprint.
    /// </summary>
    [Fact]
    public void An_absent_source_version_stays_absent() =>
        Assert.Null(CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel { Title = "Copy" }).SourceVersionNumber);

    [Fact]
    public void The_fingerprint_distinguishes_the_source_recipe()
    {
        var canonical = CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel { Title = "Copy" });

        Assert.NotEqual(canonical.Fingerprint(Guid.NewGuid()), canonical.Fingerprint(Guid.NewGuid()));
    }

    [Fact]
    public void The_fingerprint_distinguishes_the_title()
    {
        var recipeId = Guid.NewGuid();

        Assert.NotEqual(
            CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel { Title = "One copy" }).Fingerprint(recipeId),
            CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel { Title = "Another copy" }).Fingerprint(recipeId));
    }

    [Fact]
    public void The_fingerprint_distinguishes_the_source_version()
    {
        var recipeId = Guid.NewGuid();

        Assert.NotEqual(
            CanonicalDuplicateRecipe.From(
                new DuplicateRecipeViewModel { Title = "Copy", SourceVersionNumber = 1 }).Fingerprint(recipeId),
            CanonicalDuplicateRecipe.From(
                new DuplicateRecipeViewModel { Title = "Copy", SourceVersionNumber = 2 }).Fingerprint(recipeId));
    }

    /// <summary>
    /// And "whatever is current" is not the same request as the number that happens to be current, because
    /// the first says the creator did not care which version it was.
    /// </summary>
    [Fact]
    public void The_fingerprint_distinguishes_current_from_a_named_version()
    {
        var recipeId = Guid.NewGuid();

        Assert.NotEqual(
            CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel { Title = "Copy" }).Fingerprint(recipeId),
            CanonicalDuplicateRecipe.From(
                new DuplicateRecipeViewModel { Title = "Copy", SourceVersionNumber = 1 }).Fingerprint(recipeId));
    }

    [Fact]
    public void The_same_request_fingerprints_alike()
    {
        var recipeId = Guid.NewGuid();

        DuplicateRecipeViewModel Model() => new() { Title = "Copy", SourceVersionNumber = 2 };

        Assert.Equal(
            CanonicalDuplicateRecipe.From(Model()).Fingerprint(recipeId),
            CanonicalDuplicateRecipe.From(Model()).Fingerprint(recipeId));
    }

    /// <summary>
    /// The body carries no content and no concurrency token. Reflection rather than a list, so a field added
    /// without a decision fails here instead of quietly becoming a way to write content into a copy.
    /// </summary>
    [Fact]
    public void The_body_carries_nothing_but_a_title_and_a_source_version() =>
        Assert.Equal(
            [nameof(DuplicateRecipeViewModel.SourceVersionNumber), nameof(DuplicateRecipeViewModel.Title)],
            typeof(DuplicateRecipeViewModel)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
}
