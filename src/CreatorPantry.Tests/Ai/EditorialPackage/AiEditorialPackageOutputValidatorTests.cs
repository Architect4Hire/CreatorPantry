using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// The output contract, directly. The same cases run as evaluation fixtures too; these pin the stable reason
/// codes a reader of the failure depends on.
/// </summary>
public sealed class AiEditorialPackageOutputValidatorTests
{
    private const string Version = "content.editorial-package.v1";

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument> Validate(string sections, string? extra = null) =>
        AiEditorialPackageOutputValidator.Validate(
            $$"""{"schemaVersion":"{{Version}}","sections":{{{sections}}}{{extra}}}""", Version);

    [Fact]
    public void A_full_package_and_an_empty_one_are_both_valid()
    {
        var line = Guid.NewGuid();
        var full = Validate($$"""
            "headnote":{"text":"A warm loaf."},"introduction":{"text":"Start here."},
            "tips":[{"text":"Rest the dough."}],
            "substitutions":[{"lineId":"{{line}}","suggestion":"Use oat milk.","culinaryNote":"Slightly sweeter."}],
            "storageReheating":{"text":"No storage guidance supplied."},
            "faq":[{"question":"Can I halve it?","answer":"The recipe does not say."}],
            "cta":{"text":"Save it for later."}
            """);

        Assert.True(full.Succeeded, full.Failure?.Message);
        Assert.Equal(7, AiEditorialSectionCatalog.Present(full.Document!.Sections).Count);
        Assert.True(Validate("").Succeeded);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{},"surprise":1}""", AiOutputReason.UnknownField)]
    [InlineData("""{"schemaVersion":"other.v1","sections":{}}""", AiOutputReason.SchemaVersionMismatch)]
    [InlineData("not json", AiOutputReason.MalformedJson)]
    [InlineData("", AiOutputReason.EmptyPayload)]
    public void A_payload_that_is_not_the_contract_is_rejected_with_a_stable_reason(string payload, string reason)
    {
        var result = AiEditorialPackageOutputValidator.Validate(payload, Version);

        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.Failure!.ReasonCode);
    }

    [Fact]
    public void Text_that_is_empty_too_long_marked_up_or_linked_is_rejected()
    {
        Assert.Equal(AiOutputReason.EditorialFieldMissing, Validate("\"headnote\":{\"text\":\"  \"}").Failure!.ReasonCode);
        Assert.Equal(AiOutputReason.ValueTooLong, Validate($"\"headnote\":{{\"text\":\"{new string('a', AiPolicy.EditorialTextMaxLength + 1)}\"}}").Failure!.ReasonCode);
        Assert.Equal(AiOutputReason.IllegalCharacters, Validate("\"headnote\":{\"text\":\"See <b>this</b>.\"}").Failure!.ReasonCode);
        Assert.Equal(AiOutputReason.IllegalCharacters, Validate("\"cta\":{\"text\":\"Visit https://example.com now.\"}").Failure!.ReasonCode);
    }

    [Fact]
    public void Too_many_items_and_repeated_items_are_rejected()
    {
        var tips = string.Join(',', Enumerable.Range(0, AiPolicy.MaxEditorialTips + 1).Select(i => $"{{\"text\":\"Tip {i}\"}}"));
        Assert.Equal(AiOutputReason.ValueTooLong, Validate($"\"tips\":[{tips}]").Failure!.ReasonCode);

        Assert.Equal(
            AiOutputReason.EditorialDuplicateItem,
            Validate("\"tips\":[{\"text\":\"Rest it.\"},{\"text\":\"rest it.\"}]").Failure!.ReasonCode);
    }

    [Fact]
    public void A_substitution_cannot_carry_a_safety_or_suitability_field()
    {
        var line = Guid.NewGuid();

        // The shape has nowhere to put an allergen or diet claim, so one is an unknown field, not a finding.
        var result = Validate($$"""
            "substitutions":[{"lineId":"{{line}}","suggestion":"Use almond flour.","culinaryNote":"Nuttier.","allergenFree":true}]
            """);

        Assert.Equal(AiOutputReason.UnknownField, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_warning_must_declare_its_kind()
    {
        var result = Validate("", ",\"warnings\":[{\"kind\":\"Unspecified\",\"message\":\"x\"}]");

        Assert.Equal(AiOutputReason.KindNotDeclared, result.Failure!.ReasonCode);
    }

    [Fact]
    public void The_exported_schema_names_the_sections_the_prompt_asks_for()
    {
        var schema = AiEditorialPackageOutputSchema.Json;

        foreach (var name in new[] { "headnote", "introduction", "tips", "substitutions", "storageReheating", "faq", "cta", "lineId", "culinaryNote" })
        {
            Assert.Contains(name, schema, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":null}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{"tips":null}}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{"faq":null}}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{},"warnings":null}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{"tips":[null]}}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{"faq":[null]}}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1","sections":{},"warnings":[null]}""")]
    public void A_null_where_the_schema_requires_a_value_is_rejected_not_thrown_on(string payload)
    {
        var result = AiEditorialPackageOutputValidator.Validate(payload, Version);

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.ShapeInvalid, result.Failure!.ReasonCode);
    }

    [Theory]
    [InlineData("\\u200B")]
    [InlineData("\\u200D")]
    [InlineData("\\uFEFF")]
    public void An_invisible_character_in_prose_is_rejected(string escape)
    {
        var result = Validate($"\"headnote\":{{\"text\":\"gluten{escape}free\"}}");

        Assert.Equal(AiOutputReason.IllegalCharacters, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_warning_that_imitates_a_server_finding_is_rejected()
    {
        var result = Validate("", ",\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"[editorial.unsupported_number] verified safe\"}]");

        Assert.Equal(AiOutputReason.DomainInvalid, result.Failure!.ReasonCode);
        Assert.Equal(AiFailureCategory.DomainInvalid, result.Failure.Category);
    }

    [Theory]
    [InlineData("Visit example.com/loaf")]
    [InlineData("Write to me@example.com")]
    [InlineData("Follow @samskitchen")]
    [InlineData("Tag it #bread")]
    [InlineData("Open ftp://files.example")]
    [InlineData("See mailto:sam@example.com")]
    public void Links_addresses_handles_and_hashtags_in_every_shape_are_rejected(string text)
    {
        var result = Validate($"\"cta\":{{\"text\":\"{text}\"}}");

        Assert.Equal(AiOutputReason.IllegalCharacters, result.Failure!.ReasonCode);
    }

    [Theory]
    [InlineData("Makes e.g. twelve rolls, i.e. plenty.")]
    [InlineData("Number 1 on my list, served at 5 o'clock.")]
    public void Ordinary_prose_with_abbreviations_and_numbers_is_not_mistaken_for_a_link(string text)
    {
        Assert.True(Validate($"\"cta\":{{\"text\":\"{text}\"}}").Succeeded);
    }
}
