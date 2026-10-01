using System.Net;
using System.Text;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The parser for HTML: a bounded scanner that keeps the words and the headings and throws the markup away.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A scanner, not a DOM, and that is the security property rather than a shortcut.</strong> It walks
/// the bytes once, left to right, with no tree, no node identity and no notion of a document at all. There is
/// nothing for a malformed nesting to confuse, because nesting is not modelled; there is no element to carry a
/// URL, so nothing can be fetched; and script, style and the other raw-text elements are skipped without their
/// contents ever being looked at. A real HTML library would be more faithful and would also be a parser of
/// attacker-controlled input with a far larger surface, for an output that is only ever plain text.
/// </para>
/// <para>
/// Structure survives where HTML states it: <c>h1</c>–<c>h6</c> and <c>title</c> become ATX headings,
/// <c>li</c> becomes a list item, and every other block-level boundary ends the current block. Inline markup
/// simply disappears, which is what makes <c>&lt;em&gt;</c> inside a sentence not split it.
/// </para>
/// <para>
/// Character references are decoded once per block, after the markup is gone. A reference that decodes into
/// something that looks like a tag — <c>&amp;lt;script&amp;gt;</c> — therefore stays as those literal
/// characters in the artifact and is never rescanned, which is the behaviour that matters: the artifact is
/// <c>text/plain</c>, read back as text and delimited as untrusted source material when it reaches a prompt.
/// </para>
/// </remarks>
internal sealed class BrandSourceHtmlTextExtractor : IBrandSourceTextExtractor
{
    public string ExtractorId => "html/scanner@1";

    /// <summary>Elements whose contents are not text at all. Skipped whole, never accumulated.</summary>
    private static readonly HashSet<string> Opaque = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "math", "iframe", "object", "embed", "canvas", "map",
    };

    /// <summary>Elements that end the block they appear in, opening or closing.</summary>
    private static readonly HashSet<string> Block = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "caption", "dd", "details", "dialog", "div", "dl",
        "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "hgroup", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table", "tbody", "td",
        "tfoot", "th", "thead", "title", "tr", "ul",
    };

    public async Task<BrandSourceExtractedText> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        content.Position = 0;

        string html;

        try
        {
            using var reader = new StreamReader(
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);

            html = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            return BrandSourceExtractedText.Unreadable("This file is not valid UTF-8 text, so none of it could be read.");
        }

        return Scan(html, cancellationToken);
    }

    internal static BrandSourceExtractedText Scan(string html, CancellationToken cancellationToken)
    {
        var builder = new BrandSourceTextBuilder();
        var pending = new StringBuilder();

        // What the block currently being accumulated will be emitted as. Set by the tag that opened it.
        var headingLevel = 0;
        var listItem = false;

        var position = 0;

        while (position < html.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = html.IndexOf('<', position);

            if (next < 0)
            {
                pending.Append(html, position, html.Length - position);
                break;
            }

            pending.Append(html, position, next - position);
            position = next;

            // A comment, doctype, processing instruction or CDATA section: markup with no text of its own.
            if (SkipNonElement(html, ref position))
            {
                continue;
            }

            if (!ReadTag(html, ref position, out var name, out var closing))
            {
                // A lone '<' that opens nothing. It is part of the text, as a browser would also treat it.
                pending.Append('<');
                position++;
                continue;
            }

            if (!closing && Opaque.Contains(name))
            {
                Flush(builder, pending, ref headingLevel, ref listItem);
                SkipOpaque(html, ref position, name);
                continue;
            }

            if (!Block.Contains(name))
            {
                // Inline markup. Dropped without ending the block, so emphasis inside a sentence does not
                // split it; a tag boundary still separates words that were only adjacent in the source.
                pending.Append(' ');
                continue;
            }

            Flush(builder, pending, ref headingLevel, ref listItem);

            if (closing)
            {
                continue;
            }

            if (name.Length == 2 && (name[0] is 'h' or 'H') && char.IsAsciiDigit(name[1]))
            {
                headingLevel = name[1] - '0';
            }
            else if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
            {
                // The document's own title, which is brand evidence as much as any heading in the body.
                headingLevel = 1;
            }
            else if (string.Equals(name, "li", StringComparison.OrdinalIgnoreCase))
            {
                listItem = true;
            }
        }

        Flush(builder, pending, ref headingLevel, ref listItem);

        if (builder.IsEmpty)
        {
            return BrandSourceExtractedText.Unsupported("This page holds no readable text.");
        }

        return new BrandSourceExtractedText(
            BrandSourceTextExtractionOutcome.Extracted,
            builder.Build(),
            builder.Truncated ? BrandSourceExtractionReasons.Truncated : null,
            builder.Truncated);
    }

    /// <summary>Emits whatever has accumulated and resets what the next block will be.</summary>
    private static void Flush(
        BrandSourceTextBuilder builder, StringBuilder pending, ref int headingLevel, ref bool listItem)
    {
        if (pending.Length > 0)
        {
            var text = WebUtility.HtmlDecode(pending.ToString());

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

            pending.Clear();
        }

        headingLevel = 0;
        listItem = false;
    }

    /// <summary>
    /// Steps over a comment, doctype, processing instruction or CDATA section at <paramref name="position"/>.
    /// </summary>
    /// <returns>True when one was there and consumed.</returns>
    private static bool SkipNonElement(string html, ref int position)
    {
        if (position + 1 >= html.Length)
        {
            return false;
        }

        if (html.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
        {
            var end = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
            position = end < 0 ? html.Length : end + 3;
            return true;
        }

        if (html[position + 1] is '!' or '?')
        {
            var end = html.IndexOf('>', position + 2);
            position = end < 0 ? html.Length : end + 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads one element tag, leaving <paramref name="position"/> just past it.
    /// </summary>
    /// <remarks>
    /// Quoted attribute values are tracked, so a <c>&gt;</c> inside one does not end the tag early and leak
    /// attribute text into the artifact. False for a <c>&lt;</c> that no name follows.
    /// </remarks>
    private static bool ReadTag(string html, ref int position, out string name, out bool closing)
    {
        name = string.Empty;
        closing = false;

        var cursor = position + 1;

        if (cursor < html.Length && html[cursor] == '/')
        {
            closing = true;
            cursor++;
        }

        var start = cursor;

        while (cursor < html.Length && (char.IsAsciiLetterOrDigit(html[cursor]) || html[cursor] is '-' or ':' or '_'))
        {
            cursor++;
        }

        if (cursor == start)
        {
            return false;
        }

        name = html[start..cursor];
        var quote = '\0';

        while (cursor < html.Length)
        {
            var character = html[cursor];

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                cursor++;
                break;
            }

            cursor++;
        }

        position = cursor;

        return true;
    }

    /// <summary>
    /// Steps over an opaque element's whole contents without accumulating any of it.
    /// </summary>
    /// <remarks>
    /// An unclosed one consumes the rest of the document, which is the conservative answer: a file that opens
    /// <c>&lt;script&gt;</c> and never closes it has no further text that can be told apart from code.
    /// </remarks>
    private static void SkipOpaque(string html, ref int position, string name)
    {
        var cursor = position;

        while (cursor < html.Length)
        {
            var next = html.IndexOf("</", cursor, StringComparison.Ordinal);

            if (next < 0)
            {
                position = html.Length;
                return;
            }

            var after = next + 2;

            if (html.AsSpan(after).StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                var end = html.IndexOf('>', after);
                position = end < 0 ? html.Length : end + 1;
                return;
            }

            cursor = after;
        }

        position = html.Length;
    }
}
