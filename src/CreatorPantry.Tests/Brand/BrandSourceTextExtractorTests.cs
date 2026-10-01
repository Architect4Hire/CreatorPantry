using System.Text;
using CreatorPantry.Domain.Modules.Brand.Managers;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The format parsers: what each one makes of a file, and what each one refuses to claim.
/// </summary>
/// <remarks>
/// <para>
/// The artifact is one normalized plain-text form for every format, so most of these assert on the text that
/// comes out rather than on an intermediate shape: that is the contract, and it is what 11A.11's correction path
/// and 11A.12's chunker will both read.
/// </para>
/// <para>
/// <strong>The negative assertions carry as much weight as the positive ones.</strong> An image and a scanned PDF
/// must come back as review states that say no text recognition was performed, because the alternative — an empty
/// success, or invented words — is the silent OCR claim this module promises not to make.
/// </para>
/// </remarks>
public sealed class BrandSourceTextExtractorTests
{
    private static readonly Guid Unused = Guid.NewGuid();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_accepted_upload_format_has_a_reader()
    {
        // Not tidiness: a format the upload accepts and nothing reads leaves every version of it queued, run,
        // and failed with "no reader is registered" — a server fault reported as if the creator's file were at
        // fault. The two lists are allowed to be equal and nothing else.
        Assert.Equal(
            BrandSourceFileInspector.AcceptedMediaTypes.Order(StringComparer.Ordinal),
            BrandSourceTextExtractors.CoveredMediaTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_pdf_with_a_text_layer_comes_back_as_its_text()
    {
        var pdf = RenderPdf("House style", "Warm, plain, never breathless.");

        var extracted = await ExtractAsync(BrandSourceFileInspector.PdfMediaType, pdf);

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.Contains("House style", extracted.Text);
        Assert.Contains("Warm, plain, never breathless.", extracted.Text);
        Assert.Null(extracted.Reason);
        Assert.False(extracted.Truncated);
    }

    [Fact]
    public async Task A_pdf_line_break_does_not_become_a_paragraph_break()
    {
        // A PDF line break is where the glyphs ran out of page, not where the sentence ended. An artifact that
        // kept them would hand the chunker and every later reader a column of fragments.
        var sentence = string.Join(
            ' ',
            Enumerable.Repeat("Warm and plain and never once breathless about any of it.", 6));

        var extracted = await ExtractAsync(
            BrandSourceFileInspector.PdfMediaType, RenderPdf("Voice", sentence));

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.Contains("Warm and plain and never once breathless about any of it. Warm and plain", extracted.Text);
    }

    [Fact]
    public async Task A_pdf_with_no_text_layer_is_a_review_state_that_names_the_reason()
    {
        // Pages of nothing: the structural equivalent of a scan, which is the case that must never come back as
        // an empty success or as invented words.
        var scanned = RenderPdf(heading: null, body: null);

        var extracted = await ExtractAsync(BrandSourceFileInspector.PdfMediaType, scanned);

        Assert.Equal(BrandSourceTextExtractionOutcome.Unsupported, extracted.Outcome);
        Assert.Null(extracted.Text);
        Assert.Equal(BrandSourceExtractionReasons.NoTextLayer, extracted.Reason);
        Assert.Contains("No text recognition is performed", extracted.Reason);
    }

    [Fact]
    public async Task A_file_that_only_starts_like_a_pdf_is_unreadable()
    {
        // The sample the upload accepts on its header and trailer alone. It has no page tree, so a real parser
        // cannot open it — which is the corrupt-document path, distinct from a document with no text in it.
        var extracted = await ExtractAsync(BrandSourceFileInspector.PdfMediaType, BrandSourceSampleFiles.Pdf());

        Assert.Equal(BrandSourceTextExtractionOutcome.Unreadable, extracted.Outcome);
        Assert.Null(extracted.Text);
        Assert.NotNull(extracted.Reason);
    }

    [Fact]
    public async Task A_docx_keeps_its_headings_paragraphs_and_list_items()
    {
        var docx = BrandSourceSampleFiles.DocxBody(
            BrandSourceSampleFiles.DocxParagraph("House style", "Heading1")
                + BrandSourceSampleFiles.DocxParagraph("Warm, plain, never breathless.")
                + BrandSourceSampleFiles.DocxParagraph("Voice", "heading 2")
                + BrandSourceSampleFiles.DocxParagraph("Say the thing.", numbered: true)
                + BrandSourceSampleFiles.DocxParagraph("Then stop.", numbered: true));

        var extracted = await ExtractAsync(BrandSourceSampleFiles.DocxMediaType, docx);

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.Equal(
            "# House style\n\nWarm, plain, never breathless.\n\n## Voice\n\n- Say the thing.\n\n- Then stop.\n",
            extracted.Text);
    }

    [Fact]
    public async Task A_docx_field_code_is_not_mistaken_for_the_author_s_words()
    {
        // instrText carries a directive — INCLUDETEXT, HYPERLINK — not prose. Storing it would put instruction
        // text into an artifact this module later hands a model as source material.
        var docx = BrandSourceSampleFiles.DocxBody(
            "<w:p><w:r><w:instrText> INCLUDETEXT \"\\\\\\\\server\\\\share\\\\secrets.docx\" </w:instrText></w:r>"
                + "<w:r><w:t>Warm and plain.</w:t></w:r></w:p>");

        var extracted = await ExtractAsync(BrandSourceSampleFiles.DocxMediaType, docx);

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.Equal("Warm and plain.\n", extracted.Text);
        Assert.DoesNotContain("INCLUDETEXT", extracted.Text);
    }

    [Fact]
    public async Task A_docx_that_declares_a_document_type_is_refused_rather_than_resolved()
    {
        // The classic XML external-entity shape. The reader prohibits DTDs outright, so this is a refusal and
        // not a fetch — and the refusal is reported as a document that cannot be read rather than as a crash.
        var docx = BrandSourceSampleFiles.DocxWith(
            "<!DOCTYPE document [<!ENTITY secret SYSTEM \"file:///etc/passwd\">]>"
                + $"<w:document {BrandSourceSampleFiles.WordNamespaceDeclaration}><w:body>"
                + "<w:p><w:r><w:t>&secret;</w:t></w:r></w:p></w:body></w:document>");

        var extracted = await ExtractAsync(BrandSourceSampleFiles.DocxMediaType, docx);

        Assert.Equal(BrandSourceTextExtractionOutcome.Unreadable, extracted.Outcome);
        Assert.Null(extracted.Text);
    }

    [Fact]
    public async Task A_docx_carrying_a_macro_project_is_not_read_as_a_document()
    {
        // Unreachable through the upload, which refuses one. Asserted anyway: what makes "no macro is executed"
        // true is that no code path exists which could, not that an earlier gate was careful.
        var extracted = await ExtractAsync(
            BrandSourceSampleFiles.DocxMediaType, BrandSourceSampleFiles.Docx(withMacros: true));

        Assert.Equal(BrandSourceTextExtractionOutcome.Unreadable, extracted.Outcome);
        Assert.Contains("macro", extracted.Reason);
    }

    [Fact]
    public async Task A_zip_that_is_not_a_word_package_is_unreadable()
    {
        var extracted = await ExtractAsync(
            BrandSourceSampleFiles.DocxMediaType, BrandSourceSampleFiles.PlainZip());

        Assert.Equal(BrandSourceTextExtractionOutcome.Unreadable, extracted.Outcome);
    }

    [Fact]
    public async Task Markdown_passes_through_as_the_creator_wrote_it()
    {
        // Markdown is already the form this module's artifact is in, so the honest parse is almost no parse. A
        // numbered list and an indented continuation are the two things reflowing would destroy.
        var markdown = "# House style\r\n\r\n1. Say the thing.\r\n2. Then stop.\r\n\r\n- A bullet\r\n  - nested\r\n";

        var extracted = await ExtractAsync("text/markdown", Encoding.UTF8.GetBytes(markdown));

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.Equal(
            "# House style\n\n1. Say the thing.\n2. Then stop.\n\n- A bullet\n  - nested\n",
            extracted.Text);
    }

    [Fact]
    public async Task Plain_text_keeps_its_lines_and_collapses_blank_runs()
    {
        var text = "﻿Evergreen   \n\n\n\nPink\t\n\n\n";

        var extracted = await ExtractAsync("text/plain", Encoding.UTF8.GetBytes(text));

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);

        // The byte-order mark is gone, trailing whitespace is gone, three blank lines are one, and the trailing
        // run is none.
        Assert.Equal("Evergreen\n\nPink\n", extracted.Text);
    }

    [Fact]
    public async Task Text_with_invisible_characters_loses_them()
    {
        // Zero-width and bidirectional-override characters are invisible to the creator reading this back, and
        // are exactly what a trick played on a later reader is made of.
        var extracted = await ExtractAsync(
            "text/plain", Encoding.UTF8.GetBytes("Warm​and‮plain\n"));

        Assert.Equal("Warmandplain\n", extracted.Text);
    }

    [Fact]
    public async Task Html_keeps_the_words_and_the_headings_and_drops_the_markup()
    {
        var html = """
            <!doctype html><html><head><title>House style</title>
            <style>h1 { color: evergreen }</style>
            <script>alert('not text')</script></head>
            <body><h2>Voice</h2><p>Warm, <em>plain</em>, never breathless.</p>
            <!-- an aside nobody reads -->
            <ul><li>Say the thing</li><li>Then stop</li></ul>
            <p>Evergreen &amp; pink &lt;together&gt;</p></body></html>
            """;

        var extracted = await ExtractAsync("text/html", Encoding.UTF8.GetBytes(html));

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.Equal(
            "# House style\n\n## Voice\n\nWarm, plain , never breathless.\n\n- Say the thing\n\n- Then stop\n\n"
                + "Evergreen & pink <together>\n",
            extracted.Text);

        // The two things that must not survive: script and style contents, and the comment.
        Assert.DoesNotContain("alert", extracted.Text);
        Assert.DoesNotContain("color", extracted.Text);
        Assert.DoesNotContain("nobody reads", extracted.Text);
    }

    [Fact]
    public async Task Html_attribute_values_never_reach_the_artifact()
    {
        // A '>' inside a quoted attribute must not end the tag early, which is how attribute text leaks into
        // extracted output in a scanner that does not track quoting.
        var extracted = await ExtractAsync(
            "text/html",
            Encoding.UTF8.GetBytes("<p title=\"a > b secret\" data-x='c > d'>Warm and plain.</p>"));

        Assert.Equal("Warm and plain.\n", extracted.Text);
        Assert.DoesNotContain("secret", extracted.Text);
    }

    [Fact]
    public async Task An_html_entity_that_decodes_into_markup_is_not_rescanned()
    {
        // Decoding happens once, after the markup is gone, so these stay literal characters in a text/plain
        // artifact rather than becoming a tag a second pass would act on.
        var extracted = await ExtractAsync(
            "text/html", Encoding.UTF8.GetBytes("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>"));

        Assert.Equal("<script>alert(1)</script>\n", extracted.Text);
    }

    [Fact]
    public async Task An_html_page_with_an_unclosed_script_yields_nothing_further()
    {
        // The conservative answer: after an unclosed raw-text element there is no remaining text that can be
        // told apart from code.
        var extracted = await ExtractAsync(
            "text/html", Encoding.UTF8.GetBytes("<p>Kept</p><script>var a = 1; <p>Dropped</p>"));

        Assert.Equal("Kept\n", extracted.Text);
        Assert.DoesNotContain("Dropped", extracted.Text);
    }

    [Theory]
    [InlineData(BrandSourceFileInspector.PngMediaType)]
    [InlineData(BrandSourceFileInspector.JpegMediaType)]
    [InlineData(BrandSourceFileInspector.WebpMediaType)]
    public async Task An_image_is_a_review_state_that_promises_no_text_recognition(string mediaType)
    {
        var extracted = await ExtractAsync(mediaType, BrandSourceSampleFiles.Png());

        Assert.Equal(BrandSourceTextExtractionOutcome.Unsupported, extracted.Outcome);
        Assert.Null(extracted.Text);
        Assert.Equal(BrandSourceExtractionReasons.Image, extracted.Reason);
        Assert.Contains("No text recognition is performed", extracted.Reason);
    }

    [Fact]
    public async Task Text_that_is_not_utf8_is_unreadable_rather_than_a_page_of_question_marks()
    {
        var extracted = await ExtractAsync("text/plain", [0x48, 0x69, 0xFF, 0xFE, 0x21]);

        Assert.Equal(BrandSourceTextExtractionOutcome.Unreadable, extracted.Outcome);
        Assert.Contains("UTF-8", extracted.Reason);
    }

    [Fact]
    public async Task A_file_with_no_readable_text_is_a_review_state_not_an_empty_success()
    {
        var extracted = await ExtractAsync("text/plain", Encoding.UTF8.GetBytes("   \n\n\t\n"));

        Assert.Equal(BrandSourceTextExtractionOutcome.Unsupported, extracted.Outcome);
        Assert.Null(extracted.Text);
    }

    [Fact]
    public async Task Text_past_the_cap_is_stored_as_a_prefix_that_says_so()
    {
        var line = new string('a', 1000);
        var oversize = string.Join('\n', Enumerable.Repeat(line, (int)(BrandPolicy.ExtractedTextMaxBytes / 1000) + 50));

        var extracted = await ExtractAsync("text/plain", Encoding.UTF8.GetBytes(oversize));

        Assert.Equal(BrandSourceTextExtractionOutcome.Extracted, extracted.Outcome);
        Assert.True(extracted.Truncated);
        Assert.Equal(BrandSourceExtractionReasons.Truncated, extracted.Reason);
        Assert.True(
            Encoding.UTF8.GetByteCount(extracted.Text!) <= BrandPolicy.ExtractedTextMaxBytes + 1,
            "the artifact ran past the cap it reports being cut at");

        // Cut at a line boundary, not mid-line: a stored prefix is readable text, not a torn one.
        Assert.EndsWith(line + "\n", extracted.Text);
    }

    [Fact]
    public void A_builder_past_the_block_cap_stops_and_reports_truncation()
    {
        var builder = new BrandSourceTextBuilder();

        for (var index = 0; index < BrandPolicy.ExtractionMaxBlocks + 5; index++)
        {
            builder.Paragraph($"block {index}");
        }

        Assert.True(builder.Truncated);

        // And once anything has been refused, nothing later is accepted: a prefix is honest, a prefix with a
        // hole in the middle is not.
        builder.Paragraph("a short block that would have fitted");
        Assert.DoesNotContain("would have fitted", builder.Build());
    }

    [Fact]
    public void Extractor_ids_are_stable_distinct_and_short_enough_to_store()
    {
        var ids = BrandSourceTextExtractors.CoveredMediaTypes
            .Select(mediaType => BrandSourceTextExtractors.For(mediaType)!.ExtractorId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Provenance is only worth recording if it fits the column and identifies the parser.
        Assert.All(ids, id => Assert.InRange(id.Length, 1, BrandPolicy.ExtractorIdMaxLength));
        Assert.Contains("pdf/pdfpig@0.1.16", ids);
    }

    private static async Task<BrandSourceExtractedText> ExtractAsync(string mediaType, byte[] bytes)
    {
        var extractor = BrandSourceTextExtractors.For(mediaType);
        Assert.NotNull(extractor);

        using var content = new MemoryStream(bytes, writable: false);

        return await extractor.ExtractAsync(content, Cancellation);
    }

    /// <summary>
    /// A real PDF, produced by the renderer this repository already ships.
    /// </summary>
    /// <remarks>
    /// Written rather than checked in, and written by a real writer rather than assembled by hand: a PDF
    /// extraction test over bytes that are not a PDF proves nothing about extraction. Passing no heading and no
    /// body gives a document with pages and no glyphs, which is what a scan looks like to a parser.
    /// </remarks>
    private static byte[] RenderPdf(string? heading, string? body)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(container => container.Page(page =>
            {
                page.Margin(36);
                page.Content().Column(column =>
                {
                    if (heading is not null)
                    {
                        column.Item().Text(heading).FontSize(18);
                    }

                    if (body is not null)
                    {
                        column.Item().Text(body);
                    }

                    if (heading is null && body is null)
                    {
                        // A page with a drawn rectangle and no text at all.
                        column.Item().Height(200).Background(Colors.Grey.Lighten2);
                    }
                });
            }))
            .GeneratePdf();
    }
}
