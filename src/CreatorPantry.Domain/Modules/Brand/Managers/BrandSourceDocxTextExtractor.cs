using System.IO.Compression;
using System.Text;
using System.Xml;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The parser for Word documents: the main document part, read as a stream of WordprocessingML paragraphs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in a DOCX is executed, resolved or followed.</strong> The reader is configured with
/// <see cref="DtdProcessing.Prohibit"/> and no <see cref="XmlResolver"/>, so a document type declaration is an
/// error rather than an instruction and no entity, schema or external part can be fetched — the two ways an XML
/// parse of a hostile file normally goes wrong. Field codes (<c>w:instrText</c>) are skipped rather than kept:
/// they are directives such as <c>INCLUDETEXT</c>, not the author's words, and a stored artifact that quoted
/// them would be putting instruction text where this module promises to put source material.
/// </para>
/// <para>
/// A macro project cannot reach this parser — <see cref="BrandSourceFileInspector"/> refuses one at upload —
/// and the check is repeated here anyway, because the thing that makes it safe to say "no macro is executed" is
/// that no code path exists which could, not that an earlier gate was careful.
/// </para>
/// <para>
/// <strong>Only <c>word/document.xml</c>.</strong> Headers, footers, footnotes, endnotes and comments are
/// deliberately not read: they are not where a creator's brand writing lives, and each is another part to bound
/// and another source of text a creator would not recognise as theirs when they review the extraction.
/// </para>
/// </remarks>
internal sealed class BrandSourceDocxTextExtractor : IBrandSourceTextExtractor
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string MainPart = "word/document.xml";

    public string ExtractorId => "docx/wordprocessingml@1";

    public async Task<BrandSourceExtractedText> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        content.Position = 0;

        try
        {
            using var package = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

            if (package.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            {
                return BrandSourceExtractedText.Unreadable(
                    "This file carries a macro project, so it is not read as a document.");
            }

            var document = package.GetEntry(MainPart);

            if (document is null)
            {
                return BrandSourceExtractedText.Unreadable("This file is not a Word document.");
            }

            if (document.Length > BrandPolicy.SourceDocxMaxUncompressedBytes)
            {
                return BrandSourceExtractedText.Unreadable("This document is too large to read.");
            }

            await using var part = document.Open();

            return await ReadAsync(part, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return BrandSourceExtractedText.Unreadable("This file does not hold together as a Word document.");
        }
        catch (XmlException)
        {
            // Includes the prohibited-DTD case, which is a refusal rather than a fault: a Word document has no
            // business declaring one, and the answer a creator needs is that this file cannot be read.
            return BrandSourceExtractedText.Unreadable("This document's contents could not be read.");
        }
    }

    private static async Task<BrandSourceExtractedText> ReadAsync(Stream part, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(part, settings);

        var builder = new BrandSourceTextBuilder();
        var paragraph = new StringBuilder();

        var headingLevel = 0;
        var listItem = false;
        var inParagraph = false;
        var skipDepth = -1;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Nothing further can be accepted, so nothing further needs decompressing or parsing. This is what
            // bounds the work a declared-200 MB part can cost, not just the memory it would take.
            if (builder.Truncated)
            {
                break;
            }

            if (skipDepth >= 0)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == skipDepth)
                {
                    skipDepth = -1;
                }

                continue;
            }

            if (reader.NamespaceURI != WordNamespace)
            {
                continue;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "p":
                            inParagraph = true;
                            headingLevel = 0;
                            listItem = false;
                            paragraph.Clear();
                            break;

                        case "pStyle" when inParagraph:
                            headingLevel = HeadingLevel(reader.GetAttribute("val", WordNamespace));
                            break;

                        case "numPr" when inParagraph:
                            listItem = true;
                            break;

                        case "t" when inParagraph:
                            paragraph.Append(await reader.ReadElementContentAsStringAsync());

                            // ReadElementContentAsStringAsync consumed the end element, so the loop must not
                            // treat the current node as one.
                            continue;

                        case "tab" or "br" or "cr" when inParagraph:
                            paragraph.Append(' ');
                            break;

                        // Field codes and the text of a tracked deletion: markup that is not the author's prose.
                        case "instrText" or "delText" or "fldChar":
                            if (!reader.IsEmptyElement)
                            {
                                skipDepth = reader.Depth;
                            }

                            break;
                    }

                    break;

                case XmlNodeType.EndElement when reader.LocalName == "p":
                    Emit(builder, paragraph, headingLevel, listItem);
                    inParagraph = false;
                    break;
            }
        }

        if (builder.IsEmpty)
        {
            return BrandSourceExtractedText.Unsupported("This document holds no readable text.");
        }

        return new BrandSourceExtractedText(
            BrandSourceTextExtractionOutcome.Extracted,
            builder.Build(),
            builder.Truncated ? BrandSourceExtractionReasons.Truncated : null,
            builder.Truncated);
    }

    private static void Emit(BrandSourceTextBuilder builder, StringBuilder paragraph, int headingLevel, bool listItem)
    {
        var text = paragraph.ToString();
        paragraph.Clear();

        if (headingLevel > 0)
        {
            builder.Heading(headingLevel, text);
        }
        else if (listItem)
        {
            builder.ListItem(text);
        }
        else
        {
            builder.Paragraph(text);
        }
    }

    /// <summary>
    /// The ATX depth a paragraph style asks for, or zero for a style that is not a heading.
    /// </summary>
    /// <remarks>
    /// Word's built-in styles are <c>Heading1</c> to <c>Heading9</c> in a document saved by Word and
    /// <c>heading 1</c> in one written by another tool, so both spellings are read. A custom style named
    /// anything else is not guessed at: an artifact that promoted an arbitrary style to a heading would be
    /// inventing structure the document does not state.
    /// </remarks>
    private static int HeadingLevel(string? style)
    {
        if (string.IsNullOrEmpty(style))
        {
            return 0;
        }

        var trimmed = style.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (!trimmed.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || trimmed.Length != "Heading".Length + 1)
        {
            return 0;
        }

        var digit = trimmed[^1];

        return char.IsAsciiDigit(digit) && digit != '0' ? digit - '0' : 0;
    }
}
