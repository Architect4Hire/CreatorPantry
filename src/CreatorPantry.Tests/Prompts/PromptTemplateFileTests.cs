using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Prompts;

/// <summary>
/// What a template file has to get right to load at all. Each case is a few lines of inline text rather than a
/// fixture file, which is the reason <see cref="PromptTemplateFile.Parse"/> is public.
/// </summary>
public sealed class PromptTemplateFileTests
{
    private const string SoundBody = "Suggest a headnote for {{recipeTitle}}.";

    private const string ResourceName =
        "CreatorPantry.Tests.Prompts.Fixtures.fixture.headnote-1.0.0.prompt.md";

    [Fact]
    public void A_sound_file_parses_into_its_declared_contract()
    {
        var template = PromptTemplateFile.Parse(ResourceName, File());

        Assert.Equal("fixture.headnote", template.Id);
        Assert.Equal(new PromptTemplateVersion(1, 0, 0), template.Version);
        Assert.Equal("fixture.headnote.v1", template.OutputSchemaVersion);
        Assert.Equal(PromptSafetyClass.CulinaryAdvice, template.SafetyClass);
        Assert.Equal(SoundBody, template.Body);
        Assert.Equal(PromptTemplateFile.ComputeChecksum(SoundBody), template.BodyChecksum);

        var input = Assert.Single(template.Inputs);
        Assert.Equal("recipeTitle", input.Name);
        Assert.True(input.Required);
    }

    [Fact]
    public void A_body_edited_without_its_manifest_is_refused()
    {
        var reworded = "Suggest a punchier headnote for {{recipeTitle}}.";

        var failure = Assert.Throws<PromptTemplateException>(() => PromptTemplateFile.Parse(
            ResourceName,
            File(body: reworded, checksum: PromptTemplateFile.ComputeChecksum(SoundBody))));

        Assert.Contains("bodyChecksum", failure.Message, StringComparison.Ordinal);
        Assert.Contains("hashes to", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the whole checksum: a prompt body cannot be reworded without the manifest moving in the
    /// same diff, so a reviewer sees a prompt change as a prompt change.
    /// </summary>
    [Fact]
    public void Restating_the_checksum_is_what_makes_a_body_change_loadable()
    {
        var reworded = "Suggest a punchier headnote for {{recipeTitle}}.";

        var template = PromptTemplateFile.Parse(ResourceName, File(body: reworded));

        Assert.Equal(reworded, template.Body);
    }

    [Fact]
    public void A_missing_checksum_reports_the_value_it_should_have_been()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(omitChecksum: true)));

        Assert.Contains(PromptTemplateFile.ComputeChecksum(SoundBody), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Windows checkout and a Linux one must agree, or the checksum is a coin flip rather than a control.
    /// <c>.gitattributes</c> normalizes <c>*.md</c> to LF, and the loader normalizes again regardless.
    /// </summary>
    [Fact]
    public void Line_endings_and_trailing_whitespace_do_not_change_the_checksum()
    {
        Assert.Equal(
            PromptTemplateFile.ComputeChecksum("First line.\nSecond line with {{recipeTitle}}."),
            PromptTemplateFile.ComputeChecksum("First line.\r\nSecond line with {{recipeTitle}}.\r\n\r\n"));
    }

    [Fact]
    public void A_crlf_file_parses_to_the_same_template_as_an_lf_one()
    {
        var lf = PromptTemplateFile.Parse(ResourceName, File());
        var crlf = PromptTemplateFile.Parse(ResourceName, File().ReplaceLineEndings("\r\n"));

        Assert.Equal(lf.Body, crlf.Body);
        Assert.Equal(lf.BodyChecksum, crlf.BodyChecksum);
    }

    [Fact]
    public void An_unknown_safety_class_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(safetyClass: "MildlyRisky")));

        Assert.Contains("manifest JSON", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Omitting the field must fail rather than mean the least restrictive class, which is why
    /// <see cref="PromptSafetyClass.Unspecified"/> occupies zero.
    /// </summary>
    [Fact]
    public void An_undeclared_safety_class_is_refused_rather_than_defaulted()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(safetyClass: null)));

        Assert.Contains("'safetyClass' is required", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("v1.0.0")]
    [InlineData(" 1.0.0")]
    [InlineData("-1.0.0")]
    public void A_version_that_is_not_major_minor_patch_is_refused(string version)
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(version: version)));

        Assert.Contains("major.minor.patch", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Recipe.Concepts")]
    [InlineData("recipe concepts")]
    [InlineData("recipe..concepts")]
    [InlineData(".recipe")]
    [InlineData("recipe_concepts")]
    public void An_id_that_is_not_lowercase_dotted_is_refused(string id)
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(id: id)));

        Assert.Contains("'id' must be", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_no_input_declares_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() => PromptTemplateFile.Parse(
            ResourceName,
            File(body: "Headnote for {{recipeTitle}} in {{brandVoice}}.")));

        Assert.Contains("brandVoice", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no input declares", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A declared input the body never reads is a rename that only half landed, and it would otherwise sit in
    /// the manifest looking like a live contract.
    /// </summary>
    [Fact]
    public void An_input_the_body_never_uses_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(body: "Suggest a headnote.")));

        Assert.Contains("recipeTitle", failure.Message, StringComparison.Ordinal);
        Assert.Contains("never used", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclosed_placeholder_is_refused_rather_than_sent_as_literal_text()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(body: "Suggest a headnote for {{recipeTitle.")));

        Assert.Contains("never closed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_with_whitespace_inside_the_braces_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(body: "Suggest a headnote for {{ recipeTitle }}.")));

        Assert.Contains("camelCase name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicated_input_declaration_is_refused()
    {
        var manifest = """
            {
              "id": "fixture.headnote",
              "version": "1.0.0",
              "outputSchemaVersion": "fixture.headnote.v1",
              "safetyClass": "CulinaryAdvice",
              "inputs": [
                { "name": "recipeTitle", "required": true },
                { "name": "recipeTitle", "required": false }
              ],
              "bodyChecksum": "sha256:irrelevant"
            }
            """;

        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, $"---\n{manifest}\n---\n{SoundBody}\n"));

        Assert.Contains("declared twice", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mistyped manifest property would otherwise be ignored, and the resulting complaint would be about a
    /// missing declaration rather than the typo sitting in front of the reader.
    /// </summary>
    [Fact]
    public void A_mistyped_manifest_property_is_refused()
    {
        var mistyped = File().Replace("safetyClass", "safteyClass", StringComparison.Ordinal);

        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, mistyped));

        Assert.Contains("manifest JSON", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_name_that_disagrees_with_its_manifest_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() => PromptTemplateFile.Parse(
            "CreatorPantry.Tests.Prompts.Fixtures.fixture.headnote-1.1.0.prompt.md",
            File()));

        Assert.Contains("fixture.headnote-1.0.0.prompt.md", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_front_matter_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, SoundBody));

        Assert.Contains("front-matter fence", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Front_matter_that_is_never_closed_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, "---\n{ \"id\": \"fixture.headnote\" }\n"));

        Assert.Contains("never closed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_body_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, File(body: "   ")));

        Assert.Contains("body is empty", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_front_matter_json_is_refused()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            PromptTemplateFile.Parse(ResourceName, $"---\n{{ \"id\": \n---\n{SoundBody}\n"));

        Assert.Contains("manifest JSON", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sound file, with one aspect swapped per case. The declared checksum defaults to the one
    /// <paramref name="body"/> actually hashes to, so a case about placeholders is not answered by a checksum
    /// mismatch instead; <paramref name="checksum"/> and <paramref name="omitChecksum"/> break that on purpose.
    /// </summary>
    private static string File(
        string id = "fixture.headnote",
        string version = "1.0.0",
        string? safetyClass = "CulinaryAdvice",
        string body = SoundBody,
        string? checksum = null,
        bool omitChecksum = false)
    {
        var lines = new List<string>
        {
            $"  \"id\": \"{id}\"",
            $"  \"version\": \"{version}\"",
            "  \"outputSchemaVersion\": \"fixture.headnote.v1\"",
            "  \"inputs\": [ { \"name\": \"recipeTitle\", \"required\": true } ]",
        };

        if (safetyClass is not null)
        {
            lines.Add($"  \"safetyClass\": \"{safetyClass}\"");
        }

        if (!omitChecksum)
        {
            lines.Add($"  \"bodyChecksum\": \"{checksum ?? PromptTemplateFile.ComputeChecksum(body)}\"");
        }

        return $"---\n{{\n{string.Join(",\n", lines)}\n}}\n---\n{body}\n";
    }
}
