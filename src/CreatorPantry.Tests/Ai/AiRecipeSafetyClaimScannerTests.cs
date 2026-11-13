using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The scanner that closes 14.7's BL-2: free text in a recipe answer may not assure safety, absence or
/// suitability.
/// </summary>
/// <remarks>
/// Both directions are pinned, and the second matters as much as the first. A scanner that refused the
/// *subject* of these capabilities — "vegan", "fermentation", "medium-rare" — would make substitution,
/// adaptation and review unusable, which is why the broad editorial scanner could not be pointed at them and
/// why the gap went unclosed for so long. Each phrase below is a case someone could reasonably write.
/// </remarks>
public sealed class AiRecipeSafetyClaimScannerTests
{
    [Theory]
    // An absence claim about the dish or the swap, however it is spelled.
    [InlineData("Gluten-Free Buttermilk Soda Bread")]
    [InlineData("Nut Free Granola")]
    [InlineData("oat milk makes this dairy-free")]
    [InlineData("free from gluten")]
    [InlineData("free of all nuts")]
    [InlineData("contains no dairy")]
    [InlineData("does not contain any peanuts")]
    [InlineData("doesn't contain wheat")]
    [InlineData("an allergen-free alternative")]
    [InlineData("allergy-friendly for your readers")]
    [InlineData("hypoallergenic")]
    // An assurance that eating it is safe, or that it suits a person.
    [InlineData("perfectly safe to eat raw")]
    [InlineData("this is 100% safe")]
    [InlineData("safe to serve to children")]
    [InlineData("safe for people with a nut allergy")]
    [InlineData("food-safe at this temperature")]
    [InlineData("suitable for coeliacs")]
    [InlineData("fine for diabetics")]
    [InlineData("okay for pregnant readers")]
    [InlineData("medically safe")]
    [InlineData("no health risk")]
    // A guarantee, or a cure.
    [InlineData("guaranteed to set")]
    [InlineData("this kills all bacteria")]
    [InlineData("cures your cold")]
    public void An_assurance_is_found(string text)
    {
        Assert.NotNull(AiRecipeSafetyClaimScanner.FirstAssurance(text));
    }

    [Theory]
    // The subject matter of these capabilities. Each of these would be refused by the editorial scanner, and
    // refusing them here would leave a dietary adaptation with no answer it could give.
    [InlineData("Replace the butter with oat milk")]
    [InlineData("this makes the recipe vegan as written")]
    [InlineData("a plant-based alternative with a similar fat content")]
    [InlineData("keto-friendly flavour profile")]
    [InlineData("halal and kosher kitchens vary on this")]
    [InlineData("ferment for three days at room temperature")]
    [InlineData("a home canning recipe needs a tested process")]
    [InlineData("cure the pork belly for seven days")]
    [InlineData("medium-rare, about 54 °C")]
    [InlineData("bake until cooked through")]
    [InlineData("raw eggs are used in this dressing")]
    [InlineData("low sodium by comparison")]
    [InlineData("a safe bet for texture")]
    [InlineData("the oven temperature looks low for a loaf")]
    [InlineData("introduces tree nuts")]
    [InlineData("may introduce sesame, depending on the brand")]
    public void Ordinary_recipe_language_is_not_an_assurance(string text)
    {
        Assert.Null(AiRecipeSafetyClaimScanner.FirstAssurance(text));
    }

    [Theory]
    // What to buy, named by the label on its packet: the manufacturer's claim, checkable by the reader, not one
    // CreatorPantry makes about the dish. Live dietary adaptations were refused for wording like this.
    [InlineData("use a mustard labelled gluten-free")]
    [InlineData("use cider vinegar that is labeled gluten-free if needed")]
    [InlineData("a certified gluten-free oat")]
    [InlineData("buy one sold as dairy-free")]
    [InlineData("gluten-free-labelled oats")]
    [InlineData("check for a gluten-free certified brand")]
    public void A_product_named_by_its_label_is_not_an_assurance(string text)
    {
        Assert.Null(AiRecipeSafetyClaimScanner.FirstAssurance(text));
    }

    [Theory]
    // The same words used as a claim: about the product with no label, about the dish, or alongside a label
    // that excuses only itself.
    [InlineData("use gluten-free mustard", "gluten-free")]
    [InlineData("this bread is certified gluten-free", "gluten-free")]
    [InlineData("the loaf is gluten-free labelled", "gluten-free")]
    [InlineData("it's labelled dairy-free", "dairy-free")]
    [InlineData("use mustard labelled gluten-free, which makes the glaze dairy-free", "dairy-free")]
    [InlineData("mustard labelled gluten-free is safe for coeliacs", "safe for")]
    public void A_free_from_claim_is_still_found_when_no_label_excuses_it(string text, string expected)
    {
        Assert.Equal(expected, AiRecipeSafetyClaimScanner.FirstAssurance(text));
    }

    /// <summary>A title is what a recipe is published under: no label wording excuses a free-from name.</summary>
    [Theory]
    [InlineData("Certified Gluten-Free Soda Bread")]
    [InlineData("Soda Bread with Flour Labelled Gluten-Free")]
    public void A_title_gets_no_allowance_for_a_label(string title)
    {
        Assert.NotNull(AiRecipeSafetyClaimScanner.FirstTitleAssurance(title));
    }

    [Fact]
    public void The_message_names_the_label_wording_that_would_be_accepted()
    {
        var message = AiRecipeSafetyClaimScanner.Message("gluten-free", "A proposed preparationNote value");

        Assert.Contains("labelled gluten-free", message, StringComparison.Ordinal);
        Assert.Contains("never in a title", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_an_assurance()
    {
        Assert.Null(AiRecipeSafetyClaimScanner.FirstAssurance((string?)null));
        Assert.Null(AiRecipeSafetyClaimScanner.FirstAssurance(string.Empty));
        Assert.Null(AiRecipeSafetyClaimScanner.FirstAssurance("   "));
    }

    [Fact]
    public void The_first_assurance_across_several_fields_is_the_one_reported()
    {
        var found = AiRecipeSafetyClaimScanner.FirstAssurance(
            "Replace the butter with oat milk", null, "and it is dairy-free", "guaranteed");

        Assert.Equal("dairy-free", found);
    }

    [Fact]
    public void The_message_quotes_the_phrase_and_says_where_it_was()
    {
        var message = AiRecipeSafetyClaimScanner.Message("dairy-free", "An alternative");

        Assert.Contains("An alternative", message, StringComparison.Ordinal);
        Assert.Contains("\"dairy-free\"", message, StringComparison.Ordinal);
        // The way to say the same thing properly, named in the refusal rather than left to be guessed.
        Assert.Contains("safety caution", message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A long match is cut rather than reproduced whole: the refusal is written into an audit record, and a
    /// model that pasted a paragraph should not put a paragraph there.
    /// </remarks>
    [Fact]
    public void A_long_phrase_is_excerpted_in_the_message()
    {
        var message = AiRecipeSafetyClaimScanner.Message(new string('a', 120), "A warning");

        Assert.Contains("...", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 120), message, StringComparison.Ordinal);
    }
}
