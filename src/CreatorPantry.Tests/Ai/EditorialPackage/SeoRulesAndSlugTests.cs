using CreatorPantry.Domain.Managers.Reference;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

public sealed class SeoRulesTests
{
    [Fact]
    public void The_defaults_are_the_conventional_search_result_limits()
    {
        var rules = new SeoRules();

        Assert.Equal((10, 60), (rules.TitleMinLength, rules.TitleMaxLength));
        Assert.Equal((50, 160), (rules.MetaDescriptionMinLength, rules.MetaDescriptionMaxLength));
        Assert.Equal(125, rules.AltTextMaxLength);
    }

    [Fact]
    public void The_version_is_stable_for_the_same_limits_and_changes_when_any_limit_changes()
    {
        var baseline = new SeoRules().Version;

        Assert.Equal(baseline, new SeoRules().Version);
        Assert.StartsWith("seo.rules/1-", baseline, StringComparison.Ordinal);

        foreach (var changed in new Action<SeoRules>[]
        {
            rules => rules.TitleMaxLength = 70,
            rules => rules.MetaDescriptionMaxLength = 155,
            rules => rules.KeyPhraseMaxCount = 10,
            rules => rules.AltTextMaxLength = 100,
            rules => rules.MaxInternalLinks = 3,
            rules => rules.SlugMaxLength = 50,
        })
        {
            var rules = new SeoRules();
            changed(rules);

            Assert.NotEqual(baseline, rules.Version);
        }
    }

    [Fact]
    public void Configuration_overrides_the_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Content:Seo:TitleMaxLength"] = "70" })
            .Build();

        var rules = SeoRules.Bind(configuration);

        Assert.Equal(70, rules.TitleMaxLength);
        Assert.Equal(160, rules.MetaDescriptionMaxLength);
        Assert.NotEqual(new SeoRules().Version, rules.Version);
    }

    [Theory]
    [InlineData("Content:Seo:TitleMaxLength", "5")]
    [InlineData("Content:Seo:MetaDescriptionMaxLength", "0")]
    [InlineData("Content:Seo:KeyPhraseMaxCount", "1")]
    [InlineData("Content:Seo:AltTextMaxLength", "0")]
    [InlineData("Content:Seo:TitleMaxLength", "100000")]
    [InlineData("Content:Seo:MetaDescriptionMaxLength", "501")]
    [InlineData("Content:Seo:MaxInternalLinks", "41")]
    [InlineData("Content:Seo:KeyPhraseMaxCount", "21")]
    [InlineData("Content:Seo:SlugMaxLength", "201")]
    public void Limits_that_cannot_be_met_or_are_absurd_are_refused_at_startup(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        Assert.Throws<InvalidOperationException>(() => SeoRules.Bind(configuration));
    }
}

public sealed class SeoSlugTests
{
    [Theory]
    [InlineData("Buttermilk Soda Bread", "buttermilk-soda-bread")]
    [InlineData("  Olive-Oil   Cake!  ", "olive-oil-cake")]
    [InlineData("Crème Brûlée", "creme-brulee")]
    [InlineData("Mac & Cheese", "mac-and-cheese")]
    [InlineData("Don't Panic Pancakes", "dont-panic-pancakes")]
    [InlineData("Grandma’s 3-Ingredient Cookies", "grandmas-3-ingredient-cookies")]
    [InlineData("Soup — Hearty, Quick", "soup-hearty-quick")]
    public void A_title_becomes_lowercase_ascii_words_joined_by_single_hyphens(string title, string expected)
    {
        Assert.Equal(expected, SeoSlug.FromTitle(title, 60));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? ...")]
    [InlineData("日本語のレシピ")]
    public void A_title_with_nothing_usable_yields_the_empty_string_and_the_caller_chooses_a_fallback(string title)
    {
        Assert.Equal(string.Empty, SeoSlug.FromTitle(title, 60));
        Assert.Equal(string.Empty, SeoSlug.FromTitle(null, 60));
    }

    [Fact]
    public void A_slug_is_cut_at_a_hyphen_boundary_inside_the_limit_with_no_trailing_hyphen()
    {
        var slug = SeoSlug.FromTitle("The Very Best Easy Weeknight Buttermilk Soda Bread With Extra Crispy Crust", 40);

        Assert.True(slug.Length <= 40);
        Assert.False(slug.EndsWith('-'));
        Assert.Equal("the-very-best-easy-weeknight-buttermilk", slug);
    }

    [Fact]
    public void A_single_word_longer_than_the_limit_is_cut_hard()
    {
        Assert.Equal("supercalifra", SeoSlug.FromTitle("Supercalifragilistic", 12));
    }

    [Theory]
    [InlineData("Buttermilk Soda Bread")]
    [InlineData("Crème Brûlée & Friends")]
    public void Every_slug_matches_the_format_rule_and_the_function_is_deterministic(string title)
    {
        var slug = SeoSlug.FromTitle(title, 60);

        Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", slug);
        Assert.Equal(slug, SeoSlug.FromTitle(title, 60));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("sitemap")]
    [InlineData("wp-admin")]
    public void A_reserved_path_segment_is_never_offered_as_it_stands(string reserved)
    {
        var offered = SeoSlug.Offer(reserved, Guid.NewGuid(), 60);

        Assert.NotEqual(reserved, offered);
        Assert.EndsWith("-recipe", offered, StringComparison.Ordinal);
        Assert.False(SeoSlug.IsReserved(offered));
    }

    [Fact]
    public void An_ordinary_slug_is_offered_unchanged()
    {
        Assert.Equal("soda-bread", SeoSlug.Offer("soda-bread", Guid.NewGuid(), 60));
    }

    [Fact]
    public void Nothing_usable_in_a_title_falls_back_to_a_slug_unique_to_the_recipe_never_a_shared_constant()
    {
        var first = SeoSlug.Offer(string.Empty, Guid.NewGuid(), 60);
        var second = SeoSlug.Offer(string.Empty, Guid.NewGuid(), 60);

        Assert.StartsWith("recipe-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", first);
    }

    [Fact]
    public void A_reserved_slug_that_fills_the_limit_still_fits_with_its_suffix()
    {
        var offered = SeoSlug.Offer("api", Guid.NewGuid(), 8);

        Assert.True(offered.Length <= 10, offered);
    }
}
