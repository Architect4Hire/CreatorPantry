using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// SEO output is recommendation, never measurement: the scanner reports the measurements a model might dress a
/// recommendation in, the image detail nothing has seen, and the ordinary unsupported claims.
/// </summary>
public sealed class AiSeoClaimScannerTests
{
    private static readonly AiEditorialSourceFacts Source = AiEditorialSourceFacts.Of(
        ["220", "25"], "A dense, tangy loaf. Bake at 220C for 25 minutes.", null);

    private static readonly Guid Captioned = Guid.NewGuid();
    private static readonly Guid Bare = Guid.NewGuid();

    private static readonly Dictionary<Guid, string?> Captions = new()
    {
        [Captioned] = "Sliced soda bread on a board",
        [Bare] = null,
    };

    private static IReadOnlyList<AiSeoFinding> Scan(AiSeoSections sections) =>
        AiSeoClaimScanner.Scan(sections, Source, Captions, "Buttermilk Soda Bread");

    private static AiSeoSections Meta(string text) => new() { MetaDescription = new AiSeoText { Text = text } };

    // ---- metrics ----

    [Theory]
    [InlineData("A recipe with high search volume.")]
    [InlineData("The top ranking soda bread.")]
    [InlineData("Ranked #1 on Google.")]
    [InlineData("Beat your competitors with this loaf.")]
    [InlineData("Drives traffic and a great click-through rate.")]
    [InlineData("Low keyword difficulty.")]
    [InlineData("Over 10,000 monthly searches.")]
    [InlineData("Get 5k views from this bread.")]
    [InlineData("Number one soda bread, top 10 guaranteed.")]
    public void A_search_metric_or_ranking_claim_is_reported(string text)
    {
        Assert.Contains(Scan(Meta(text)), finding => finding.Code == AiSeoClaimScanner.UnsupportedMetric && finding.Kind is AiWarningKind.UnverifiedClaim);
    }

    [Fact]
    public void Ordinary_copy_is_not_mistaken_for_a_metric()
    {
        var findings = Scan(Meta("A dense, tangy loaf you can bake at 220 degrees in 25 minutes."));

        Assert.DoesNotContain(findings, finding => finding.Code == AiSeoClaimScanner.UnsupportedMetric);
        Assert.Empty(findings);
    }

    [Fact]
    public void A_metric_is_found_in_a_key_phrase_an_anchor_and_a_reason_as_well_as_in_prose()
    {
        var sections = new AiSeoSections
        {
            KeyPhrases = [new AiSeoKeyPhrase { Phrase = "high search volume bread" }],
            InternalLinks = [new AiSeoInternalLink { RecipeId = Guid.NewGuid(), AnchorText = "Top ranking loaf", Reason = "Drives traffic." }],
        };

        var metrics = Scan(sections).Where(finding => finding.Code == AiSeoClaimScanner.UnsupportedMetric).ToList();

        Assert.Contains(metrics, finding => finding.Section is AiSeoSection.KeyPhrases);
        Assert.Contains(metrics, finding => finding.Section is AiSeoSection.InternalLinks);
    }

    [Theory]
    [InlineData("Search volume is high.")]
    [InlineData("Se​arch volume is high.")]
    [InlineData("ｓｅａｒｃｈ volume is high.")]
    public void Formatting_tricks_do_not_hide_a_metric(string text)
    {
        Assert.Contains(Scan(Meta(text)), finding => finding.Code == AiSeoClaimScanner.UnsupportedMetric);
    }

    // ---- the ordinary claims still apply to SEO copy ----

    [Fact]
    public void Unsupported_figures_safety_claims_and_provenance_are_found_in_title_and_meta_too()
    {
        var findings = Scan(Meta("My grandmother's healthy gluten-free loaf, ready in 45 minutes."));

        Assert.Contains(findings, finding => finding.Code == AiEditorialClaimScanner.UnsupportedNumber);
        Assert.Contains(findings, finding => finding.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim);
        Assert.Contains(findings, finding => finding.Code == AiEditorialClaimScanner.UnsupportedProvenance);
    }

    // ---- alt text ----

    [Fact]
    public void Every_alt_text_carries_a_finding_that_it_was_not_checked_against_the_image()
    {
        var sections = new AiSeoSections
        {
            AltText =
            [
                new AiSeoAltText { AssetLinkId = Captioned, Text = "Sliced soda bread on a board", Basis = AiSeoAltTextBasis.Caption },
                new AiSeoAltText { AssetLinkId = Bare, Text = "Buttermilk soda bread", Basis = AiSeoAltTextBasis.RecipeTitle },
            ],
        };

        var notChecked = Scan(sections).Where(finding => finding.Code == AiSeoClaimScanner.AltTextNotCheckedAgainstImage).ToList();

        Assert.Equal([0, 1], notChecked.Select(finding => finding.ItemIndex));
        Assert.All(notChecked, finding => Assert.Equal(AiWarningKind.Limitation, finding.Kind));
    }

    [Theory]
    [InlineData("Golden sliced soda bread", "golden")]
    [InlineData("Soda bread on a rustic wooden board", "rustic")]
    [InlineData("Steaming soda bread", "steaming")]
    [InlineData("Overhead view of sliced soda bread", "overhead")]
    [InlineData("Soda bread garnished with herbs", "garnished")]
    public void A_visual_detail_the_caption_and_title_do_not_contain_is_reported(string text, string detail)
    {
        var sections = new AiSeoSections
        {
            AltText = [new AiSeoAltText { AssetLinkId = Captioned, Text = text, Basis = AiSeoAltTextBasis.Caption }],
        };

        // "rustic wooden board" names two details, each reported; what matters is that the one named is among them.
        var findings = Scan(sections).Where(f => f.Code == AiSeoClaimScanner.UnsupportedVisualDetail).ToList();

        Assert.Contains(findings, finding => finding.Message.Contains(detail, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_visual_detail_the_caption_itself_gives_is_not_reported()
    {
        var captions = new Dictionary<Guid, string?> { [Captioned] = "Golden sliced soda bread on a rustic board" };
        var sections = new AiSeoSections
        {
            AltText = [new AiSeoAltText { AssetLinkId = Captioned, Text = "Golden sliced soda bread on a rustic board", Basis = AiSeoAltTextBasis.Caption }],
        };

        Assert.DoesNotContain(
            AiSeoClaimScanner.Scan(sections, Source, captions, "Soda Bread"),
            finding => finding.Code == AiSeoClaimScanner.UnsupportedVisualDetail);
    }

    [Fact]
    public void Findings_locate_the_item_they_are_about()
    {
        var sections = new AiSeoSections
        {
            KeyPhrases = [new AiSeoKeyPhrase { Phrase = "soda bread" }, new AiSeoKeyPhrase { Phrase = "top ranking bread" }],
        };

        var finding = Assert.Single(Scan(sections), f => f.Code == AiSeoClaimScanner.UnsupportedMetric);

        Assert.Equal(AiSeoSection.KeyPhrases, finding.Section);
        Assert.Equal(1, finding.ItemIndex);

        // And the standing finding that no search data was consulted, which belongs to the section as a whole.
        Assert.Contains(Scan(sections), f => f.Code == AiSeoClaimScanner.KeyPhrasesNotFromSearchData && f.ItemIndex is null);
    }

    // ---- a wider net, with a benign corpus to bound false positives ----

    [Theory]
    [InlineData("A recipe with a big search-volume bump.")]
    [InlineData("Dominates the SERPs for bread.")]
    [InlineData("Low competition, high reward.")]
    [InlineData("Our most searched loaf.")]
    [InlineData("Popular on Google every autumn.")]
    [InlineData("Sits on page 1 for soda bread.")]
    [InlineData("A trending bread this year.")]
    [InlineData("Thousands of searches a month for this loaf.")]
    [InlineData("The top spot for buttermilk recipes.")]
    [InlineData("Our No. 1 pick.")]
    [InlineData("Brings in lots of visitors.")]
    [InlineData("With a low bounce rate.")]
    [InlineData("High conversion rate recipe.")]
    [InlineData("A keyword-difficulty friendly loaf.")]
    [InlineData("In high demand among bakers.")]
    public void Phrasings_of_a_search_metric_that_a_thin_pattern_would_miss_are_reported(string text)
    {
        Assert.Contains(Scan(Meta(text)), finding => finding.Code == AiSeoClaimScanner.UnsupportedMetric);
    }

    [Theory]
    [InlineData("A dense, tangy loaf with a soft crumb.")]
    [InlineData("Easy weeknight bread you can bake at 220 degrees.")]
    [InlineData("Mix, shape and bake a buttermilk loaf in 25 minutes.")]
    [InlineData("A quick bread with no yeast and no waiting.")]
    [InlineData("Tangy, hearty and simple to make at home.")]
    public void Ordinary_search_copy_is_not_reported_as_a_metric(string text)
    {
        Assert.DoesNotContain(Scan(Meta(text)), finding => finding.Code == AiSeoClaimScanner.UnsupportedMetric);
    }

    [Theory]
    [InlineData("Juicy soda bread")]
    [InlineData("A bowl of soup with bread")]
    [InlineData("Soda bread served in a skillet")]
    [InlineData("Charred crust on the loaf")]
    [InlineData("Soda bread with a fork beside it")]
    public void A_wider_set_of_how_an_image_looks_is_reported_when_the_caption_does_not_give_it(string text)
    {
        var sections = new AiSeoSections { AltText = [new AiSeoAltText { AssetLinkId = Bare, Text = text, Basis = AiSeoAltTextBasis.RecipeTitle }] };

        Assert.Contains(Scan(sections), finding => finding.Code == AiSeoClaimScanner.UnsupportedVisualDetail);
    }

    [Fact]
    public void A_title_adjective_does_not_license_a_visual_word_in_the_alt_text()
    {
        // "Golden Milk" names a drink; it says nothing about how its photograph looks.
        var sections = new AiSeoSections { AltText = [new AiSeoAltText { AssetLinkId = Bare, Text = "Golden milk", Basis = AiSeoAltTextBasis.RecipeTitle }] };

        var findings = AiSeoClaimScanner.Scan(sections, Source, Captions, "Golden Milk");

        Assert.Contains(findings, finding => finding.Code == AiSeoClaimScanner.UnsupportedVisualDetail);
    }

    [Fact]
    public void A_hyphenated_caption_supports_the_words_in_it()
    {
        var captions = new Dictionary<Guid, string?> { [Captioned] = "Golden-brown sliced soda bread" };
        var sections = new AiSeoSections { AltText = [new AiSeoAltText { AssetLinkId = Captioned, Text = "Golden-brown sliced soda bread", Basis = AiSeoAltTextBasis.Caption }] };

        Assert.DoesNotContain(
            AiSeoClaimScanner.Scan(sections, Source, captions, "Soda Bread"),
            finding => finding.Code == AiSeoClaimScanner.UnsupportedVisualDetail);
    }
}
