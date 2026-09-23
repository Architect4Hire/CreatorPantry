using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The canonical form of a create request: what it normalizes, and — the reason it exists — which pairs of
/// differently-written requests it makes identical.
/// </summary>
/// <remarks>
/// This value is both the idempotency fingerprint and what Business maps from, so these tests are making two
/// claims at once: that a retry written slightly differently still replays, and that two requests treated as
/// the same really would have produced the same recipe.
/// </remarks>
public sealed class CanonicalCreateRecipeTests
{
    /// <summary>
    /// Compares the serialized form, because that is what is hashed — and because these records hold
    /// <c>IReadOnlyList</c> members, on which record equality falls back to reference equality and would
    /// report two identical requests as different.
    /// </summary>
    private static string Fingerprint(CreateRecipeViewModel model) =>
        JsonSerializer.Serialize(CanonicalCreateRecipe.From(model));

    // ---- Requests that mean the same thing ----

    [Fact]
    public void Tags_listed_in_another_order_are_the_same_request()
    {
        // The bug this type was introduced for. RecipeTag has no sort order, so both requests produce exactly
        // the same recipe — and hashing the raw body reported them as key reuse on a legitimate retry.
        Assert.Equal(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Tags = ["weeknight", "freezer"] }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Tags = ["freezer", "weeknight"] }));
    }

    [Fact]
    public void Padding_around_text_is_the_same_request()
    {
        Assert.Equal(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Headnote = "A note." }),
            Fingerprint(new CreateRecipeViewModel { Title = "  Cake  ", Headnote = "\tA note.\n" }));
    }

    [Fact]
    public void An_omitted_status_is_the_same_request_as_an_explicit_draft()
    {
        Assert.Equal(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake" }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Status = SettableRecipeStatusViewModel.Draft }));
    }

    [Fact]
    public void A_blank_optional_field_is_the_same_request_as_an_omitted_one()
    {
        Assert.Equal(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake" }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Description = "   " }));
    }

    // ---- Requests that do not ----

    [Fact]
    public void A_tag_written_in_another_casing_is_a_different_request()
    {
        // Tempting to collapse, and wrong to. Casing does not change which tag is meant — both normalize to
        // "weeknight" — but it does change the recipe: if the workspace has no such tag yet, the spelling in
        // the request becomes the vocabulary entry's display name, and the creator sees it. Two requests that
        // would leave different text on screen are not one request.
        Assert.NotEqual(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Tags = ["Weeknight"] }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Tags = ["WEEKNIGHT"] }));
    }

    [Fact]
    public void A_different_title_is_a_different_request()
    {
        Assert.NotEqual(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake" }),
            Fingerprint(new CreateRecipeViewModel { Title = "A different cake" }));
    }

    [Fact]
    public void An_extra_tag_is_a_different_request()
    {
        Assert.NotEqual(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Tags = ["weeknight"] }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Tags = ["weeknight", "freezer"] }));
    }

    [Fact]
    public void An_explicit_ready_status_is_a_different_request()
    {
        Assert.NotEqual(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake" }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Status = SettableRecipeStatusViewModel.Ready }));
    }

    [Fact]
    public void Internal_spacing_is_a_different_request()
    {
        // Because it is a different recipe: internal spacing is the creator's own text, and nothing collapses
        // it. Treating these as one request would replay the wrong headnote.
        Assert.NotEqual(
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Headnote = "one my" }),
            Fingerprint(new CreateRecipeViewModel { Title = "Cake", Headnote = "one   my" }));
    }

    // ---- Normalization itself ----

    [Fact]
    public void Text_is_trimmed_and_otherwise_untouched()
    {
        var canonical = CanonicalCreateRecipe.From(new CreateRecipeViewModel
        {
            Title = "  Olive oil cake  ",
            Headnote = "  The one   my grandmother made.  ",
        });

        Assert.Equal("Olive oil cake", canonical.Title);
        Assert.Equal("The one   my grandmother made.", canonical.Headnote);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_optional_field_becomes_null(string? blank)
    {
        Assert.Null(CanonicalCreateRecipe.From(new CreateRecipeViewModel { Title = "Cake", Description = blank }).Description);
    }

    [Fact]
    public void Tags_keep_the_creators_wording_and_carry_a_normalized_identity()
    {
        var canonical = CanonicalCreateRecipe.From(
            new CreateRecipeViewModel { Title = "Cake", Tags = ["  Freezer Friendly  ", "Weeknight"] });

        Assert.Equal(["Freezer Friendly", "Weeknight"], canonical.Tags.Select(tag => tag.Name));
        Assert.Equal(["freezer friendly", "weeknight"], canonical.Tags.Select(tag => tag.NormalizedName));
    }

    [Fact]
    public void Tags_are_ordered_by_identity_rather_than_by_how_they_were_typed()
    {
        var canonical = CanonicalCreateRecipe.From(
            new CreateRecipeViewModel { Title = "Cake", Tags = ["zucchini", "apple", "mango"] });

        Assert.Equal(["apple", "mango", "zucchini"], canonical.Tags.Select(tag => tag.NormalizedName));
    }

    [Fact]
    public void Tags_that_normalize_alike_are_collapsed_keeping_the_first_wording()
    {
        var canonical = CanonicalCreateRecipe.From(
            new CreateRecipeViewModel { Title = "Cake", Tags = ["Weeknight", "  WEEKNIGHT "] });

        var tag = Assert.Single(canonical.Tags);
        Assert.Equal("Weeknight", tag.Name);
    }

    [Fact]
    public void No_tags_becomes_an_empty_set()
    {
        Assert.Empty(CanonicalCreateRecipe.From(new CreateRecipeViewModel { Title = "Cake", Tags = null }).Tags);
        Assert.Empty(CanonicalCreateRecipe.From(new CreateRecipeViewModel { Title = "Cake", Tags = [] }).Tags);
    }

    [Fact]
    public void A_blank_tag_is_dropped_rather_than_carried()
    {
        // The validator refuses this request outright; this only decides what a caller that bypassed it gets,
        // and a blank tag name has no identity to store.
        var canonical = CanonicalCreateRecipe.From(
            new CreateRecipeViewModel { Title = "Cake", Tags = ["weeknight", "   ", null] });

        Assert.Single(canonical.Tags);
    }

    [Fact]
    public void The_fingerprint_carries_no_actor_workspace_or_timestamp()
    {
        // Any of those would make every request unique, which is the opposite of what a fingerprint is for.
        var json = Fingerprint(new CreateRecipeViewModel { Title = "Cake" });

        Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAt", json, StringComparison.OrdinalIgnoreCase);
    }
}
