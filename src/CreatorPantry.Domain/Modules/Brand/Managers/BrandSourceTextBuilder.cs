using System.Text;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Assembles the one normalized form every parser produces, and the only place that decides what that form is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The artifact is plain UTF-8 text, not JSON.</strong> Structure is carried in the text itself — ATX
/// <c>#</c> headings, <c>- </c> list items, blocks a blank line apart — rather than in a parallel document. One
/// artifact means one media type in storage, one checksum, and text a creator can correct directly in 11A.11;
/// a JSON sidecar would mean two things to keep in step and a correction path that has to edit a tree.
/// </para>
/// <para>
/// Every block is normalized the same way whatever parser offered it: control characters dropped, runs of
/// whitespace collapsed to one space, ends trimmed, empty blocks discarded. That is what makes a DOCX heading
/// and an HTML heading come out identically, which is the point of having one form at all.
/// </para>
/// <para>
/// <strong>The caps are refusals to grow, not errors.</strong> Past
/// <see cref="BrandPolicy.ExtractedTextMaxBytes"/> or <see cref="BrandPolicy.ExtractionMaxBlocks"/> the builder
/// stops accepting and reports <see cref="Truncated"/>; the artifact is a prefix and the extraction records
/// that it is. A hostile file cannot make this allocate without bound, and an honest enormous one still
/// produces something useful.
/// </para>
/// </remarks>
internal sealed class BrandSourceTextBuilder
{
    private const string BlockSeparator = "\n\n";

    private readonly StringBuilder _text = new();
    private long _bytes;
    private int _blocks;

    /// <summary>Whether anything was refused for want of room.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Whether any block has been accepted.</summary>
    public bool IsEmpty => _blocks == 0;

    /// <summary>Appends a heading at <paramref name="level"/>, clamped to one of the six ATX depths.</summary>
    public void Heading(int level, string? text) =>
        Append(new string('#', Math.Clamp(level, 1, 6)) + ' ', text);

    public void Paragraph(string? text) => Append(string.Empty, text);

    public void ListItem(string? text) => Append("- ", text);

    /// <summary>
    /// The artifact as it will be stored. A trailing newline, so the file ends on a line like any text file.
    /// </summary>
    public string Build() => _blocks == 0 ? string.Empty : _text.Append('\n').ToString();

    private void Append(string prefix, string? text)
    {
        if (Truncated)
        {
            // Once anything has been refused, accepting a later block would produce an artifact that silently
            // skips a passage in the middle. A prefix is honest; a prefix with holes is not.
            return;
        }

        var normalized = Normalize(text);

        if (normalized.Length == 0)
        {
            return;
        }

        if (_blocks >= BrandPolicy.ExtractionMaxBlocks)
        {
            Truncated = true;
            return;
        }

        var block = prefix + normalized;
        var cost = Encoding.UTF8.GetByteCount(block) + (_blocks == 0 ? 0 : BlockSeparator.Length);

        if (_bytes + cost > BrandPolicy.ExtractedTextMaxBytes)
        {
            Truncated = true;
            return;
        }

        if (_blocks > 0)
        {
            _text.Append(BlockSeparator);
        }

        _text.Append(block);
        _bytes += cost;
        _blocks++;
    }

    /// <summary>
    /// One block's text, reduced to printable characters and single spaces.
    /// </summary>
    /// <remarks>
    /// Control characters go rather than being escaped, and that includes the ones a terminal or a diff would
    /// act on: this text is read back, shown to a creator and later delimited into a prompt, and none of those
    /// readers benefits from a bidirectional override or a NUL surviving. Tabs and newlines inside a block
    /// become spaces, because the block boundary is what carries structure here.
    /// </remarks>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            // Zero-width and bidirectional formatting characters, and anything else with no glyph.
            if (char.IsControl(character) || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
