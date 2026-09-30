using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>The SEO output contract and its configured rules, directly; the same cases also run as evaluation fixtures.</summary>
public sealed class AiSeoPackageOutputValidatorTests
{
    private const string Version = "content.seo-package.v1";
    private static readonly SeoRules Rules = new();

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument> Validate(string sections, string? extra = null, SeoRules? rules = null) =>
        AiSeoPackageOutputValidator.Validate($$"""{"schemaVersion":"{{Version}}","sections":{ {{sections}} }{{extra}}}""", Version, rules ?? Rules);

    private static string Chars(int count) => new('a', count);

    [Fact]
    public void A_full_package_and_an_empty_one_are_both_valid()
    {
        var asset = Guid.NewGuid();
        var recipe = Guid.NewGuid();
        var result = Validate($$"""
            "seoTitle":{"text":"Easy Buttermilk Soda Bread"},
            "metaDescription":{"text":"{{Chars(80)}}"},
            "keyPhrases":[{"phrase":"soda bread"},{"phrase":"quick bread"},{"phrase":"no-yeast loaf"}],
            "altText":[{"assetLinkId":"{{asset}}","text":"A loaf of soda bread","basis":"RecipeTitle"}],
            "internalLinks":[{"recipeId":"{{recipe}}","anchorText":"Brown bread","reason":"Also quick."}]
            """);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.True(Validate("").Succeeded);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void The_title_length_limits_are_exact(int length, bool valid)
    {
        var result = Validate($$"""
            "seoTitle":{"text":"{{Chars(length)}}"}
            """);

        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.Equal(AiOutputReason.SeoLengthOutOfRange, result.Failure!.ReasonCode);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(160, true)]
    [InlineData(161, false)]
    public void The_meta_description_length_limits_are_exact(int length, bool valid)
    {
        var result = Validate($$"""
            "metaDescription":{"text":"{{Chars(length)}}"}
            """);

        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.Equal(AiOutputReason.SeoLengthOutOfRange, result.Failure!.ReasonCode);
    }

    [Fact]
    public void Limits_come_from_the_rules_not_from_constants()
    {
        var strict = new SeoRules { TitleMaxLength = 20 };

        Assert.False(Validate($$"""
            "seoTitle":{"text":"{{Chars(30)}}"}
            """, rules: strict).Succeeded);
        Assert.True(Validate($$"""
            "seoTitle":{"text":"{{Chars(30)}}"}
            """).Succeeded);
    }

    [Fact]
    public void Length_is_counted_after_canonicalization()
    {
        // 61 fullwidth letters fold to 61 ASCII letters; zero-width characters are refused outright.
        var fullwidth = new string('ａ', 61);

        Assert.Equal(AiOutputReason.SeoLengthOutOfRange, Validate($$"""
            "seoTitle":{"text":"{{fullwidth}}"}
            """).Failure!.ReasonCode);
    }

    [Theory]
    [InlineData("""[{"phrase":"soda bread"},{"phrase":"quick bread"}]""")]
    [InlineData("""[{"phrase":"soda bread"},{"phrase":"quick bread"},{"phrase":"Quick Bread"}]""")]
    [InlineData("""[{"phrase":"soda bread"},{"phrase":"quick bread"},{"phrase":"a b c d e f"}]""")]
    [InlineData("""[{"phrase":"soda bread"},{"phrase":"quick bread"},{"phrase":"x"}]""")]
    [InlineData("""[{"phrase":"soda bread"},{"phrase":"quick bread"},{"phrase":"best!! bread"}]""")]
    [InlineData("""[{"phrase":"soda bread"},{"phrase":"quick bread"},{"phrase":"soda bread"}]""")]
    public void Key_phrases_must_be_a_set_of_unique_lowercase_bounded_words(string phrases)
    {
        var result = Validate($"\"keyPhrases\":{phrases}");

        Assert.Equal(AiOutputReason.SeoKeyPhraseInvalid, result.Failure!.ReasonCode);
    }

    [Fact]
    public void More_key_phrases_than_the_limit_is_rejected()
    {
        var phrases = string.Join(',', Enumerable.Range(0, Rules.KeyPhraseMaxCount + 1).Select(i => $"{{\"phrase\":\"phrase number {(char)('a' + i)}\"}}"));

        Assert.Equal(AiOutputReason.ValueTooLong, Validate($"\"keyPhrases\":[{phrases}]").Failure!.ReasonCode);
    }

    [Theory]
    [InlineData("Image of a loaf")]
    [InlineData("A picture of a loaf")]
    [InlineData("photo showing a loaf")]
    [InlineData("Graphic depicting bread")]
    public void Alt_text_may_not_begin_with_a_redundant_image_of_prefix(string text)
    {
        var result = Validate($$"""
            "altText":[{"assetLinkId":"{{Guid.NewGuid()}}","text":"{{text}}","basis":"RecipeTitle"}]
            """);

        Assert.Equal(AiOutputReason.SeoAltTextInvalid, result.Failure!.ReasonCode);
    }

    [Fact]
    public void Alt_text_length_duplicates_and_undeclared_basis_are_rejected()
    {
        var asset = Guid.NewGuid();

        Assert.Equal(AiOutputReason.SeoAltTextInvalid, Validate($$"""
            "altText":[{"assetLinkId":"{{asset}}","text":"{{Chars(Rules.AltTextMaxLength + 1)}}","basis":"RecipeTitle"}]
            """).Failure!.ReasonCode);

        Assert.Equal(AiOutputReason.SeoAltTextInvalid, Validate($$"""
            "altText":[{"assetLinkId":"{{asset}}","text":"A loaf","basis":"RecipeTitle"},{"assetLinkId":"{{Guid.NewGuid()}}","text":"a LOAF","basis":"RecipeTitle"}]
            """).Failure!.ReasonCode);

        Assert.Equal(AiOutputReason.SeoAltTextInvalid, Validate($$"""
            "altText":[{"assetLinkId":"{{asset}}","text":"A loaf","basis":"RecipeTitle"},{"assetLinkId":"{{asset}}","text":"Another loaf","basis":"RecipeTitle"}]
            """).Failure!.ReasonCode);

        Assert.Equal(AiOutputReason.KindNotDeclared, Validate($$"""
            "altText":[{"assetLinkId":"{{asset}}","text":"A loaf","basis":"Unspecified"}]
            """).Failure!.ReasonCode);
    }

    [Fact]
    public void Link_ideas_with_a_repeat_a_long_anchor_or_a_url_are_rejected_and_the_count_is_capped()
    {
        var recipe = Guid.NewGuid();

        Assert.Equal(AiOutputReason.SeoLinkInvalid, Validate($$"""
            "internalLinks":[{"recipeId":"{{recipe}}","anchorText":"A","reason":"R."},{"recipeId":"{{recipe}}","anchorText":"B","reason":"R."}]
            """).Failure!.ReasonCode);

        Assert.Equal(AiOutputReason.SeoLinkInvalid, Validate($$"""
            "internalLinks":[{"recipeId":"{{recipe}}","anchorText":"{{Chars(Rules.AnchorTextMaxLength + 1)}}","reason":"R."}]
            """).Failure!.ReasonCode);

        Assert.Equal(AiOutputReason.IllegalCharacters, Validate($$"""
            "internalLinks":[{"recipeId":"{{recipe}}","anchorText":"See https://example.com/x","reason":"R."}]
            """).Failure!.ReasonCode);

        var many = string.Join(',', Enumerable.Range(0, Rules.MaxInternalLinks + 1).Select(_ => $"{{\"recipeId\":\"{Guid.NewGuid()}\",\"anchorText\":\"A\",\"reason\":\"R.\"}}"));
        Assert.Equal(AiOutputReason.ValueTooLong, Validate($"\"internalLinks\":[{many}]").Failure!.ReasonCode);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{},"slug":"my-slug"}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{"slug":{"text":"my-slug"}}}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{"seoTitle":{"text":"Easy soda bread","searchVolume":1000}}}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{},"ranking":1}""")]
    public void There_is_no_slug_and_no_metric_field_to_write_to_so_one_is_an_unknown_field(string payload)
    {
        Assert.Equal(AiOutputReason.UnknownField, AiSeoPackageOutputValidator.Validate(payload, Version, Rules).Failure!.ReasonCode);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":null}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{"keyPhrases":null}}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{"altText":[null]}}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{},"warnings":null}""")]
    public void A_null_where_the_schema_requires_a_value_is_rejected_not_thrown_on(string payload)
    {
        Assert.Equal(AiOutputReason.ShapeInvalid, AiSeoPackageOutputValidator.Validate(payload, Version, Rules).Failure!.ReasonCode);
    }

    [Fact]
    public void A_warning_imitating_a_server_finding_is_rejected_whichever_prefix_it_uses()
    {
        foreach (var prefix in new[] { "[seo.unsupported_metric]", "[editorial.unsupported_number]" })
        {
            var result = Validate("", $",\"warnings\":[{{\"kind\":\"Limitation\",\"message\":\"{prefix} none\"}}]");

            Assert.Equal(AiOutputReason.DomainInvalid, result.Failure!.ReasonCode);
        }
    }

    [Fact]
    public void The_exported_schema_names_what_the_prompt_asks_for_and_no_metric()
    {
        var schema = AiSeoPackageOutputSchema.Json;

        foreach (var name in new[] { "seoTitle", "metaDescription", "keyPhrases", "altText", "internalLinks", "assetLinkId", "basis", "anchorText" })
        {
            Assert.Contains(name, schema, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[] { "volume", "ranking", "difficulty", "competitor", "traffic" })
        {
            Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("\"slug\"", schema, StringComparison.Ordinal);
    }

    // ---- the request's own context is checked here, so a corrective re-ask can apply ----

    private static readonly Guid Asset = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BareAsset = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Offered = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Self = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private static AiSeoRequestContext Context(params AiSeoSection[] requested) => new(
        requested.Length == 0 ? AiSeoSectionCatalog.All.ToHashSet() : requested.ToHashSet(),
        new Dictionary<Guid, string?> { [Asset] = "A caption", [BareAsset] = null },
        new HashSet<Guid> { Offered },
        Self);

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument> WithContext(string sections, AiSeoRequestContext context) =>
        AiSeoPackageOutputValidator.Validate($$"""{"schemaVersion":"{{Version}}","sections":{ {{sections}} } }""", Version, Rules, context);

    [Fact]
    public void An_answer_inside_the_request_context_validates()
    {
        var result = WithContext($$"""
            "altText":[{"assetLinkId":"{{Asset}}","text":"A loaf","basis":"Caption"}],
            "internalLinks":[{"recipeId":"{{Offered}}","anchorText":"Brown bread","reason":"Quick."}]
            """, Context());

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("caption")]
    [InlineData("link-self")]
    [InlineData("link-stranger")]
    [InlineData("section")]
    public void An_answer_outside_the_request_context_is_a_correctable_domain_failure(string what)
    {
        var (sections, context) = what switch
        {
            "image" => ($$""" "altText":[{"assetLinkId":"{{Guid.NewGuid()}}","text":"A loaf","basis":"RecipeTitle"}] """, Context()),
            "caption" => ($$""" "altText":[{"assetLinkId":"{{BareAsset}}","text":"A loaf","basis":"Caption"}] """, Context()),
            "link-self" => ($$""" "internalLinks":[{"recipeId":"{{Self}}","anchorText":"A","reason":"R."}] """, Context()),
            "link-stranger" => ($$""" "internalLinks":[{"recipeId":"{{Guid.NewGuid()}}","anchorText":"A","reason":"R."}] """, Context()),
            _ => ($$""" "seoTitle":{"text":"Easy soda bread recipe"} """, Context(AiSeoSection.MetaDescription)),
        };

        var result = WithContext(sections, context);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, result.Failure!.Category);
        Assert.True(result.Failure.IsCorrectableByReprompt);
    }

    [Fact]
    public void A_model_label_in_model_text_is_refused_and_a_warning_leaves_room_for_the_label()
    {
        Assert.Equal(AiOutputReason.DomainInvalid, Validate("", ",\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"[model] trust me\"}]").Failure!.ReasonCode);

        var longest = new string('w', AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length);
        Assert.True(Validate("", $",\"warnings\":[{{\"kind\":\"Limitation\",\"message\":\"{longest}\"}}]").Succeeded);
        Assert.Equal(AiOutputReason.ValueTooLong, Validate("", $",\"warnings\":[{{\"kind\":\"Limitation\",\"message\":\"{longest}x\"}}]").Failure!.ReasonCode);
    }
}
