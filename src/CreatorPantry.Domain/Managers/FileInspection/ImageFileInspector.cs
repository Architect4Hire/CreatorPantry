using System.Buffers.Binary;

namespace CreatorPantry.Domain.Managers.FileInspection;

public enum ImageFileInspectionOutcome
{
    Accepted = 1,

    /// <summary>No bytes at all.</summary>
    Empty = 2,

    /// <summary>Not one of the four formats this reads, whatever anyone labelled it.</summary>
    Unsupported = 3,

    /// <summary>Starts like a supported format and does not hold together as one.</summary>
    Corrupt = 4,

    /// <summary>More pixels, or a longer edge, than the caller's cap.</summary>
    TooLarge = 5,

    /// <summary>
    /// A whole image, and then more: bytes after the format's own end, or a ZIP directory reachable from the end
    /// of the file. What a polyglot looks like from the outside.
    /// </summary>
    TrailingData = 6,
}

/// <param name="MediaType">Established from the bytes. Present exactly when the outcome is accepted.</param>
/// <param name="FrameCount">
/// 1 for a still. An APNG's declared frame count, an animated WebP's frame chunks, a GIF's image descriptors.
/// </param>
/// <param name="HasLocationMetadata">
/// Whether readable EXIF or XMP in the file carries a GPS position. False also means "none this could read": a
/// compressed or malformed metadata block is not opened, and is not a reason to refuse the picture.
/// </param>
public readonly record struct ImageFileInspection(
    ImageFileInspectionOutcome Outcome,
    string? MediaType = null,
    int Width = 0,
    int Height = 0,
    int FrameCount = 0,
    bool HasLocationMetadata = false)
{
    public bool IsAnimated => FrameCount > 1;
}

/// <summary>
/// The one reading of an image file: PNG, JPEG, WebP or GIF, walked structurally from its first byte to its last.
/// Every module that accepts, stages, embeds or sends a picture asks this, so there is one answer to "is this an
/// image, and which".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing anyone said about the file is an input.</strong> Not a declared media type, not an extension,
/// not a provider's label. The bytes decide (media.md, SEC-004).
/// </para>
/// <para>
/// <strong>Structural, not a render.</strong> Every PNG chunk is length-checked and its CRC verified through
/// <c>IEND</c>; every JPEG segment and its entropy-coded data are walked through <c>EOI</c>; every WebP RIFF chunk
/// is bounded by the RIFF size, which must be the file's; every GIF block is walked through the trailer. No pixel
/// is decoded, so a hostile file costs one linear pass and no allocation proportional to what it claims.
/// </para>
/// <para>
/// <strong>An image ends where its format says it ends.</strong> Anything after that is refused as
/// <see cref="ImageFileInspectionOutcome.TrailingData"/> — with one exception the 15.2 review approved: a JPEG may be
/// followed by further complete JPEGs, because that is how a gain map, a depth map or a multi-picture file is
/// written by the phones creators take photographs with. A motion photo's video tail is not a JPEG and is refused.
/// A ZIP directory reachable from the end of the file is refused for every format, because a ZIP's comment field
/// can swallow a valid image's closing bytes and make the end check alone pass.
/// </para>
/// <para>
/// What this cannot see is a payload hidden inside a well-formed ancillary chunk or application segment: that is a
/// valid image by its own format's rules, and only a re-encode removes it. Renditions are re-encoded, which is why
/// pages show those rather than originals.
/// </para>
/// </remarks>
public static class ImageFileInspector
{
    public const string PngMediaType = "image/png";

    public const string JpegMediaType = "image/jpeg";

    public const string WebpMediaType = "image/webp";

    public const string GifMediaType = "image/gif";

    /// <summary>Every media type this can establish.</summary>
    public static readonly IReadOnlySet<string> MediaTypes =
        new HashSet<string>(StringComparer.Ordinal) { PngMediaType, JpegMediaType, WebpMediaType, GifMediaType };

    private const int PngChunkOverhead = 12;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// What the first bytes claim to be, or null. A dispatch, not a verdict: only <see cref="Inspect"/> says
    /// whether the rest of the file agrees.
    /// </summary>
    public static string? MediaTypeOf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature))
        {
            return PngMediaType;
        }

        if (head.StartsWith(JpegSignature))
        {
            return JpegMediaType;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return WebpMediaType;
        }

        // Both header versions. 89a is what anything with animation or transparency writes; 87a still turns up in
        // older saved files.
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return GifMediaType;
        }

        return null;
    }

    /// <summary>Reads <paramref name="bytes"/> end to end, or says why it will not accept them.</summary>
    /// <param name="maxPixels">The caller's pixel cap. Each edge is held to it as well, before any multiplication.</param>
    public static ImageFileInspection Inspect(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPixels, 1);

        if (bytes.IsEmpty)
        {
            return new ImageFileInspection(ImageFileInspectionOutcome.Empty);
        }

        var inspection = MediaTypeOf(bytes) switch
        {
            PngMediaType => Png(bytes, maxPixels),
            JpegMediaType => Jpeg(bytes, maxPixels),
            WebpMediaType => Webp(bytes, maxPixels),
            GifMediaType => Gif(bytes, maxPixels),
            _ => new ImageFileInspection(ImageFileInspectionOutcome.Unsupported),
        };

        return inspection.Outcome == ImageFileInspectionOutcome.Accepted && ArchiveTrailer.EndsInZipDirectory(bytes)
            ? Trailing()
            : inspection;
    }

    /// <summary>
    /// Whether the PNG chunk at <paramref name="position"/>, of <paramref name="length"/> data bytes, carries the
    /// CRC its type and data compute to. The caller has bounded the chunk inside <paramref name="bytes"/>.
    /// </summary>
    public static bool PngChecksumHolds(ReadOnlySpan<byte> bytes, int position, int length)
    {
        var crc = uint.MaxValue;

        foreach (var value in bytes.Slice(position + 4, length + 4))
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return (crc ^ uint.MaxValue) == BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + 8 + length)..]);
    }

    /// <summary>Every chunk from the signature to <c>IEND</c>, which must be the file's last twelve bytes.</summary>
    private static ImageFileInspection Png(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        var position = PngSignature.Length;
        uint width = 0, height = 0;
        var frames = 1;
        bool headed = false, data = false, location = false;

        while (true)
        {
            if (bytes.Length - position < PngChunkOverhead)
            {
                return Corrupt();
            }

            var declared = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);

            if (declared > (uint)(bytes.Length - position - PngChunkOverhead))
            {
                return Corrupt();
            }

            var length = (int)declared;
            var type = bytes.Slice(position + 4, 4);

            if (!IsPngChunkName(type) || !PngChecksumHolds(bytes, position, length))
            {
                return Corrupt();
            }

            var payload = bytes.Slice(position + 8, length);
            var next = position + PngChunkOverhead + length;

            if (!headed)
            {
                // IHDR first, thirteen bytes, opening with the width and height.
                if (!type.SequenceEqual("IHDR"u8) || length != 13 || !IsPngHeaderSane(payload))
                {
                    return Corrupt();
                }

                width = BinaryPrimitives.ReadUInt32BigEndian(payload);
                height = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
                headed = true;

                if (IsTooLarge(width, height, maxPixels))
                {
                    return TooLarge();
                }
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return Corrupt();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data = true;
            }
            else if (type.SequenceEqual("acTL"u8))
            {
                // The animation control chunk precedes the image data, declares at least one frame, and is what
                // makes the file an APNG.
                var declaredFrames = length == 8 ? BinaryPrimitives.ReadUInt32BigEndian(payload) : 0;

                if (data || declaredFrames == 0)
                {
                    return Corrupt();
                }

                frames = (int)Math.Min(declaredFrames, int.MaxValue);
            }
            else if (type.SequenceEqual("eXIf"u8))
            {
                location |= ImageLocationMetadata.InExif(payload);
            }
            else if (type.SequenceEqual("iTXt"u8))
            {
                location |= ImageLocationMetadata.InPngText(payload);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !data)
                {
                    return Corrupt();
                }

                return next == bytes.Length
                    ? Measured(PngMediaType, width, height, maxPixels, frames, location)
                    : Trailing();
            }

            position = next;
        }
    }

    private static bool IsPngChunkName(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
        {
            if (!char.IsAsciiLetter((char)value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A bit depth the colour type allows, and the only compression and filter methods PNG defines.</summary>
    private static bool IsPngHeaderSane(ReadOnlySpan<byte> header)
    {
        int depth = header[8], colourType = header[9];

        var depthAllowed = colourType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => depth is 8 or 16,
            _ => false,
        };

        return depthAllowed && header[10] == 0 && header[11] == 0 && header[12] is 0 or 1;
    }

    /// <summary>
    /// The first image through <c>EOI</c>, then whatever follows it — which may only be further complete JPEGs.
    /// </summary>
    private static ImageFileInspection Jpeg(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        var first = WalkJpeg(bytes, maxPixels);

        if (first.Outcome != ImageFileInspectionOutcome.Accepted)
        {
            return new ImageFileInspection(first.Outcome);
        }

        var position = first.End;
        var location = first.HasLocationMetadata;

        while (position < bytes.Length)
        {
            if (!bytes[position..].StartsWith(JpegSignature))
            {
                return Trailing();
            }

            var appended = WalkJpeg(bytes[position..], maxPixels);

            if (appended.Outcome != ImageFileInspectionOutcome.Accepted)
            {
                return new ImageFileInspection(appended.Outcome);
            }

            // A gain map or a depth map is a picture in its own right, held to the same cap by the walk; one
            // that states no size is as corrupt as a first image that does not.
            if (appended.Width == 0 || appended.Height == 0)
            {
                return Corrupt();
            }

            // A gain map or depth map can carry its own EXIF, and a position there is still in the file.
            location |= appended.HasLocationMetadata;
            position += appended.End;
        }

        return Measured(JpegMediaType, first.Width, first.Height, maxPixels, 1, location);
    }

    private readonly record struct JpegWalk(
        ImageFileInspectionOutcome Outcome, int End = 0, uint Width = 0, uint Height = 0, bool HasLocationMetadata = false);

    /// <summary>
    /// One JPEG from <c>SOI</c> through <c>EOI</c>: every marker segment bounded by its declared length, and every
    /// scan's entropy-coded data run to the marker that ends it.
    /// </summary>
    /// <remarks>
    /// An image with no frame header or no scan is corrupt, and so is one that runs out of bytes before
    /// <c>EOI</c>. Bounded by the length of the file: every step moves forward by at least one byte.
    /// </remarks>
    private static JpegWalk WalkJpeg(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        var position = 2;
        uint width = 0, height = 0;
        bool framed = false, scanned = false, location = false;

        while (true)
        {
            if (position + 2 > bytes.Length || bytes[position] != 0xFF)
            {
                return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
            }

            var marker = bytes[position + 1];

            if (marker == 0xFF)
            {
                // A fill byte before the marker proper.
                position++;
                continue;
            }

            if (marker == 0xD9)
            {
                return framed && scanned
                    ? new JpegWalk(ImageFileInspectionOutcome.Accepted, position + 2, width, height, location)
                    : new JpegWalk(ImageFileInspectionOutcome.Corrupt);
            }

            // A stuffed zero outside entropy data, or a second start before this image has ended.
            if (marker is 0x00 or 0xD8)
            {
                return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (position + 4 > bytes.Length)
            {
                return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 2)..]);

            if (segmentLength < 2 || position + 2 + segmentLength > bytes.Length)
            {
                return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
            }

            var segment = bytes.Slice(position + 4, segmentLength - 2);

            // SOF0 to SOF15, less the three in that range that are tables rather than frames. The first frame
            // header is the image's size.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (segment.Length < 6)
                {
                    return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
                }

                if (!framed)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                    framed = true;

                    if (IsTooLarge(width, height, maxPixels))
                    {
                        return new JpegWalk(ImageFileInspectionOutcome.TooLarge);
                    }
                }
            }
            else if (marker == 0xE1)
            {
                location |= ImageLocationMetadata.InJpegApp1(segment);
            }

            position += 2 + segmentLength;

            if (marker == 0xDA)
            {
                if (!framed)
                {
                    return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
                }

                scanned = true;
                position = EndOfEntropyData(bytes, position);

                if (position < 0)
                {
                    return new JpegWalk(ImageFileInspectionOutcome.Corrupt);
                }
            }
        }
    }

    /// <summary>
    /// Where a scan's entropy-coded data ends: the first <c>0xFF</c> that is neither a stuffed zero nor a restart
    /// marker. Negative when the file ends first.
    /// </summary>
    private static int EndOfEntropyData(ReadOnlySpan<byte> bytes, int position)
    {
        while (true)
        {
            var found = bytes[position..].IndexOf((byte)0xFF);

            if (found < 0 || position + found + 1 >= bytes.Length)
            {
                return -1;
            }

            position += found;
            var next = bytes[position + 1];

            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                position += 2;
                continue;
            }

            return position;
        }
    }

    /// <summary>
    /// Every chunk inside the RIFF container, whose declared size must be exactly the rest of the file.
    /// </summary>
    /// <remarks>
    /// A simple file is one <c>VP8 </c> or <c>VP8L</c> chunk and nothing else. An extended file opens with
    /// <c>VP8X</c> and must carry image data — a bitstream chunk or animation frames — somewhere after it; chunks it
    /// does not know are allowed, as the container format requires, but are still bounded and padded.
    /// </remarks>
    private static ImageFileInspection Webp(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        if (bytes.Length < 20)
        {
            return Corrupt();
        }

        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);

        if (riffEnd > bytes.Length || riffEnd < 20)
        {
            return Corrupt();
        }

        var end = (int)riffEnd;
        var position = 12;
        uint width = 0, height = 0;
        var frames = 0;
        bool extended = false, image = false, location = false;

        while (position < end)
        {
            if (end - position < 8)
            {
                return Corrupt();
            }

            var fourCc = bytes.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);

            if (size > (uint)(end - position - 8))
            {
                return Corrupt();
            }

            var payload = bytes.Slice(position + 8, (int)size);
            var next = position + 8 + (int)size + (int)(size & 1);

            if (next > end)
            {
                return Corrupt();
            }

            if (position == 12)
            {
                if (fourCc.SequenceEqual("VP8 "u8))
                {
                    if (payload.Length < 10 || !payload[3..6].SequenceEqual((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A]))
                    {
                        return Corrupt();
                    }

                    width = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF);
                    height = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF);
                    image = true;
                }
                else if (fourCc.SequenceEqual("VP8L"u8))
                {
                    if (payload.Length < 5 || payload[0] != 0x2F)
                    {
                        return Corrupt();
                    }

                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                    width = (bits & 0x3FFF) + 1;
                    height = ((bits >> 14) & 0x3FFF) + 1;
                    image = true;
                }
                else if (fourCc.SequenceEqual("VP8X"u8))
                {
                    if (payload.Length < 10)
                    {
                        return Corrupt();
                    }

                    width = (uint)(payload[4] | (payload[5] << 8) | (payload[6] << 16)) + 1;
                    height = (uint)(payload[7] | (payload[8] << 8) | (payload[9] << 16)) + 1;
                    extended = true;
                }
                else
                {
                    return Corrupt();
                }

                if (IsTooLarge(width, height, maxPixels))
                {
                    return TooLarge();
                }
            }
            else if (!extended)
            {
                // A simple file is its one bitstream chunk. Anything after it is a payload riding inside the RIFF.
                return Corrupt();
            }
            else if (fourCc.SequenceEqual("VP8 "u8) || fourCc.SequenceEqual("VP8L"u8))
            {
                image = true;
            }
            else if (fourCc.SequenceEqual("ANMF"u8))
            {
                image = true;
                frames++;
            }
            else if (fourCc.SequenceEqual("EXIF"u8))
            {
                location |= ImageLocationMetadata.InExif(payload);
            }
            else if (fourCc.SequenceEqual("XMP "u8))
            {
                location |= ImageLocationMetadata.InXmp(payload);
            }

            position = next;
        }

        if (!image)
        {
            return Corrupt();
        }

        return end < bytes.Length
            ? Trailing()
            : Measured(WebpMediaType, width, height, maxPixels, Math.Max(frames, 1), location);
    }

    /// <summary>
    /// Every block from the logical screen descriptor through the trailer, which must be the file's last byte.
    /// </summary>
    /// <remarks>
    /// The size recorded is the logical screen, the canvas every frame shares. At least one image descriptor is
    /// required: a GIF with no frame is not a picture.
    /// </remarks>
    private static ImageFileInspection Gif(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        if (bytes.Length < 13)
        {
            return Corrupt();
        }

        uint width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        uint height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);

        // Header (6) plus logical screen descriptor (7). The packed byte's high bit says whether a global colour
        // table follows, and its low three bits give that table's size.
        var position = 13;

        if (IsTooLarge(width, height, maxPixels))
        {
            return TooLarge();
        }

        if (!SkipGifColourTable(bytes, bytes[10], ref position))
        {
            return Corrupt();
        }

        var frames = 0;

        while (position < bytes.Length)
        {
            switch (bytes[position++])
            {
                case 0x3B:
                    if (frames == 0)
                    {
                        return Corrupt();
                    }

                    return position == bytes.Length
                        ? Measured(GifMediaType, width, height, maxPixels, frames, false)
                        : Trailing();

                case 0x21:
                    // Extension: a label byte, then sub-blocks. Graphic control, comment, plain text and
                    // application blocks all take this shape.
                    if (position >= bytes.Length)
                    {
                        return Corrupt();
                    }

                    position++;

                    if (!SkipGifSubBlocks(bytes, ref position))
                    {
                        return Corrupt();
                    }

                    break;

                case 0x2C:
                    // Image descriptor: nine bytes, an optional local colour table, the LZW minimum code size,
                    // then the compressed data as sub-blocks.
                    if (position + 9 > bytes.Length)
                    {
                        return Corrupt();
                    }

                    var packed = bytes[position + 8];
                    position += 9;

                    if (!SkipGifColourTable(bytes, packed, ref position) || position >= bytes.Length)
                    {
                        return Corrupt();
                    }

                    position++;

                    if (!SkipGifSubBlocks(bytes, ref position))
                    {
                        return Corrupt();
                    }

                    frames++;
                    break;

                default:
                    return Corrupt();
            }
        }

        // Ran out of bytes before the trailer.
        return Corrupt();
    }

    private static bool SkipGifColourTable(ReadOnlySpan<byte> bytes, byte packed, ref int position)
    {
        if ((packed & 0x80) == 0)
        {
            return true;
        }

        position += (1 << ((packed & 0x07) + 1)) * 3;

        return position <= bytes.Length;
    }

    /// <summary>Skips a chain of length-prefixed sub-blocks, ending at the zero-length one.</summary>
    private static bool SkipGifSubBlocks(ReadOnlySpan<byte> bytes, ref int position)
    {
        while (position < bytes.Length)
        {
            var size = bytes[position++];

            if (size == 0)
            {
                return true;
            }

            position += size;
        }

        return false;
    }

    private static ImageFileInspection Measured(
        string mediaType, uint width, uint height, long maxPixels, int frames, bool location)
    {
        if (width == 0 || height == 0)
        {
            return Corrupt();
        }

        if (IsTooLarge(width, height, maxPixels))
        {
            return TooLarge();
        }

        return new ImageFileInspection(
            ImageFileInspectionOutcome.Accepted, mediaType, (int)width, (int)height, frames, location);
    }

    /// <summary>
    /// Whether a declared size is past the cap. Asked the moment a format states its size, so a few bytes claiming
    /// a vast canvas are refused as that rather than walked.
    /// </summary>
    /// <remarks>
    /// Each side on its own first: two sides near the top of 32 bits multiply past what a long holds, and the
    /// product would come back small or negative and pass. The int bound keeps the narrowing in
    /// <see cref="Measured"/> safe for a caller whose cap is wider than a row's int columns.
    /// </remarks>
    private static bool IsTooLarge(uint width, uint height, long maxPixels) =>
        width > maxPixels
        || height > maxPixels
        || width > int.MaxValue
        || height > int.MaxValue
        || (long)width * height > maxPixels;

    private static ImageFileInspection Corrupt() => new(ImageFileInspectionOutcome.Corrupt);

    private static ImageFileInspection TooLarge() => new(ImageFileInspectionOutcome.TooLarge);

    private static ImageFileInspection Trailing() => new(ImageFileInspectionOutcome.TrailingData);

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
