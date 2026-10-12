using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The long-form assembler held to files (LF.3.4): a revision's sections rendered into one Markdown document,
/// byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The golden files are the contract.</strong> They live beside this test in <c>Golden/LongForm/</c>
/// and are compared exactly, line endings included. Run with <c>UPDATE_LONG_FORM_GOLDEN=1</c> to rewrite them,
/// and read the diff before committing it: a changed byte is a changed document for every creator.
/// </para>
/// <para>
/// No database and no clock. The assembler is a pure function of a document, which is the point.
/// </para>
/// </remarks>
public sealed class LongFormDocumentAssemblerTests
{
    private static readonly Guid Hook = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Method = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Tips = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Closing = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static TheoryData<string> Documents => ["complete", "minimal", "reused-editorial", "special-characters"];

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_document_is_rendered_exactly_as_its_golden_file(string name)
    {
        var markdown = LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, Named(name)).Markdown;
        var path = GoldenPath(name);

        if (Environment.GetEnvironmentVariable("UPDATE_LONG_FORM_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, markdown);
        }

        Assert.True(File.Exists(path), $"No golden file at {path}. Run once with UPDATE_LONG_FORM_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path), markdown);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void The_same_document_always_gives_the_same_bytes_with_one_kind_of_line_ending_and_one_newline_at_the_end(string name)
    {
        var first = LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, Named(name)).Markdown;

        // Through storage and back, as a revision is: the projection is of what is stored.
        var second = LongFormDocumentAssembler.Assemble(
            LongFormKind.BlogArticle, LongFormDocument.Deserialize(Named(name).Serialize())).Markdown;

        Assert.Equal(first, second);
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void The_document_carries_no_label_for_who_wrote_what_and_reports_it_beside_the_text_instead()
    {
        var assembled = LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, Named("reused-editorial"));

        // The article is the creator's. Nothing in it says "generated", "AI", "editorial" or names a source.
        foreach (var label in new[] { "editorial", "generated", "AI", "accepted", "proposal" })
        {
            Assert.DoesNotContain(label, assembled.Markdown, StringComparison.OrdinalIgnoreCase);
        }

        var tips = assembled.Sections.Single(section => section.Id == Tips);
        Assert.Equal(["tips"], tips.Provenance!.ReusedEditorialParts);
        Assert.Equal(ContentRevisionSource.AiGenerated, tips.Provenance.Source);
        Assert.Null(assembled.Sections.Single(section => section.Id == Hook).Provenance!.ReusedEditorialParts);
    }

    [Fact]
    public void An_unwritten_section_is_left_out_of_the_text_and_kept_in_the_list()
    {
        var document = new LongFormDocument(
        [
            Section(Hook, LongFormSectionRole.Hook, "The quick loaf", "Some breads ask for an afternoon."),
            Section(Tips, LongFormSectionRole.Tips, "Three things that help", body: null),
            Section(Closing, LongFormSectionRole.Closing, "Bake it this weekend", "   \n  "),
        ]);

        var assembled = LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, document);

        Assert.Equal("## The quick loaf\n\nSome breads ask for an afternoon.\n", assembled.Markdown);
        Assert.Equal([true, false, false], assembled.Sections.Select(section => section.IsWritten));
        Assert.All(assembled.Sections.Skip(1), section =>
        {
            Assert.Null(section.Body);
            Assert.Equal(0, section.CharacterCount);
        });

        // Nothing is said about the length of a piece that is not all there.
        Assert.Equal(LongFormWordTargetStatus.NotAssessed, assembled.WordTarget);

        var issue = Assert.Single(LongFormDocumentBusiness.Issues(assembled));
        Assert.Equal(LongFormAcceptanceIssue.UnwrittenSections, issue.Issue);
        Assert.Equal([Tips, Closing], issue.SectionIds);
    }

    [Fact]
    public void A_document_with_nothing_written_is_empty_and_not_a_heading_list()
    {
        var assembled = LongFormDocumentAssembler.Assemble(
            LongFormKind.BlogArticle,
            new LongFormDocument([Section(Hook, LongFormSectionRole.Hook, "The quick loaf", body: null)]));

        Assert.Equal(string.Empty, assembled.Markdown);
        Assert.Equal(0, assembled.WordCount);
    }

    [Fact]
    public void A_section_over_its_ceiling_is_rendered_whole_and_named_as_something_to_confirm()
    {
        var longBody = string.Join(' ', Enumerable.Repeat("buttermilk", 400));
        Assert.True(longBody.Length > LongFormPolicy.SectionMaxCharacters);

        var assembled = LongFormDocumentAssembler.Assemble(
            LongFormKind.BlogArticle,
            new LongFormDocument(
            [
                Section(Hook, LongFormSectionRole.Hook, "The quick loaf", "Short."),
                Section(Method, LongFormSectionRole.MethodNarrative, "From bowl to oven", longBody),
            ]));

        // Reported, never trimmed.
        Assert.Contains(longBody, assembled.Markdown, StringComparison.Ordinal);
        Assert.True(assembled.Sections[1].OverCeiling);
        Assert.False(assembled.Sections[0].OverCeiling);
        Assert.False(assembled.DocumentOverCeiling);

        var issue = Assert.Single(LongFormDocumentBusiness.Issues(assembled));
        Assert.Equal(LongFormAcceptanceIssue.OverCeiling, issue.Issue);
        Assert.Equal([Method], issue.SectionIds);
    }

    [Fact]
    public void A_whole_and_modest_document_has_nothing_to_confirm_and_its_words_are_counted()
    {
        var assembled = LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, Named("complete"));

        Assert.Empty(LongFormDocumentBusiness.Issues(assembled));
        Assert.True(assembled.WordCount > 0);
        Assert.Equal(assembled.Sections.Sum(section => section.CharacterCount), assembled.CharacterCount);

        // Short for an article, and said to be: a fact about the piece, not something that stops it.
        Assert.Equal(LongFormWordTargetStatus.Under, assembled.WordTarget);
    }

    [Fact]
    public void A_piece_can_be_too_long_as_a_whole_with_no_one_section_to_blame()
    {
        // Seven sections just inside their own ceiling, and together past the document's.
        var body = new string('a', LongFormPolicy.SectionMaxCharacters - 10);
        var roles = new[]
        {
            LongFormSectionRole.Hook, LongFormSectionRole.Context, LongFormSectionRole.WhyItWorks,
            LongFormSectionRole.IngredientNotes, LongFormSectionRole.MethodNarrative, LongFormSectionRole.Tips,
            LongFormSectionRole.Closing,
        };

        var assembled = LongFormDocumentAssembler.Assemble(
            LongFormKind.BlogArticle,
            new LongFormDocument([.. roles.Select(role => Section(Guid.NewGuid(), role, role.ToString(), body))]));

        Assert.True(assembled.CharacterCount > LongFormPolicy.DocumentMaxCharacters);
        Assert.True(assembled.DocumentOverCeiling);
        Assert.All(assembled.Sections, section => Assert.False(section.OverCeiling));

        var issue = Assert.Single(LongFormDocumentBusiness.Issues(assembled));
        Assert.Equal(LongFormAcceptanceIssue.OverCeiling, issue.Issue);
        Assert.Empty(issue.SectionIds);
    }

    [Fact]
    public void A_body_is_the_creators_markdown_and_is_written_as_it_stands_even_when_it_looks_like_structure()
    {
        const string body = "## A heading of the creator's own\n\n- a list\n\n[a link](https://example.test) and <b>html</b>";

        var assembled = LongFormDocumentAssembler.Assemble(
            LongFormKind.BlogArticle,
            new LongFormDocument([Section(Hook, LongFormSectionRole.Hook, "## The heading", body)]));

        // The heading is made inert; the body is not touched.
        Assert.Equal("## \\## The heading\n\n" + body + "\n", assembled.Markdown);
    }

    [Fact]
    public void A_revision_stored_before_attribution_existed_still_reads_and_reports_none()
    {
        // The shape a section's provenance had when LF.3.3 stored it: no reusedEditorialParts at all.
        const string stored = """
            {"sections":[{"id":"11111111-1111-1111-1111-111111111111","role":1,"heading":"The quick loaf","intent":"Open.",
            "body":"Some breads ask for an afternoon.","settled":false,"sourceReferenceId":null,
            "provenance":{"source":0,"aiProposalId":"99999999-9999-9999-9999-999999999999","promptTemplateId":"content.long-form-section",
            "promptTemplateVersion":"1.0.0","promptTemplateBodyChecksum":"sha256:abc","reusedEditorialRevisionId":null,"writtenInRevisionNumber":2}}]}
            """;

        var assembled = LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, LongFormDocument.Deserialize(stored));

        Assert.Equal("## The quick loaf\n\nSome breads ask for an afternoon.\n", assembled.Markdown);
        Assert.Null(assembled.Sections[0].Provenance!.ReusedEditorialParts);
        Assert.Equal(ContentRevisionSource.AiGenerated, assembled.Sections[0].Provenance.Source);
    }

    [Fact]
    public void Assembling_changes_nothing_about_the_document_it_was_given()
    {
        var document = Named("special-characters");
        var before = document.Serialize();

        LongFormDocumentAssembler.Assemble(LongFormKind.BlogArticle, document);

        Assert.Equal(before, document.Serialize());
    }

    /// <summary>
    /// The recipe export's escaper lives in another module and cannot be called from the content module, so
    /// its rule is restated there. This is what stops the two drifting apart.
    /// </summary>
    [Theory]
    [InlineData("Plain heading")]
    [InlineData("# Not a heading")]
    [InlineData("- not a bullet")]
    [InlineData("1. not a list")]
    [InlineData("12) nor this")]
    [InlineData("=== not a rule")]
    [InlineData("[a link](https://example.test) and <b>html</b>")]
    [InlineData("*stars* _under_ `ticks` ~tilde~ a|b & more \\ slash")]
    [InlineData("  spaced\tout\r\nacross\nlines  ")]
    [InlineData("Crème brûlée — 🍞 for two")]
    [InlineData("")]
    public void A_heading_is_escaped_exactly_as_the_recipe_export_escapes_a_line(string heading)
    {
        Assert.Equal(RecipeMarkdownEscaper.Line(heading), LongFormMarkdownEscaper.Line(heading));
    }

    private static LongFormDocument Named(string name) => name switch
    {
        "complete" => new LongFormDocument(
        [
            Section(Hook, LongFormSectionRole.Hook, "The loaf for people who do not bake",
                "Some breads ask for a whole afternoon. This one asks for a bowl, a spoon and an oven that is already hot."),
            Section(Method, LongFormSectionRole.MethodNarrative, "From bowl to oven",
                "Mix the dry ingredients first.\n\nStir in the buttermilk until the dough only just comes together. Then bake for 45 minutes."),
            Section(Tips, LongFormSectionRole.Tips, "Three things that help",
                "- Stir until the dough **only just** holds.\n- Have the oven hot first.\n- Use a wooden spoon."),
            Section(Closing, LongFormSectionRole.Closing, "Bake it this weekend",
                "That is the whole of it. Go and make some bread.", settled: true, source: ContentRevisionSource.CreatorEdit),
        ]),

        "minimal" => new LongFormDocument(
        [
            Section(Hook, LongFormSectionRole.Hook, "One section", "One line."),
        ]),

        "reused-editorial" => new LongFormDocument(
        [
            Section(Hook, LongFormSectionRole.Hook, "The bread I make when there is no time",
                "The bread I make when there is no time to make bread."),
            Section(Tips, LongFormSectionRole.Tips, "What I have learned",
                "- Do not overwork the dough.\n- Score the top deeply so the middle bakes through.",
                reused: ["tips"]),
        ]),

        "special-characters" => new LongFormDocument(
        [
            Section(Hook, LongFormSectionRole.Hook, "# Not a heading, a *title* with [a link](https://example.test) & <b>tags</b>",
                "\r\n\r\nA body that arrives with Windows line endings.\r\nAnd a second line, kept with a break.  \r\nThen a third.\r\n\r\n\r\n"),
            Section(Method, LongFormSectionRole.MethodNarrative, "1. Step one | step two",
                "Markdown in a body is the creator's and is kept: **bold**, _italic_, `code`, a > quote and 1) a number.\n\n    Indented, and left indented."),
            Section(Tips, LongFormSectionRole.Tips, "Crème brûlée — 🍞\tfor\ntwo",
                "Unicode stays as it is: crème brûlée, 45 °C, ½ cup, 🍞."),
            Section(Closing, LongFormSectionRole.Closing, "- dashed = and equals",
                "Trailing spaces and blank lines at the end go.   \n\n\n"),
        ]),

        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static LongFormSection Section(
        Guid id,
        LongFormSectionRole role,
        string heading,
        string? body,
        bool settled = false,
        ContentRevisionSource source = ContentRevisionSource.AiGenerated,
        IReadOnlyList<string>? reused = null) => new(
        id,
        role,
        heading,
        "What this section is for.",
        body,
        settled,
        SourceReferenceId: null,
        string.IsNullOrWhiteSpace(body)
            ? null
            : new LongFormSectionProvenance(
                source,
                source is ContentRevisionSource.AiGenerated ? Guid.Parse("99999999-9999-9999-9999-999999999999") : null,
                null,
                null,
                null,
                ReusedEditorialRevisionId: null,
                WrittenInRevisionNumber: 2,
                reused));

    private static string GoldenPath(string name, [CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "Golden", "LongForm", name + ".md");
}
