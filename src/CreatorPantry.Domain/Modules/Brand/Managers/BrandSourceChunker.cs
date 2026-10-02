using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>One passage cut from an extracted artifact, before it is embedded.</summary>
/// <param name="Ordinal">1 for the first passage, in reading order.</param>
/// <param name="StartByteOffset">Where the passage starts in the artifact, in UTF-8 bytes.</param>
/// <param name="ByteLength">How many UTF-8 bytes of the artifact it spans.</param>
/// <param name="ContentChecksum"><c>sha256:</c> and the digest of exactly those bytes.</param>
/// <param name="Text">The passage, exactly as the artifact has it.</param>
public sealed record BrandSourceChunkPassage(
    int Ordinal, long StartByteOffset, int ByteLength, string ContentChecksum, string Text);

/// <summary>
/// Cuts extracted text into the passages retrieval will read. Pure: the same text always gives the same
/// passages, which is what lets an interrupted run resume and a replay find nothing to redo.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Strategy <c>text/paragraph-1600c-200o@1</c>.</strong> Aim for <see cref="BrandPolicy.ChunkTargetLength"/>
/// characters, prefer to end on a blank line, then a line break, then sentence punctuation, then whitespace,
/// and only hard-split a run with none of those. Each passage after the first starts
/// <see cref="BrandPolicy.ChunkOverlapLength"/> characters before the previous one ended — moved forward to the
/// next whitespace so a word is not cut — so a sentence split by a boundary is whole in one of the two.
/// </para>
/// <para>
/// <strong>Offsets are bytes, text is characters.</strong> Cuts are made on UTF-16 indices and never inside a
/// surrogate pair, so every passage is a valid slice; the byte offset is then computed from the prefix. A
/// passage is stored verbatim and never trimmed or normalized, because <see cref="BrandSourceChunkPassage.ContentChecksum"/>
/// must hash exactly the bytes the offsets name. Blank passages (all whitespace) are dropped, not embedded.
/// </para>
/// <para>Changing any behavior here means a new <see cref="Id"/>: a strategy change moves every boundary.</para>
/// </remarks>
public static class BrandSourceChunker
{
    /// <summary>This strategy's stable identity, recorded on every set it produces.</summary>
    public const string Id = "text/paragraph-1600c-200o@1";

    /// <summary>How far back from the target a preferred break may be taken.</summary>
    private const int LookBack = 400;

    public static IReadOnlyList<BrandSourceChunkPassage> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var passages = new List<BrandSourceChunkPassage>();
        var start = 0;
        var byteOffset = 0L;
        var bytesCounted = 0; // UTF-16 index up to which byteOffset has been accumulated

        while (start < text.Length)
        {
            var end = FindEnd(text, start);
            var slice = text.AsSpan(start, end - start);

            if (!slice.IsWhiteSpace())
            {
                byteOffset += Encoding.UTF8.GetByteCount(text.AsSpan(bytesCounted, start - bytesCounted));
                bytesCounted = start;

                var passage = text.Substring(start, end - start);
                var bytes = Encoding.UTF8.GetBytes(passage);

                passages.Add(new BrandSourceChunkPassage(
                    passages.Count + 1,
                    byteOffset,
                    bytes.Length,
                    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    passage));
            }

            if (end >= text.Length)
            {
                break;
            }

            start = NextStart(text, start, end);
        }

        return passages;
    }

    private static int FindEnd(string text, int start)
    {
        var limit = Math.Min(text.Length, start + BrandPolicy.ChunkTargetLength);

        if (limit >= text.Length)
        {
            return text.Length;
        }

        var floor = Math.Max(start + 1, limit - LookBack);
        var end = LastBreak(text, floor, limit);

        if (end < 0)
        {
            // Nothing to break on within reach: hard split, and never between the halves of a surrogate pair.
            end = limit;

            if (char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
            {
                end--;
            }
        }

        return end;
    }

    /// <summary>The end index (exclusive) of the best break in [floor, limit], or -1.</summary>
    private static int LastBreak(string text, int floor, int limit)
    {
        var paragraph = text.LastIndexOf("\n\n", limit - 1, limit - floor, StringComparison.Ordinal);

        if (paragraph >= 0)
        {
            return paragraph + 2;
        }

        var line = text.LastIndexOf('\n', limit - 1, limit - floor);

        if (line >= 0)
        {
            return line + 1;
        }

        for (var i = limit - 1; i >= floor; i--)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 < text.Length) && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 1;
            }
        }

        for (var i = limit - 1; i >= floor; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static int NextStart(string text, int start, int end)
    {
        var next = Math.Max(start + 1, end - BrandPolicy.ChunkOverlapLength);

        // Forward to the next whitespace so the overlap begins on a word, then past it. If there is none
        // before the end, the overlap is dropped rather than starting mid-word.
        var boundary = next;

        while (boundary < end && !char.IsWhiteSpace(text[boundary]))
        {
            boundary++;
        }

        next = boundary < end ? boundary + 1 : end;

        // Never inside a surrogate pair, and always progress.
        if (next < text.Length && char.IsLowSurrogate(text[next]))
        {
            next++;
        }

        return Math.Max(next, start + 1);
    }
}
