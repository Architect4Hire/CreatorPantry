using System.Buffers.Binary;

namespace CreatorPantry.Domain.Modules.Media.Managers;

public enum GeneratedImageInspectionOutcome
{
    Accepted = 1,

    /// <summary>No bytes at all.</summary>
    Empty = 2,

    /// <summary>Not one of the formats this will stage, whatever the provider labelled it.</summary>
    Unsupported = 3,

    /// <summary>Starts like a supported format and does not hold together as one.</summary>
    Corrupt = 4,

    /// <summary>More pixels than <see cref="MediaPolicy.ImageMaxPixels"/> allows.</summary>
    TooLarge = 5,
}

/// <param name="MediaType">Established from the bytes. Present exactly when the outcome is accepted.</param>
public readonly record struct GeneratedImageInspection(
    GeneratedImageInspectionOutcome Outcome, string? MediaType = null, int Width = 0, int Height = 0);

/// <summary>
/// Decides what a provider actually returned by reading it: PNG, JPEG, WebP or GIF, and the real pixel
/// dimensions of each.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing the provider said is an input.</strong> Not its declared media type, not the size that
/// was asked for, not a filename. A response is a stranger's bytes arriving over the network, and media.md
/// asks for a signature check rather than a label for exactly this reason. The media type and the
/// dimensions stored on a <c>GeneratedImage</c> are the ones this reads out of the file.
/// </para>
/// <para>
/// <strong>Nothing is resized, padded or re-encoded.</strong> A provider that returned a different shape
/// from the one requested has that shape recorded as it is. Stretching an image to the aspect a creator
/// asked for would silently distort their work, and the restriction on this job forbids it — a mismatch is
/// something to show them, not something to hide by scaling.
/// </para>
/// <para>
/// "Holds together" is structural, not a render: a header that yields sane dimensions. No pixel is decoded,
/// which also means this cannot be made to spend arbitrary time or memory on a hostile file.
/// </para>
/// </remarks>
public static class GeneratedImageInspector
{
    public const string PngMediaType = "image/png";

    public const string JpegMediaType = "image/jpeg";

    public const string WebpMediaType = "image/webp";

    public const string GifMediaType = "image/gif";

    /// <summary>Every media type a staged image may be stored as.</summary>
    public static readonly IReadOnlySet<string> AcceptedMediaTypes =
        new HashSet<string>(StringComparer.Ordinal) { PngMediaType, JpegMediaType, WebpMediaType, GifMediaType };

    /// <summary>
    /// The extension a download of this media type should carry, without its dot, or null for a type this
    /// does not know.
    /// </summary>
    /// <remarks>
    /// Here rather than next to the download, because this class is what decides a staged image's media
    /// type from its bytes: a format this can read and that had no extension to offer would be a drift
    /// between two lists, and <c>GeneratedImageInspectorTests</c> asserts the two stay in step. One per
    /// type — a JPEG is always <c>.jpg</c>, because a download name is ours to choose and determinism is
    /// the point.
    /// </remarks>
    public static string? CanonicalExtension(string? mediaType) =>
        mediaType is not null && CanonicalExtensions.TryGetValue(mediaType, out var extension)
            ? extension
            : null;

    private static readonly Dictionary<string, string> CanonicalExtensions = new(StringComparer.Ordinal)
    {
        [PngMediaType] = "png",
        [JpegMediaType] = "jpg",
        [WebpMediaType] = "webp",
        [GifMediaType] = "gif",
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Reads the format and dimensions out of <paramref name="bytes"/>, or says why it will not.</summary>
    public static GeneratedImageInspection Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return new GeneratedImageInspection(GeneratedImageInspectionOutcome.Empty);
        }

        if (bytes.StartsWith(PngSignature))
        {
            return Png(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return Jpeg(bytes);
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return Webp(bytes);
        }

        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return Gif(bytes);
        }

        return new GeneratedImageInspection(GeneratedImageInspectionOutcome.Unsupported);
    }

    /// <summary>The IHDR chunk, which the format requires to be the first one.</summary>
    private static GeneratedImageInspection Png(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[12..16].SequenceEqual("IHDR"u8))
        {
            return Corrupt();
        }

        return Dimensions(
            PngMediaType,
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
    }

    /// <summary>
    /// Walks the marker segments to the first frame header, which is where a JPEG states its size.
    /// </summary>
    /// <remarks>
    /// Bounded by the length of the file and by the segment lengths it declares, so a malformed chain ends
    /// as corrupt rather than looping. Image data or an end marker before any frame header means there was
    /// never a frame to describe.
    /// </remarks>
    private static GeneratedImageInspection Jpeg(ReadOnlySpan<byte> bytes)
    {
        var position = 2;

        while (position + 4 <= bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                return Corrupt();
            }

            var marker = bytes[position + 1];

            if (marker == 0xFF)
            {
                // A fill byte before the marker proper.
                position++;
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return Corrupt();
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 2)..]);

            if (segmentLength < 2)
            {
                return Corrupt();
            }

            // SOF0 to SOF15, less the three in that range that are tables rather than frames.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return position + 9 > bytes.Length
                    ? Corrupt()
                    : Dimensions(
                        JpegMediaType,
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 7)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]));
            }

            position += 2 + segmentLength;
        }

        return Corrupt();
    }

    /// <summary>The three WebP variants, each of which states its size differently.</summary>
    private static GeneratedImageInspection Webp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) > bytes.Length - 8)
        {
            return Corrupt();
        }

        var chunk = bytes[12..16];

        if (chunk.SequenceEqual("VP8 "u8))
        {
            return bytes[23..26].SequenceEqual((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A])
                ? Dimensions(
                    WebpMediaType,
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF))
                : Corrupt();
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);

            return bytes[20] == 0x2F
                ? Dimensions(WebpMediaType, (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1)
                : Corrupt();
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return Dimensions(
                WebpMediaType,
                (uint)(bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1,
                (uint)(bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1);
        }

        return Corrupt();
    }

    /// <summary>
    /// A GIF's logical screen size, from the descriptor that follows the six-byte header.
    /// </summary>
    /// <remarks>
    /// The canvas every frame shares. Frames are not counted: what is validated here is the file, and a
    /// staged image records the size of the image it is.
    /// </remarks>
    private static GeneratedImageInspection Gif(ReadOnlySpan<byte> bytes) =>
        bytes.Length < 10
            ? Corrupt()
            : Dimensions(
                GifMediaType,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));

    private static GeneratedImageInspection Dimensions(string mediaType, uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return Corrupt();
        }

        // Each side on its own first: two sides near the top of 32 bits multiply past what a long holds,
        // and the product would come back small or negative and pass.
        if (width > MediaPolicy.ImageMaxPixels
            || height > MediaPolicy.ImageMaxPixels
            || (long)width * height > MediaPolicy.ImageMaxPixels)
        {
            return new GeneratedImageInspection(GeneratedImageInspectionOutcome.TooLarge);
        }

        // Safe to narrow: every format above reads its dimensions from at most 32 bits, and the pixel cap
        // just rejected anything whose product could not fit a row's int columns.
        return new GeneratedImageInspection(
            GeneratedImageInspectionOutcome.Accepted, mediaType, (int)width, (int)height);
    }

    private static GeneratedImageInspection Corrupt() =>
        new(GeneratedImageInspectionOutcome.Corrupt);
}
