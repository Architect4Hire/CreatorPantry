using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Fonts;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The parser for PDFs: the text layer, page by page, in content order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A PDF with no text layer is <see cref="BrandSourceTextExtractionOutcome.Unsupported"/>, never a
/// guess.</strong> A scan is a page of pictures, and this module performs no text recognition anywhere — so the
/// honest answer is a review state that says there is nothing to read and why, which is exactly what
/// <see cref="BrandSourceExtractionReasons.NoTextLayer"/> says. Reporting an empty extraction as a success, or
/// claiming words that were never in the file, is the silent OCR claim recipes.md and media.md both forbid.
/// </para>
/// <para>
/// <strong>No headings.</strong> A PDF states none — its headings are a font size — and inferring them from
/// glyph metrics would be inventing structure the document does not carry. Paragraph blocks and page breaks are
/// what survives, which is what the text is actually good for.
/// </para>
/// <para>
/// Lenient parsing is on and missing fonts are skipped, so a slightly malformed file still yields its text
/// rather than nothing. <see cref="ParsingOptions.ClipPaths"/> stays off: clipping matters when rendering and
/// costs work here for an output that is only characters. No password is ever supplied — an encrypted document
/// is refused rather than attacked.
/// </para>
/// </remarks>
internal sealed class BrandSourcePdfTextExtractor : IBrandSourceTextExtractor
{
    /// <summary>
    /// Versioned on the library, because a PdfPig upgrade can change what a file's text comes out as and an
    /// artifact nobody can attribute to a parser is one nobody can re-derive.
    /// </summary>
    public string ExtractorId => "pdf/pdfpig@0.1.16";

    public Task<BrandSourceExtractedText> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        content.Position = 0;

        // Synchronous by nature: PdfPig reads a seekable stream it holds, and there is no asynchronous entry
        // point to offer. The caller has already buffered the whole file, so nothing here waits on I/O.
        return Task.FromResult(Extract(content, cancellationToken));
    }

    private static BrandSourceExtractedText Extract(Stream content, CancellationToken cancellationToken)
    {
        var options = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            ClipPaths = false,

            // PdfPig logs parse complaints; it must not be given the application's logger, because a complaint
            // can quote document content and this module does not log creator content by default (ai.md).
            Logger = SilentLog.Instance,
        };

        try
        {
            using var pdf = PdfDocument.Open(content, options);

            var builder = new BrandSourceTextBuilder();

            // Bounded before the walk, so a file declaring thousands of pages costs what the cap allows.
            var pages = Math.Min(pdf.NumberOfPages, BrandPolicy.ExtractionMaxPdfPages);

            for (var number = 1; number <= pages && !builder.Truncated; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string text;

                try
                {
                    // Content order rather than reading order analysis: it is what the document itself states,
                    // and a layout algorithm's guess at columns is a quality improvement this does not need to
                    // make to be correct. The flag asks for blank lines between blocks, which is what the
                    // reflow below cuts on.
                    text = ContentOrderTextExtractor.GetText(pdf.GetPage(number), true);
                }
                catch (Exception exception) when (IsParseFailure(exception))
                {
                    // One unreadable page does not make the document unreadable. The pages that did read are
                    // worth keeping, and a skipped one is visible as a gap rather than reported as text.
                    continue;
                }

                Reflow(builder, text);
            }

            if (builder.IsEmpty)
            {
                return BrandSourceExtractedText.Unsupported(BrandSourceExtractionReasons.NoTextLayer);
            }

            return new BrandSourceExtractedText(
                BrandSourceTextExtractionOutcome.Extracted,
                builder.Build(),
                builder.Truncated ? BrandSourceExtractionReasons.Truncated : null,
                builder.Truncated);
        }
        catch (PdfDocumentEncryptedException)
        {
            return BrandSourceExtractedText.Unreadable(
                "This PDF is password-protected, so its text cannot be read.");
        }
        catch (Exception exception) when (IsParseFailure(exception))
        {
            return BrandSourceExtractedText.Unreadable("This PDF does not hold together, so its text could not be read.");
        }
    }

    /// <summary>
    /// One page's extracted text, turned into blocks: lines rejoined into paragraphs, blank lines as breaks.
    /// </summary>
    /// <remarks>
    /// Rejoining is the point. A PDF line break is where the glyphs ran out of page, not where the sentence
    /// ended, so an artifact that kept them would hand every later reader a column of fragments.
    /// </remarks>
    private static void Reflow(BrandSourceTextBuilder builder, string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return;
        }

        var paragraph = new List<string>();

        foreach (var rawLine in pageText.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                Emit(builder, paragraph);
                continue;
            }

            paragraph.Add(line);
        }

        Emit(builder, paragraph);
    }

    private static void Emit(BrandSourceTextBuilder builder, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        builder.Paragraph(string.Join(' ', paragraph));
        paragraph.Clear();
    }

    /// <summary>
    /// Whether an exception is this file failing to parse rather than the application failing.
    /// </summary>
    /// <remarks>
    /// Listed rather than caught as <see cref="Exception"/>: a cancellation and an out-of-memory are not a
    /// malformed document and must not be recorded as one. PdfPig's own hierarchy does not cover every way a
    /// hostile file can end a parse — a truncated offset table surfaces as an index or format exception from
    /// the tokenizer — so the three framework types it can reach the surface as are named here too.
    /// </remarks>
    private static bool IsParseFailure(Exception exception) =>
        exception is PdfDocumentFormatException
            or PdfDocumentStackDepthException
            or InvalidFontFormatException
            or CorruptCompressedDataException
            or InvalidOperationException
            or ArgumentException
            or IndexOutOfRangeException
            or FormatException
            or OverflowException
            or NotSupportedException
            or EndOfStreamException;

    /// <summary>
    /// The sink PdfPig's parse complaints go to: nowhere.
    /// </summary>
    /// <remarks>
    /// Not an oversight and not laziness. A parser's complaint about a malformed object quotes the bytes it
    /// choked on, which is creator content from a private document, and ai.md forbids logging that by default.
    /// What this module does record about an extraction — the outcome, the parser, the timing — it records on
    /// the operation row, where it is scoped to a workspace and visible to the creator it belongs to.
    /// </remarks>
    private sealed class SilentLog : UglyToad.PdfPig.Logging.ILog
    {
        public static readonly SilentLog Instance = new();

        public void Debug(string message)
        {
        }

        public void Debug(string message, Exception ex)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }

        public void Error(string message, Exception ex)
        {
        }
    }
}
