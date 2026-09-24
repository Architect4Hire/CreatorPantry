using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The validation matrix for <see cref="RestoreRecipeVersionViewModel"/>.
/// </summary>
/// <remarks>
/// Short, because the body is: a restore submits no content, so the only shape rules are the token's and the
/// reason's length. What this file mostly documents is the <em>absence</em> — there is no field here through
/// which a restore could change a recipe's content, which is what makes "a restore takes everything from the
/// archive" structural rather than a rule Business has to enforce.
/// </remarks>
public sealed class RestoreRecipeVersionViewModelValidatorTests
{
    private readonly RestoreRecipeVersionViewModelValidator _validator = new();

    /// <summary>Eight bytes, base64 — the shape a real concurrency token has.</summary>
    private const string Token = "AQIDBAUGBwg=";

    private IReadOnlyList<string> ErrorsFor(RestoreRecipeVersionViewModel model, string property) =>
    [
        .. _validator.Validate(model).Errors
            .Where(failure => failure.PropertyName == property)
            .Select(failure => failure.ErrorMessage)
    ];

    [Fact]
    public void A_token_alone_is_a_valid_restore() =>
        Assert.True(_validator.Validate(new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = Token }).IsValid);

    [Fact]
    public void A_token_and_a_reason_are_valid() =>
        Assert.True(_validator.Validate(new RestoreRecipeVersionViewModel
        {
            ExpectedConcurrencyToken = Token,
            Reason = "Tuesday's edit broke the bake time.",
        }).IsValid);

    // ---- The token ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_restore_without_a_token_is_refused(string? token) =>
        Assert.Equal(
            ["Send the recipe's concurrency token with your restore."],
            ErrorsFor(
                new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = token },
                nameof(RestoreRecipeVersionViewModel.ExpectedConcurrencyToken)));

    /// <summary>
    /// A token that could never have been issued is a client bug, and is reported as one rather than left to
    /// arrive at Business and come back as a conflict — which would send a creator looking for a collaborator
    /// who was never there.
    /// </summary>
    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("AQID")]
    [InlineData("AQIDBAUGBwgJ")]
    public void A_token_of_the_wrong_shape_is_refused(string token) =>
        Assert.Equal(
            ["That is not a concurrency token this API issued."],
            ErrorsFor(
                new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = token },
                nameof(RestoreRecipeVersionViewModel.ExpectedConcurrencyToken)));

    /// <summary>
    /// One message, not two. The token rules cascade, so a missing token is not also reported as malformed.
    /// </summary>
    [Fact]
    public void A_missing_token_is_reported_once() =>
        Assert.Single(ErrorsFor(
            new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = null },
            nameof(RestoreRecipeVersionViewModel.ExpectedConcurrencyToken)));

    // ---- The reason ----

    /// <summary>
    /// Surrounding whitespace is <em>not</em> refused, because <c>Convert.TryFromBase64String</c> ignores it
    /// and the value still decodes to the eight bytes the recipe's token is. Pinned rather than left
    /// unstated: this is shared behaviour with the edit route, and a reader looking at the rule above would
    /// otherwise reasonably assume the opposite.
    /// </summary>
    [Fact]
    public void A_token_padded_with_whitespace_still_decodes_and_is_accepted() =>
        Assert.True(_validator.Validate(
            new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = " AQIDBAUGBwg= " }).IsValid);

    [Fact]
    public void A_reason_at_the_limit_is_accepted() =>
        Assert.True(_validator.Validate(new RestoreRecipeVersionViewModel
        {
            ExpectedConcurrencyToken = Token,
            Reason = new string('x', RecipePolicy.NoteMaxLength),
        }).IsValid);

    [Fact]
    public void A_reason_past_the_limit_is_refused() =>
        Assert.Single(ErrorsFor(
            new RestoreRecipeVersionViewModel
            {
                ExpectedConcurrencyToken = Token,
                Reason = new string('x', RecipePolicy.NoteMaxLength + 1),
            },
            nameof(RestoreRecipeVersionViewModel.Reason)));

    /// <summary>
    /// Measured after trimming, so surrounding whitespace cannot push an acceptable reason over the limit —
    /// and cannot be used to smuggle a longer one under it either, because the canonical form trims too.
    /// </summary>
    [Fact]
    public void A_reason_is_measured_trimmed() =>
        Assert.True(_validator.Validate(new RestoreRecipeVersionViewModel
        {
            ExpectedConcurrencyToken = Token,
            Reason = "   " + new string('x', RecipePolicy.NoteMaxLength) + "   ",
        }).IsValid);

    // ---- The canonical form ----

    [Fact]
    public void A_blank_reason_canonicalizes_to_none() =>
        Assert.Null(CanonicalRestoreRecipeVersion.From(new RestoreRecipeVersionViewModel
        {
            ExpectedConcurrencyToken = Token,
            Reason = "   ",
        }).Reason);

    [Fact]
    public void A_reason_is_canonicalized_trimmed() =>
        Assert.Equal(
            "Tuesday's edit broke the bake time.",
            CanonicalRestoreRecipeVersion.From(new RestoreRecipeVersionViewModel
            {
                ExpectedConcurrencyToken = Token,
                Reason = "  Tuesday's edit broke the bake time.  ",
            }).Reason);

    /// <summary>
    /// Both route values are in the fingerprint. Without the version number, one key would replay "put it
    /// back to 3" as the answer to "put it back to 5" — the failure an idempotency key exists to prevent.
    /// </summary>
    [Fact]
    public void The_fingerprint_distinguishes_the_version_asked_for()
    {
        var canonical = CanonicalRestoreRecipeVersion.From(
            new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = Token });
        var recipeId = Guid.NewGuid();

        Assert.NotEqual(canonical.Fingerprint(recipeId, 3), canonical.Fingerprint(recipeId, 5));
    }

    [Fact]
    public void The_fingerprint_distinguishes_the_recipe()
    {
        var canonical = CanonicalRestoreRecipeVersion.From(
            new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = Token });

        Assert.NotEqual(canonical.Fingerprint(Guid.NewGuid(), 3), canonical.Fingerprint(Guid.NewGuid(), 3));
    }

    /// <summary>
    /// A caller who re-read the recipe and re-sent the same key is asking for a different restore, and should
    /// be told the key was reused rather than handed the earlier answer.
    /// </summary>
    [Fact]
    public void The_fingerprint_distinguishes_the_state_the_restore_was_composed_against()
    {
        var recipeId = Guid.NewGuid();

        var first = CanonicalRestoreRecipeVersion
            .From(new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = Token })
            .Fingerprint(recipeId, 3);
        var second = CanonicalRestoreRecipeVersion
            .From(new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = "CAcGBQQDAgE=" })
            .Fingerprint(recipeId, 3);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// The same request twice is the same restore. Stated because the fingerprint is an anonymous object, and
    /// two of those compare by value only because the compiler generates the equality — a hand-written type
    /// here would have had to remember to.
    /// </summary>
    [Fact]
    public void The_same_request_fingerprints_alike()
    {
        var recipeId = Guid.NewGuid();

        RestoreRecipeVersionViewModel Model() => new()
        {
            ExpectedConcurrencyToken = Token,
            Reason = "Tuesday's edit broke the bake time.",
        };

        Assert.Equal(
            CanonicalRestoreRecipeVersion.From(Model()).Fingerprint(recipeId, 3),
            CanonicalRestoreRecipeVersion.From(Model()).Fingerprint(recipeId, 3));
    }

    /// <summary>
    /// There is no content field to submit. Reflection rather than a list, so a field added to the view model
    /// without a decision about it fails here instead of quietly becoming restorable input.
    /// </summary>
    [Fact]
    public void The_body_carries_nothing_but_a_token_and_a_reason() =>
        Assert.Equal(
            [nameof(RestoreRecipeVersionViewModel.ExpectedConcurrencyToken), nameof(RestoreRecipeVersionViewModel.Reason)],
            typeof(RestoreRecipeVersionViewModel)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
}
