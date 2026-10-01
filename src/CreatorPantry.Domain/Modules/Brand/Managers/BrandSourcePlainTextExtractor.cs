using System.Text;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The parser for Markdown and plain text: the creator's own lines, normalized but not rewritten.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not reflowed.</strong> The binary formats have no line structure worth keeping, so
/// <see cref="BrandSourceTextBuilder"/> reassembles them into blocks. A Markdown or text file is the opposite
/// case: its line breaks are the author's, and a numbered list, a table, an address block or a fenced code
/// sample is destroyed by joining consecutive lines into a paragraph. Markdown is already the form this
/// module's artifact is in, so the honest parse of it is almost no parse at all.
/// </para>
/// <para>
/// What is changed is only what a stored artifact should not carry: a byte-order mark, platform line endings,
/// trailing whitespace, control and formatting characters a reader cannot see but a terminal or a prompt
/// boundary might act on, and runs of blank lines longer than one. Past
/// <see cref="BrandPolicy.ExtractedTextMaxBytes"/> the artifact is a prefix cut at a line boundary and says so.
/// </para>
/// </remarks>
internal sealed class BrandSourcePlainTextExtractor : IBrandSourceTextExtractor
{
    public string ExtractorId => "text/lines@1";

    public async Task<BrandSourceExtractedText> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        content.Position = 0;

        string decoded;

        try
        {
            // Strict, so a file that is not really UTF-8 is a review state rather than a page of replacement
            // characters. The upload already established that it is; this is the second half of that promise.
            using var reader = new StreamReader(
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);

            decoded = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            return BrandSourceExtractedText.Unreadable("This file is not valid UTF-8 text, so none of it could be read.");
        }

        return Normalize(decoded);
    }

    /// <summary>
    /// One text document reduced to the stored form: normalized lines, at most one blank line between them.
    /// </summary>
    internal static BrandSourceExtractedText Normalize(string decoded)
    {
        var text = new StringBuilder();
        var bytes = 0L;
        var truncated = false;
        var blankPending = false;
        var wrote = false;

        foreach (var rawLine in decoded.Split('\n'))
        {
            var line = Line(rawLine);

            if (line.Length == 0)
            {
                // Held rather than written: runs of blank lines collapse to one, and a trailing run to none.
                blankPending = wrote;
                continue;
            }

            var pending = (blankPending ? "\n\n" : wrote ? "\n" : string.Empty) + line;
            var cost = Encoding.UTF8.GetByteCount(pending);

            if (bytes + cost > BrandPolicy.ExtractedTextMaxBytes)
            {
                // Cut at a line boundary and stop for good: accepting a later, shorter line would produce an
                // artifact that silently skips a passage in the middle.
                truncated = true;
                break;
            }

            text.Append(pending);
            bytes += cost;
            blankPending = false;
            wrote = true;
        }

        if (!wrote)
        {
            return BrandSourceExtractedText.Unsupported("This file holds no readable text.");
        }

        return new BrandSourceExtractedText(
            BrandSourceTextExtractionOutcome.Extracted,
            text.Append('\n').ToString(),
            truncated ? BrandSourceExtractionReasons.Truncated : null,
            truncated);
    }

    /// <summary>
    /// One line, with its invisible characters and trailing space removed and its interior spacing left alone.
    /// </summary>
    /// <remarks>
    /// Interior whitespace survives because indentation is meaning in a Markdown file — a nested list item, a
    /// fenced block, a continuation line. Control and formatting characters do not: they are invisible to the
    /// creator reading this back and are exactly what a bidirectional-override or zero-width trick is made of.
    /// </remarks>
    private static string Line(string raw)
    {
        var builder = new StringBuilder(raw.Length);

        foreach (var character in raw)
        {
            if (character is '\t' or ' ')
            {
                builder.Append(character);
                continue;
            }

            if (char.IsControl(character) || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// The creator-facing notes an extraction records, in one place so that two parsers cannot describe the same
/// situation differently.
/// </summary>
/// <remarks>
/// Each one <strong>describes</strong> the document rather than quoting it, which is what lets these be stored
/// on a row and shown in a list while the text itself stays private (ai.md). None of them claims anything was
/// recognised in an image: no OCR is performed anywhere in this module, and saying so plainly is the point.
/// </remarks>
internal static class BrandSourceExtractionReasons
{
    public const string Truncated =
        "This document holds more text than is stored. What is kept is the beginning of it.";

    public const string Image =
        "This is an image, so there is no text to read. No text recognition is performed on pictures.";

    public const string NoTextLayer =
        "This PDF has no text layer, which usually means it is a scan. No text recognition is performed, so "
            + "nothing was extracted.";
}
