using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The long-form export held to files (LF.5.1): one accepted revision as Markdown and as HTML, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The golden files are the contract.</strong> They live beside this test in
/// <c>Golden/LongFormExport/</c>, a <c>.md</c> and a <c>.html</c> per document, and are compared exactly. Run
/// with <c>UPDATE_LONG_FORM_EXPORT_GOLDEN=1</c> to rewrite them, and read the diff before committing it: a
/// changed byte is a changed download for every creator — including one that only a Markdig upgrade moved.
/// </para>
/// <para>
/// No database and no clock. The exporter is a pure function of its input.
/// </para>
/// </remarks>
public sealed partial class LongFormExporterTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Third = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Fourth = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Fifth = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly string[] Names = ["complete", "minimal", "issue", "special-characters", "heading-injection"];

    public static TheoryData<string, LongFormExportFormat> Documents
    {
        get
        {
            var data = new TheoryData<string, LongFormExportFormat>();

            foreach (var name in Names)
            {
                data.Add(name, LongFormExportFormat.Markdown);
                data.Add(name, LongFormExportFormat.Html);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_document_is_exported_exactly_as_its_golden_file(string name, LongFormExportFormat format)
    {
        var content = Export(Named(name), format);
        var path = GoldenPath(name, format);

        if (Environment.GetEnvironmentVariable("UPDATE_LONG_FORM_EXPORT_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Assert.True(File.Exists(path), $"No golden file at {path}. Run once with UPDATE_LONG_FORM_EXPORT_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path), content);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void The_same_input_always_gives_the_same_bytes_with_lf_newlines_and_one_newline_at_the_end(
        string name, LongFormExportFormat format)
    {
        var input = Named(name);
        var first = Export(input, format);

        // Through storage and back, as a revision is: the projection is of what is stored.
        var second = Export(input with { Document = LongFormDocument.Deserialize(input.Document.Serialize()) }, format);

        Assert.Equal(first, second);
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Exporting_changes_nothing_about_the_document_it_was_given(string name, LongFormExportFormat format)
    {
        var input = Named(name);
        var before = input.Document.Serialize();

        LongFormExporter.Export(input, format);

        Assert.Equal(before, input.Document.Serialize());
    }

    // ---- What may be exported ----

    [Theory]
    [InlineData(LongFormDraftStatus.Draft)]
    [InlineData(LongFormDraftStatus.Outlined)]
    [InlineData(LongFormDraftStatus.Writing)]
    [InlineData(LongFormDraftStatus.Assembled)]
    public void A_draft_that_has_not_been_accepted_is_refused_in_either_format(LongFormDraftStatus status)
    {
        foreach (var format in Enum.GetValues<LongFormExportFormat>())
        {
            // Whole and written, and still not the article of record.
            var report = LongFormExporter.Export(Named("complete") with { Status = status }, format);

            Assert.Null(report.Content);
            Assert.Null(report.Title);
            Assert.Equal(ContentErrorCodes.LongFormExportNotAccepted, Assert.Single(report.Refusals).Code);
        }
    }

    [Fact]
    public void An_accepted_piece_whose_sources_have_moved_is_exported_as_it_was_accepted_and_said_to_be()
    {
        foreach (var format in Enum.GetValues<LongFormExportFormat>())
        {
            var accepted = LongFormExporter.Export(Named("complete"), format);
            var stale = LongFormExporter.Export(Named("complete") with { Status = LongFormDraftStatus.NeedsReview }, format);

            // The same bytes: what moved is said beside the document, never in it.
            Assert.Equal(accepted.Content, stale.Content);
            Assert.Empty(accepted.Warnings);
            Assert.Equal(LongFormExportCodes.SourcesMoved, Assert.Single(stale.Warnings).Code);
        }
    }

    [Fact]
    public void A_revision_with_nothing_written_is_refused()
    {
        var input = Named("minimal") with
        {
            Document = new LongFormDocument([Section(First, LongFormSectionRole.Hook, "The quick loaf", body: null)]),
        };

        var report = LongFormExporter.Export(input, LongFormExportFormat.Markdown);

        Assert.Null(report.Content);
        Assert.Equal(ContentErrorCodes.LongFormExportNothingWritten, Assert.Single(report.Refusals).Code);
    }

    [Fact]
    public void An_unwritten_section_is_left_out_of_both_documents_and_reported_beside_them()
    {
        var input = Named("minimal") with
        {
            Document = new LongFormDocument(
            [
                Section(First, LongFormSectionRole.Hook, "The quick loaf", "Some breads ask for an afternoon."),
                Section(Second, LongFormSectionRole.Tips, "Three things that help", body: null),
            ]),
        };

        foreach (var format in Enum.GetValues<LongFormExportFormat>())
        {
            var report = LongFormExporter.Export(input, format);

            Assert.DoesNotContain("Three things that help", report.Content, StringComparison.Ordinal);
            Assert.Equal(LongFormExportCodes.SectionsUnwritten, Assert.Single(report.Warnings).Code);
        }
    }

    // ---- The Markdown ----

    [Theory]
    [InlineData("complete")]
    [InlineData("issue")]
    [InlineData("special-characters")]
    [InlineData("heading-injection")]
    public void The_markdown_carries_the_sections_byte_for_byte_as_the_creator_accepted_them(string name)
    {
        var input = Named(name);
        var accepted = LongFormDocumentAssembler.Assemble(input.Kind, input.Document).Markdown;

        Assert.Contains(accepted + "\n---\n\n", Export(input, LongFormExportFormat.Markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_that_tries_to_be_structure_is_one_inert_line_of_markdown()
    {
        var markdown = Export(Named("heading-injection"), LongFormExportFormat.Markdown);
        var lines = markdown.Split('\n');

        // One title, the four sections the plan has, and no heading a creator's heading text smuggled in.
        Assert.Single(lines, line => line.StartsWith("# ", StringComparison.Ordinal));
        Assert.Equal(4, lines.Count(line => line.StartsWith("## ", StringComparison.Ordinal)));

        // Every bracket, angle and star a heading carried is escaped, so none opens a link, a tag or emphasis.
        foreach (var heading in lines.Where(line => line.StartsWith('#')))
        {
            Assert.DoesNotMatch(@"(?<!\\)[\[\]<>*`]", heading);
        }

        // One rule: the attribution's. The heading that was three dashes did not become a second.
        Assert.Single(lines, line => line == "---");
    }

    // ---- The HTML ----

    [Theory]
    [InlineData("complete")]
    [InlineData("issue")]
    [InlineData("special-characters")]
    [InlineData("heading-injection")]
    public void The_html_holds_only_the_elements_the_export_writes_and_links_only_where_a_reader_may_be_sent(string name)
    {
        var html = Export(Named(name), LongFormExportFormat.Html);

        string[] allowed =
        [
            "html", "head", "meta", "title", "body", "article", "section", "footer", "div",
            "h1", "h2", "h3", "h4", "h5", "h6", "p", "ul", "ol", "li", "blockquote", "pre", "code",
            "em", "strong", "a", "img", "br", "hr",
        ];

        // Whatever a creator typed, the only tags in the document are ones the export itself writes.
        Assert.All(ElementName().Matches(html), match => Assert.Contains(match.Groups[1].Value, allowed));

        Assert.All(Address().Matches(html), match =>
        {
            var schemes = match.Groups[1].Value == "src" ? new[] { "https://", "http://" } : ["https://", "http://", "mailto:"];

            Assert.Contains(schemes, scheme => match.Groups[2].Value.StartsWith(scheme, StringComparison.Ordinal));
        });

        // No handler, no style, no class: a quote in a creator's text is escaped, so every attribute found
        // is one the export wrote.
        Assert.Single(Attribute().Matches(html), match => match.Groups[1].Value == "charset");
        Assert.All(Attribute().Matches(html), match => Assert.Contains(match.Groups[1].Value, new[] { "charset", "href", "src", "alt", "title" }));
    }

    [Fact]
    public void Markdown_html_in_a_body_is_written_as_its_text_and_an_unsafe_link_as_the_words_it_wrapped()
    {
        var html = Export(Named("special-characters"), LongFormExportFormat.Html);

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;bold by hand&lt;/b&gt;", html, StringComparison.Ordinal);

        // The words stay; the address does not.
        Assert.Contains("a script link", html, StringComparison.Ordinal);
        Assert.Contains("a relative link", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=\"data", html, StringComparison.Ordinal);

        // And a link a reader may follow is still one.
        Assert.Contains("<a href=\"https://example.test/flour\">the flour I use</a>", html, StringComparison.Ordinal);
        Assert.Contains("<img src=\"https://example.test/loaf.jpg\" alt=\"The loaf\" />", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_inside_a_body_sits_under_its_sections_heading()
    {
        var input = Named("minimal") with
        {
            Document = new LongFormDocument(
            [
                Section(First, LongFormSectionRole.Hook, "The section", "# One\n\n## Two\n\n### Three\n\n###### Six\n\nSetext\n==="),
            ]),
        };

        var html = Export(input, LongFormExportFormat.Html);

        Assert.Contains("<h2>The section</h2>\n<h3>One</h3>\n<h3>Two</h3>\n<h3>Three</h3>\n<h6>Six</h6>\n<h3>Setext</h3>\n", html, StringComparison.Ordinal);
        Assert.Single(H2().Matches(html));
        Assert.DoesNotContain("<h1>", html, StringComparison.Ordinal);
    }

    // ---- The title and the attribution ----

    [Fact]
    public void An_issue_is_titled_by_its_subject_then_by_the_working_title_and_an_article_never_by_a_subject()
    {
        var issue = Named("issue");

        Assert.Equal("Three loaves for a cold week", LongFormExporter.Export(issue, LongFormExportFormat.Html).Title);

        var noSubject = issue with { Document = issue.Document with { Framing = new LongFormIssueFraming("  ", issue.Document.Framing!.Note) } };
        Assert.Equal("Week 42 issue", LongFormExporter.Export(noSubject, LongFormExportFormat.Html).Title);

        // A subject on a document that is not an issue is not this document's title, and its note is not in it.
        var article = Named("complete") with { Document = Named("complete").Document with { Framing = issue.Document.Framing } };
        var report = LongFormExporter.Export(article, LongFormExportFormat.Markdown);
        Assert.Equal("Buttermilk soda bread, start to finish", report.Title);
        Assert.DoesNotContain("that", report.Content!.Split("## ")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_piece_with_no_title_has_none_rather_than_an_invented_one()
    {
        var markdown = LongFormExporter.Export(Named("minimal"), LongFormExportFormat.Markdown);
        var html = LongFormExporter.Export(Named("minimal"), LongFormExportFormat.Html);

        Assert.Null(markdown.Title);
        Assert.StartsWith("## One section\n", markdown.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", html.Content, StringComparison.Ordinal);

        // A page must have a title element; it names the kind of thing this is, and nothing a creator wrote.
        Assert.Contains("<title>Blog article</title>", html.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_attribution_names_the_revision_and_every_pinned_recipe_version_in_pin_order()
    {
        var markdown = Export(Named("issue"), LongFormExportFormat.Markdown);

        Assert.EndsWith(
            "\n---\n\nExported from CreatorPantry · Newsletter issue · Revision 4\n\nWritten from:\n\n"
            + "- Buttermilk Soda Bread · Version 3\n- Brown Butter Loaf · Version 1\n- A recipe that is no longer available\n",
            markdown,
            StringComparison.Ordinal);

        var report = LongFormExporter.Export(Named("issue"), LongFormExportFormat.Markdown);
        Assert.Contains(report.Warnings, warning => warning.Code == LongFormExportCodes.RecipeUnavailable);

        // A piece written from no recipe says where it came from and names none.
        Assert.EndsWith(
            "\n---\n\nExported from CreatorPantry · Blog article · Revision 1\n",
            Export(Named("minimal"), LongFormExportFormat.Markdown),
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void The_document_says_nothing_of_who_wrote_which_words_and_carries_no_date(string name, LongFormExportFormat format)
    {
        var content = Export(Named(name), format);

        foreach (var label in new[] { "generated", "editorial", "proposal", "2026", "GMT", "UTC" })
        {
            Assert.DoesNotContain(label, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- The recipe export ----

    /// <summary>
    /// LF.5.1 adds a second export and must not move the first. The recipe export's own tests hold its output
    /// to these files; this holds the files, so regenerating one is a change a reviewer is shown here too.
    /// </summary>
    [Theory]
    [InlineData("alternate-unit-metric", "5603d04bbd3bc975a4ae352676559891f1fee6347f4be7ddbf6b9108983aabe9")]
    [InlineData("alternate-unit-us", "166600414470f4972a12ee558d72064901ea29d31f11f4004bcb6ac1da541792")]
    [InlineData("compact", "b6ee085afe9ed38d744591f14b64e271a298e6a7d19d443538978c75c9b5fe40")]
    [InlineData("complete", "9332857ffb92fc4161db2c429a2999664ac5ad84869e8cfc14ef52ba310fbe7c")]
    [InlineData("minimal", "864dc674a6450927743c3303e956d09be8ac5e159dff60cb1f28605d22364295")]
    [InlineData("special-characters", "adc1c38e108e91044a044454de0c28477d6120ed1985ad3b75fc3d956598b0d8")]
    public void The_recipe_markdown_exports_bytes_have_not_moved(string name, string sha256)
    {
        var path = Path.Combine(SourceDirectory(), "..", "Recipes", "MarkdownGolden", name + ".golden.md");

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    // ---- The documents ----

    private static LongFormExportInput Named(string name) => name switch
    {
        "complete" => new(
            LongFormKind.BlogArticle,
            LongFormDraftStatus.Accepted,
            RevisionNumber: 7,
            Title: "Buttermilk soda bread, start to finish",
            new LongFormDocument(
            [
                Section(First, LongFormSectionRole.Hook, "The loaf for people who do not bake",
                    "Some breads ask for a whole afternoon. This one asks for a bowl, a spoon and an oven that is already hot."),
                Section(Second, LongFormSectionRole.MethodNarrative, "From bowl to oven",
                    "Mix the dry ingredients first.\n\nStir in the buttermilk until the dough *only just* comes together.  \nThen bake for 45 minutes.\n\n"
                    + "### If the top browns too fast\n\nCover it loosely and carry on.\n\n![The loaf](https://example.test/loaf.jpg)"),
                Section(Third, LongFormSectionRole.Tips, "Three things that help",
                    "- Stir until the dough **only just** holds.\n- Have the oven hot first.\n- Use [the flour I use](https://example.test/flour).\n\n"
                    + "> My grandmother's rule: a wet dough and a hot oven.\n\n1. Weigh.\n2. Mix.\n3. Bake."),
                Section(Fourth, LongFormSectionRole.Closing, "Bake it this weekend",
                    "That is the whole of it. Write to <hello@example.test> and tell me how it went."),
            ]),
            [new("Buttermilk Soda Bread", 3)]),

        "minimal" => new(
            LongFormKind.BlogArticle,
            LongFormDraftStatus.Accepted,
            RevisionNumber: 1,
            Title: null,
            new LongFormDocument([Section(First, LongFormSectionRole.Hook, "One section", "One line.")]),
            []),

        "issue" => new(
            LongFormKind.NewsletterIssue,
            LongFormDraftStatus.Accepted,
            RevisionNumber: 4,
            Title: "Week 42 issue",
            new LongFormDocument(
                [
                    Section(First, LongFormSectionRole.Greeting, "Hello again", "It turned cold on Tuesday, and I turned the oven on."),
                    Section(Second, LongFormSectionRole.Item, "The soda bread", "The one I make when there is no time.", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                    Section(Third, LongFormSectionRole.Item, "A brown butter loaf", "Slower, and worth it.\n\n- Brown the butter properly.\n- Let it cool.", Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
                    Section(Fourth, LongFormSectionRole.Item, "The third loaf", "One from the archive.", Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
                    Section(Fifth, LongFormSectionRole.Signoff, "Until next week", "Stay warm.\n\nMaeve"),
                ],
                new LongFormIssueFraming(
                    "Three loaves for a cold week",
                    "\r\nA short one this week.\r\n\r\nIt has been *that* kind of week.\r\n\r\n")),
            [new("Buttermilk Soda Bread", 3), new("Brown Butter Loaf", 1), null]),

        "special-characters" => new(
            LongFormKind.BlogArticle,
            LongFormDraftStatus.Accepted,
            RevisionNumber: 12,
            Title: "  Crème brûlée & \"friends\" — <b>it's</b> 🍞\tfor\r\ntwo  ",
            new LongFormDocument(
            [
                Section(First, LongFormSectionRole.Hook, "# Not a heading, a *title* with [a link](https://example.test) & <b>tags</b>",
                    "\r\n\r\nA body that arrives with Windows line endings.\r\nAnd a second line, kept with a break.  \r\nThen a third.\r\n\r\n\r\n"),
                Section(Second, LongFormSectionRole.MethodNarrative, "1. Step one | step two",
                    "Raw HTML is the creator's in Markdown and text in HTML: <script>alert(1)</script> and <b>bold by hand</b>.\n\n"
                    + "<div onclick=\"alert(1)\">\na block of it\n</div>\n\n"
                    + "[a script link](javascript:alert(1)), [a relative link](/about), [the flour I use](https://example.test/flour), "
                    + "<javascript:alert(2)> and <https://example.test/auto>.\n\n"
                    + "![The loaf](https://example.test/loaf.jpg) ![embedded](data:image/png;base64,AAAA) ![local](loaf.jpg)\n\n"
                    + "An entity &lt;em&gt; and an ampersand & a \"quote\"."),
                Section(Third, LongFormSectionRole.Tips, "Crème brûlée — 🍞\tfor\ntwo",
                    "Unicode stays as it is: crème brûlée, 45 °C, ½ cup, 🍞.\n\n    Indented, and left as code."),
                Section(Fourth, LongFormSectionRole.Closing, "- dashed = and equals",
                    "Trailing spaces and blank lines at the end go.   \n\n\n"),
            ]),
            [new("Crème <b>brûlée</b> & *stars*\n# and a second line", 2), new("  ", 1)]),

        "heading-injection" => new(
            LongFormKind.BlogArticle,
            LongFormDraftStatus.Accepted,
            RevisionNumber: 2,
            Title: "# A title [with a link](javascript:alert(1))\n\n## and a second heading",
            new LongFormDocument(
            [
                Section(First, LongFormSectionRole.Hook, "Tips](https://evil.test)\n\n# Injected title\n\n<script>alert(1)</script>",
                    "The body under a heading that tried to end itself."),
                Section(Second, LongFormSectionRole.Context, "![pixel](https://evil.test/pixel.png) and `code` and **bold**",
                    "The body under a heading that tried to be a picture."),
                Section(Third, LongFormSectionRole.Tips, "</h2><img src=x onerror=alert(1)><h2>",
                    "The body under a heading that tried to close its own tag."),
                Section(Fourth, LongFormSectionRole.Closing, "---",
                    "The body under a heading that tried to be a rule."),
            ]),
            [new("Soda Bread](https://evil.test) <script>", 1)]),

        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string Export(LongFormExportInput input, LongFormExportFormat format)
    {
        var report = LongFormExporter.Export(input, format);

        Assert.Empty(report.Refusals);

        return Assert.IsType<string>(report.Content);
    }

    private static LongFormSection Section(
        Guid id, LongFormSectionRole role, string heading, string? body, Guid? source = null) => new(
        id,
        role,
        heading,
        "What this section is for.",
        body,
        Settled: false,
        source,
        string.IsNullOrWhiteSpace(body)
            ? null
            : new LongFormSectionProvenance(
                ContentRevisionSource.AiGenerated,
                Guid.Parse("99999999-9999-9999-9999-999999999999"),
                "content.long-form-section",
                "1.0.0",
                "sha256:abc",
                ReusedEditorialRevisionId: null,
                WrittenInRevisionNumber: 2,
                ["tips"]));

    private static string GoldenPath(string name, LongFormExportFormat format) => Path.Combine(
        SourceDirectory(), "Golden", "LongFormExport", name + (format is LongFormExportFormat.Html ? ".html" : ".md"));

    private static string SourceDirectory([CallerFilePath] string callerPath = "") => Path.GetDirectoryName(callerPath)!;

    [GeneratedRegex(@"</?([A-Za-z][A-Za-z0-9]*)")]
    private static partial Regex ElementName();

    [GeneratedRegex(@"\b(href|src)=""([^""]*)""")]
    private static partial Regex Address();

    [GeneratedRegex(@"\s([A-Za-z-]+)=""")]
    private static partial Regex Attribute();

    [GeneratedRegex("<h2>")]
    private static partial Regex H2();
}
