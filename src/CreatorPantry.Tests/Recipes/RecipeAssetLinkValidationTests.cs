using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The shape rules for linking a library asset to a recipe and attaching one to a test (RCPUB-005), and the
/// one domain rule the link has of its own: what becomes of a step picture when its step goes.
/// </summary>
/// <remarks>
/// The endpoint tests prove these are wired; this proves each rule on its own, at its boundary, so a rule
/// that drifts is named by the test that fails rather than by a 400 somewhere in a longer walk.
/// </remarks>
public sealed class RecipeAssetLinkValidationTests
{
    /// <summary>Eight bytes, base64 — the shape a real concurrency token has.</summary>
    private const string Token = "AQIDBAUGBwg=";

    private static readonly Guid Asset = Guid.NewGuid();

    private static LinkRecipeAssetViewModel Link(RecipeAssetRole? role = RecipeAssetRole.Gallery) => new()
    {
        MediaAssetId = Asset,
        Role = role,
        ExpectedConcurrencyToken = Token,
    };

    private static IReadOnlyList<string> FieldsRefused(LinkRecipeAssetViewModel model) =>
        [.. new LinkRecipeAssetViewModelValidator().Validate(model).Errors.Select(failure => failure.PropertyName).Distinct()];

    private static IReadOnlyList<string> FieldsRefused(AttachTestImageViewModel model) =>
        [.. new AttachTestImageViewModelValidator().Validate(model).Errors.Select(failure => failure.PropertyName).Distinct()];

    // ---- Linking ----

    [Theory]
    [InlineData(RecipeAssetRole.Hero)]
    [InlineData(RecipeAssetRole.Gallery)]
    [InlineData(RecipeAssetRole.Process)]
    [InlineData(RecipeAssetRole.Social)]
    public void A_recipe_level_role_needs_only_the_asset_the_role_and_the_token(RecipeAssetRole role) =>
        Assert.Empty(FieldsRefused(Link(role)));

    [Fact]
    public void A_step_picture_needs_its_step() =>
        Assert.Equal(
            [nameof(LinkRecipeAssetViewModel.InstructionStepId)],
            FieldsRefused(Link(RecipeAssetRole.Step)));

    [Fact]
    public void A_step_picture_with_its_step_is_accepted() =>
        Assert.Empty(FieldsRefused(Link(RecipeAssetRole.Step) with { InstructionStepId = Guid.NewGuid() }));

    [Theory]
    [InlineData(RecipeAssetRole.Hero)]
    [InlineData(RecipeAssetRole.Gallery)]
    [InlineData(RecipeAssetRole.Process)]
    [InlineData(RecipeAssetRole.Social)]
    public void Only_a_step_picture_may_name_a_step(RecipeAssetRole role) =>
        Assert.Equal(
            [nameof(LinkRecipeAssetViewModel.InstructionStepId)],
            FieldsRefused(Link(role) with { InstructionStepId = Guid.NewGuid() }));

    [Fact]
    public void The_asset_is_required_and_cannot_be_the_empty_id()
    {
        Assert.Contains(nameof(LinkRecipeAssetViewModel.MediaAssetId), FieldsRefused(Link() with { MediaAssetId = null }));
        Assert.Contains(nameof(LinkRecipeAssetViewModel.MediaAssetId), FieldsRefused(Link() with { MediaAssetId = Guid.Empty }));
    }

    [Fact]
    public void The_role_is_required_and_must_be_one_the_domain_has()
    {
        Assert.Contains(nameof(LinkRecipeAssetViewModel.Role), FieldsRefused(Link(role: null)));
        Assert.Contains(nameof(LinkRecipeAssetViewModel.Role), FieldsRefused(Link((RecipeAssetRole)99)));
        Assert.Contains(nameof(LinkRecipeAssetViewModel.Role), FieldsRefused(Link((RecipeAssetRole)0)));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(250, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void A_pinned_version_starts_at_one(int version, bool accepted) =>
        Assert.Equal(
            accepted,
            !FieldsRefused(Link() with { VersionNumber = version }).Contains(nameof(LinkRecipeAssetViewModel.VersionNumber)));

    [Fact]
    public void Leaving_the_version_out_is_how_a_link_follows_the_current_one() =>
        Assert.Empty(FieldsRefused(Link() with { VersionNumber = null }));

    [Fact]
    public void A_caption_may_run_to_the_limit_and_no_further()
    {
        Assert.Empty(FieldsRefused(Link() with { Caption = new string('x', RecipePolicy.CaptionMaxLength) }));
        Assert.Equal(
            [nameof(LinkRecipeAssetViewModel.Caption)],
            FieldsRefused(Link() with { Caption = new string('x', RecipePolicy.CaptionMaxLength + 1) }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("AQID")]
    public void A_link_needs_a_well_formed_token(string? token) =>
        Assert.Equal(
            [nameof(LinkRecipeAssetViewModel.ExpectedConcurrencyToken)],
            FieldsRefused(Link() with { ExpectedConcurrencyToken = token }));

    [Theory]
    [InlineData(Token, true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("AQID", false)]
    public void An_unlink_carries_the_token_and_nothing_else(string? token, bool accepted) =>
        Assert.Equal(
            accepted,
            new UnlinkRecipeAssetViewModelValidator().Validate(new UnlinkRecipeAssetViewModel { ExpectedConcurrencyToken = token }).IsValid);

    /// <summary>
    /// Structural rather than checked: there is no property through which a link request could say which
    /// workspace it is for (tenancy.md).
    /// </summary>
    [Fact]
    public void No_request_in_this_feature_can_carry_a_workspace()
    {
        foreach (var type in (Type[])[typeof(LinkRecipeAssetViewModel), typeof(UnlinkRecipeAssetViewModel), typeof(AttachTestImageViewModel)])
        {
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Workspace", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- Attaching to a test ----

    [Fact]
    public void An_attachment_needs_only_the_asset() =>
        Assert.Empty(FieldsRefused(new AttachTestImageViewModel { MediaAssetId = Asset }));

    [Fact]
    public void An_attachment_refuses_a_missing_asset_a_version_below_one_and_an_overlong_caption()
    {
        Assert.Equal(
            (string[])[nameof(AttachTestImageViewModel.Caption), nameof(AttachTestImageViewModel.MediaAssetId), nameof(AttachTestImageViewModel.VersionNumber)],
            FieldsRefused(new AttachTestImageViewModel
            {
                MediaAssetId = Guid.Empty,
                VersionNumber = 0,
                Caption = new string('x', RecipePolicy.CaptionMaxLength + 1),
            }).Order());
    }

    [Fact]
    public void An_attachment_may_pin_a_version_and_name_an_issue() =>
        Assert.Empty(FieldsRefused(new AttachTestImageViewModel
        {
            MediaAssetId = Asset,
            VersionNumber = 3,
            TestIssueId = Guid.NewGuid(),
            Caption = "The dense crumb.",
        }));

    // ---- What becomes of a step picture when its step goes ----

    private static RecipeAssetLink StepImage(Guid stepId, int order) => new()
    {
        Id = Guid.NewGuid(),
        MediaAssetId = Guid.NewGuid(),
        MediaAssetVersionNumber = 2,
        InstructionStepId = stepId,
        Role = RecipeAssetRole.Step,
        SortOrder = order,
        Caption = "Folding in.",
    };

    [Fact]
    public void Removing_a_step_demotes_its_pictures_and_no_one_elses()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var recipe = new Recipe { Id = Guid.NewGuid(), Title = "Cake" };
        var ofGone = StepImage(gone, 0);
        var alsoOfGone = StepImage(gone, 1);
        var ofKept = StepImage(kept, 2);
        var hero = new RecipeAssetLink { Id = Guid.NewGuid(), MediaAssetId = Guid.NewGuid(), Role = RecipeAssetRole.Hero, SortOrder = 3 };
        recipe.AssetLinks.Add(ofGone);
        recipe.AssetLinks.Add(alsoOfGone);
        recipe.AssetLinks.Add(ofKept);
        recipe.AssetLinks.Add(hero);

        var changed = RecipeAssetLinkRules.DemoteStepImages(recipe, [gone]);

        Assert.True(changed);

        foreach (var link in (RecipeAssetLink[])[ofGone, alsoOfGone])
        {
            Assert.Equal(RecipeAssetRole.Process, link.Role);
            Assert.Null(link.InstructionStepId);
        }

        // The picture is kept whole: the same link, where it was, with its words and its pin.
        Assert.Equal(4, recipe.AssetLinks.Count);
        Assert.Equal(0, ofGone.SortOrder);
        Assert.Equal("Folding in.", ofGone.Caption);
        Assert.Equal(2, ofGone.MediaAssetVersionNumber);

        Assert.Equal(RecipeAssetRole.Step, ofKept.Role);
        Assert.Equal(kept, ofKept.InstructionStepId);
        Assert.Equal(RecipeAssetRole.Hero, hero.Role);
    }

    [Fact]
    public void Removing_a_step_with_no_picture_changes_nothing_and_says_so()
    {
        var recipe = new Recipe { Id = Guid.NewGuid(), Title = "Cake" };
        var other = StepImage(Guid.NewGuid(), 0);
        recipe.AssetLinks.Add(other);

        Assert.False(RecipeAssetLinkRules.DemoteStepImages(recipe, [Guid.NewGuid()]));
        Assert.False(RecipeAssetLinkRules.DemoteStepImages(recipe, []));
        Assert.Equal(RecipeAssetRole.Step, other.Role);
    }

    /// <summary>
    /// The pairing the database also checks: a link is a step picture exactly when it names a step. Demotion
    /// has to move both halves together or the row it leaves would be refused on save.
    /// </summary>
    [Fact]
    public void A_demoted_link_satisfies_the_pairing_the_table_enforces()
    {
        var link = StepImage(Guid.NewGuid(), 0);

        RecipeAssetLinkRules.Demote(link);

        Assert.Equal(link.Role == RecipeAssetRole.Step, link.InstructionStepId is not null);
        Assert.Equal(RecipeAssetRole.Process, link.Role);
    }
}
