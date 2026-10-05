namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// How many frames a GIF holds, read by walking its block structure.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> IMG-004 accepts an animated GIF rather than refusing one, and the
/// analysis then describes its opening frame — so the creator has to be told that is what happened. Telling
/// them requires knowing whether there was more than one frame, and nothing else in the repository can say:
/// there is no image-decoding dependency, and <c>BrandSourceFileInspector</c> reads only the header.
/// </para>
/// <para>
/// <strong>It counts frames and decodes nothing.</strong> It walks the block structure — colour tables,
/// extensions and their sub-blocks, image descriptors — and skips every payload rather than inflating it, so
/// it never allocates more than the span it was given and cannot be made to do work by a crafted file. A
/// frame count is all IMG-004 needs; the pixels are the provider's problem.
/// </para>
/// <para>
/// <strong>Null means "could not tell", and that is a real answer.</strong> A truncated or malformed GIF has
/// already passed the inspector's header check, so refusing it here would be a second opinion on validity
/// that this type is not qualified to give. The handler warns in the same words it uses for an animated file,
/// because "this may have had frames you did not see" is true either way.
/// </para>
/// </remarks>
public static class GifFrames
{
    /// <summary>The longest a GIF's block chain may be walked before giving up.</summary>
    /// <remarks>
    /// A guard rather than a limit anyone should reach: a reference image with more than this many frames is
    /// a video, and the walk stops with what it has rather than reading on.
    /// </remarks>
    public const int MaxFramesCounted = 512;

    /// <summary>
    /// The number of image frames, or null when the bytes are not a GIF this can walk to the end of.
    /// </summary>
    public static int? Count(ReadOnlySpan<byte> gif)
    {
        if (gif.Length < 13 || !(gif.StartsWith("GIF87a"u8) || gif.StartsWith("GIF89a"u8)))
        {
            return null;
        }

        // Header (6) plus logical screen descriptor (7). The packed byte's high bit says whether a global
        // colour table follows, and its low three bits give that table's size.
        var position = 13;

        if (!SkipColourTable(gif, gif[10], ref position))
        {
            return null;
        }

        var frames = 0;

        while (position < gif.Length && frames < MaxFramesCounted)
        {
            switch (gif[position++])
            {
                case 0x3B:
                    // Trailer. A clean end, so the count is trustworthy.
                    return frames;

                case 0x21:
                    // Extension: a label byte, then sub-blocks. Graphic control, comment, plain text and the
                    // NETSCAPE loop block all take this shape, so none of them needs its own case.
                    if (position >= gif.Length)
                    {
                        return null;
                    }

                    position++;

                    if (!SkipSubBlocks(gif, ref position))
                    {
                        return null;
                    }

                    break;

                case 0x2C:
                    // Image descriptor: nine bytes, an optional local colour table, the LZW minimum code
                    // size, then the compressed data as sub-blocks.
                    if (position + 9 > gif.Length)
                    {
                        return null;
                    }

                    var packed = gif[position + 8];
                    position += 9;

                    if (!SkipColourTable(gif, packed, ref position) || position >= gif.Length)
                    {
                        return null;
                    }

                    position++;

                    if (!SkipSubBlocks(gif, ref position))
                    {
                        return null;
                    }

                    frames++;
                    break;

                default:
                    // Not a block this format defines. The file is malformed from here on, and guessing past
                    // it would turn a broken GIF into a confident frame count.
                    return null;
            }
        }

        // Ran out of bytes before the trailer, or hit the guard. Either way the count is not trustworthy.
        return null;
    }

    /// <summary>Whether the GIF holds more than one frame. Null when that cannot be determined.</summary>
    public static bool? IsAnimated(ReadOnlySpan<byte> gif) => Count(gif) is { } frames ? frames > 1 : null;

    private static bool SkipColourTable(ReadOnlySpan<byte> gif, byte packed, ref int position)
    {
        if ((packed & 0x80) == 0)
        {
            return true;
        }

        var entries = 1 << ((packed & 0x07) + 1);
        position += entries * 3;

        return position <= gif.Length;
    }

    /// <summary>
    /// Skips a chain of length-prefixed sub-blocks, ending at the zero-length one.
    /// </summary>
    private static bool SkipSubBlocks(ReadOnlySpan<byte> gif, ref int position)
    {
        while (position < gif.Length)
        {
            var size = gif[position++];

            if (size == 0)
            {
                return true;
            }

            position += size;
        }

        return false;
    }
}
