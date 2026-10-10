using System.Buffers.Binary;
using System.IO.Compression;

namespace CreatorPantry.Domain.Modules.Media.Managers;

public enum PngDecodeOutcome
{
    Decoded = 1,

    /// <summary>Not a PNG at all. Another format, or nothing.</summary>
    NotPng = 2,

    /// <summary>A PNG that does not hold together: a bad checksum, a bad length, missing or surplus data.</summary>
    Corrupt = 3,

    /// <summary>A valid PNG of a kind this does not read: interlaced, or fewer than 8 bits a sample.</summary>
    Unsupported = 4,

    /// <summary>More bytes, more pixels or a longer edge than a rendition may be made from.</summary>
    TooLarge = 5,
}

/// <param name="Pixels">Present exactly when the outcome is decoded.</param>
public readonly record struct PngDecodeResult(PngDecodeOutcome Outcome, PixelBuffer? Pixels = null);

/// <summary>
/// Reads a PNG into pixels with nothing but the framework: no package, no native codec.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it reads</strong> is the matrix B-28 approved (docs/architecture-decisions/ai-fluency.md):
/// non-interlaced, 8 or 16 bits a sample, every colour type, with <c>tRNS</c> honoured. Sixteen-bit samples
/// keep their high byte. Interlacing and the sub-byte depths are refused by name rather than attempted,
/// because the provider emits neither and a half-right decoder is worse than an honest refusal.
/// </para>
/// <para>
/// <strong>The file is a stranger's bytes.</strong> Nothing proportional to the picture is allocated until
/// the declared size has passed <see cref="MediaPolicy.RenditionMaxPixels"/> and
/// <see cref="MediaPolicy.RenditionMaxEdge"/>, so a hundred bytes declaring a vast canvas costs nothing.
/// The compressed data is then inflated one row at a time and only as far as that declared size: a stream
/// that would inflate past it is corrupt after one surplus byte, not after the memory is gone.
/// </para>
/// <para>
/// <strong>Every failure is a result.</strong> A bad checksum, a truncated chunk or invalid compressed data
/// comes back as an outcome. The one exception that leaves is <see cref="OperationCanceledException"/>,
/// which is the caller's own request.
/// </para>
/// <para>
/// It takes memory rather than a span because inflation needs a <see cref="Stream"/>, and a stream cannot
/// hold a span. The compressed chunks are read where they lie; they are never copied together.
/// </para>
/// </remarks>
public static class PngDecoder
{
    // The signature and the IHDR chunk, which the format requires to come first.
    private const int HeaderLength = 8 + 12 + HeaderDataLength;
    private const int HeaderDataLength = 13;
    private const int ChunkOverhead = 12;
    private const int MaxPaletteBytes = 256 * 3;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PngDecodeResult Decode(ReadOnlyMemory<byte> file, CancellationToken cancellationToken = default)
    {
        var bytes = file.Span;

        if (bytes.Length > MediaPolicy.ImageMaxBytes)
        {
            return Failed(PngDecodeOutcome.TooLarge);
        }

        if (!bytes.StartsWith(Signature))
        {
            return Failed(PngDecodeOutcome.NotPng);
        }

        var inspection = GeneratedImageInspector.Inspect(bytes);

        if (inspection.Outcome != GeneratedImageInspectionOutcome.Accepted)
        {
            return Failed(inspection.Outcome == GeneratedImageInspectionOutcome.TooLarge
                ? PngDecodeOutcome.TooLarge
                : PngDecodeOutcome.Corrupt);
        }

        if (bytes.Length < HeaderLength
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != HeaderDataLength
            || !ChecksumHolds(bytes, position: 8, HeaderDataLength))
        {
            return Failed(PngDecodeOutcome.Corrupt);
        }

        int width = inspection.Width, height = inspection.Height;

        if (width > MediaPolicy.RenditionMaxEdge
            || height > MediaPolicy.RenditionMaxEdge
            || (long)width * height > MediaPolicy.RenditionMaxPixels)
        {
            return Failed(PngDecodeOutcome.TooLarge);
        }

        int depth = bytes[24], colourType = bytes[25];
        var channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };

        // Compression, filter method and interlace, in that order. Zero is the only value of the first two
        // the format defines, and the only interlace this reads.
        if (bytes[26] != 0 || bytes[27] != 0 || bytes[28] != 0
            || channels == 0
            || !(depth == 8 || (depth == 16 && colourType != 3)))
        {
            return Failed(PngDecodeOutcome.Unsupported);
        }

        var compressed = new List<(int Offset, int Length)>();
        (int Offset, int Length)? palette = null, transparency = null;
        var position = HeaderLength;
        bool compressedClosed = false, ended = false;

        while (!ended)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bytes.Length - position < ChunkOverhead)
            {
                return Failed(PngDecodeOutcome.Corrupt);
            }

            var declared = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);

            if (declared > (uint)(bytes.Length - position - ChunkOverhead))
            {
                return Failed(PngDecodeOutcome.Corrupt);
            }

            var length = (int)declared;

            if (!ChecksumHolds(bytes, position, length))
            {
                return Failed(PngDecodeOutcome.Corrupt);
            }

            var type = bytes.Slice(position + 4, 4);
            var data = (Offset: position + 8, Length: length);

            if (type.SequenceEqual("IDAT"u8))
            {
                // The format requires the compressed chunks to be consecutive.
                if (compressedClosed)
                {
                    return Failed(PngDecodeOutcome.Corrupt);
                }

                compressed.Add(data);
            }
            else
            {
                var afterData = compressed.Count > 0;
                compressedClosed = afterData;

                if (type.SequenceEqual("IEND"u8))
                {
                    if (length != 0)
                    {
                        return Failed(PngDecodeOutcome.Corrupt);
                    }

                    ended = true;
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    if (palette is not null || transparency is not null || afterData
                        || length == 0 || length % 3 != 0 || length > MaxPaletteBytes)
                    {
                        return Failed(PngDecodeOutcome.Corrupt);
                    }

                    palette = data;
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    if (transparency is not null || afterData)
                    {
                        return Failed(PngDecodeOutcome.Corrupt);
                    }

                    transparency = data;
                }
                else if (type.SequenceEqual("IHDR"u8))
                {
                    return Failed(PngDecodeOutcome.Corrupt);
                }
                else if ((type[0] & 0x20) == 0)
                {
                    // An upper-case first letter marks a chunk a decoder may not skip. One this does not
                    // know means the picture cannot be rendered correctly without it.
                    return Failed(PngDecodeOutcome.Unsupported);
                }
            }

            position += ChunkOverhead + length;
        }

        var sampleBytes = depth / 8;

        var transparencyHolds = colourType switch
        {
            3 => palette is not null && (transparency is null || transparency.Value.Length <= palette.Value.Length / 3),
            0 => transparency is null || transparency.Value.Length == 2,
            2 => transparency is null || transparency.Value.Length == 6,
            _ => transparency is null,
        };

        if (compressed.Count == 0 || !transparencyHolds)
        {
            return Failed(PngDecodeOutcome.Corrupt);
        }

        // Every allocation that scales with the picture is below this line, and each is bounded by the caps
        // checked above: the output by the pixel cap, a row by the edge cap.
        var pixelBytes = channels * sampleBytes;
        var stride = width * pixelBytes;
        var rgba = new byte[width * height * PixelBuffer.BytesPerPixel];
        byte[] current = new byte[stride], previous = new byte[stride];
        var paletteBytes = palette is { } p ? bytes.Slice(p.Offset, p.Length) : default;
        var transparencyBytes = transparency is { } t ? bytes.Slice(t.Offset, t.Length) : default;
        var hasTransparency = false;

        try
        {
            using var inflated = new ZLibStream(new ChunkStream(file, compressed), CompressionMode.Decompress);
            Span<byte> filter = stackalloc byte[1];

            for (var row = 0; row < height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Fill(inflated, filter)
                    || !Fill(inflated, current)
                    || !Unfilter(filter[0], current, previous, pixelBytes)
                    || !Expand(
                        current,
                        rgba.AsSpan(row * width * PixelBuffer.BytesPerPixel, width * PixelBuffer.BytesPerPixel),
                        colourType,
                        sampleBytes,
                        paletteBytes,
                        transparencyBytes,
                        ref hasTransparency))
                {
                    return Failed(PngDecodeOutcome.Corrupt);
                }

                (current, previous) = (previous, current);
            }

            // The declared size is the whole of what may be inflated. Anything beyond it is not this
            // picture, and reading on to find out how much there is would be the bomb going off.
            if (inflated.Read(filter) != 0)
            {
                return Failed(PngDecodeOutcome.Corrupt);
            }
        }
        catch (InvalidDataException)
        {
            return Failed(PngDecodeOutcome.Corrupt);
        }

        return new PngDecodeResult(PngDecodeOutcome.Decoded, new PixelBuffer(width, height, rgba, hasTransparency));
    }

    private static PngDecodeResult Failed(PngDecodeOutcome outcome) => new(outcome);

    /// <summary>Reads until <paramref name="destination"/> is full, or says the data ran out first.</summary>
    private static bool Fill(Stream source, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = source.Read(destination);

            if (read == 0)
            {
                return false;
            }

            destination = destination[read..];
        }

        return true;
    }

    /// <summary>
    /// Undoes one row's filter in place. <paramref name="previous"/> is the row above, already unfiltered,
    /// and all zeroes above the first row — which is what the format says a missing neighbour is.
    /// </summary>
    private static bool Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int pixelBytes)
    {
        switch (filter)
        {
            case 0:
                return true;

            case 1:
                for (var i = pixelBytes; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - pixelBytes]);
                }

                return true;

            case 2:
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + previous[i]);
                }

                return true;

            case 3:
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= pixelBytes ? row[i - pixelBytes] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }

                return true;

            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int left = 0, upperLeft = 0;

                    if (i >= pixelBytes)
                    {
                        left = row[i - pixelBytes];
                        upperLeft = previous[i - pixelBytes];
                    }

                    row[i] = (byte)(row[i] + Paeth(left, previous[i], upperLeft));
                }

                return true;

            default:
                return false;
        }
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        int toLeft = Math.Abs(estimate - left), toAbove = Math.Abs(estimate - above), toUpperLeft = Math.Abs(estimate - upperLeft);

        return toLeft <= toAbove && toLeft <= toUpperLeft ? left
            : toAbove <= toUpperLeft ? above
            : upperLeft;
    }

    /// <summary>
    /// Turns one unfiltered row into RGBA. False only for a palette index with no palette entry behind it.
    /// </summary>
    /// <remarks>
    /// A sixteen-bit sample is big-endian, so its high byte is the first one and <paramref name="sampleBytes"/>
    /// is the step to the next sample. A <c>tRNS</c> colour key is matched against the whole sample, both
    /// bytes of it, before the low byte is dropped.
    /// </remarks>
    private static bool Expand(
        ReadOnlySpan<byte> row,
        Span<byte> destination,
        int colourType,
        int sampleBytes,
        ReadOnlySpan<byte> palette,
        ReadOnlySpan<byte> transparency,
        ref bool hasTransparency)
    {
        var step = (colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 }) * sampleBytes;
        var key = transparency.IsEmpty || colourType == 3
            ? default
            : sampleBytes == 2 ? transparency : KeyLowBytes(transparency, stackalloc byte[3]);

        for (int source = 0, target = 0; source < row.Length; source += step, target += PixelBuffer.BytesPerPixel)
        {
            byte red, green, blue, alpha = byte.MaxValue;

            switch (colourType)
            {
                case 0:
                    red = green = blue = row[source];
                    break;

                case 2:
                    red = row[source];
                    green = row[source + sampleBytes];
                    blue = row[source + (2 * sampleBytes)];
                    break;

                case 3:
                    var entry = row[source] * 3;

                    if (entry >= palette.Length)
                    {
                        return false;
                    }

                    red = palette[entry];
                    green = palette[entry + 1];
                    blue = palette[entry + 2];
                    alpha = row[source] < transparency.Length ? transparency[row[source]] : byte.MaxValue;
                    break;

                case 4:
                    red = green = blue = row[source];
                    alpha = row[source + sampleBytes];
                    break;

                default:
                    red = row[source];
                    green = row[source + sampleBytes];
                    blue = row[source + (2 * sampleBytes)];
                    alpha = row[source + (3 * sampleBytes)];
                    break;
            }

            if (!key.IsEmpty && row.Slice(source, key.Length).SequenceEqual(key))
            {
                alpha = 0;
            }

            destination[target] = red;
            destination[target + 1] = green;
            destination[target + 2] = blue;
            destination[target + 3] = alpha;
            hasTransparency |= alpha != byte.MaxValue;
        }

        return true;
    }

    /// <summary>
    /// A colour key is always stored as sixteen-bit samples. For an eight-bit picture only the low byte of
    /// each means anything, so the key is narrowed to the shape a pixel has in the row.
    /// </summary>
    private static ReadOnlySpan<byte> KeyLowBytes(ReadOnlySpan<byte> transparency, Span<byte> narrowed)
    {
        var samples = transparency.Length / 2;

        for (var i = 0; i < samples; i++)
        {
            narrowed[i] = transparency[(i * 2) + 1];
        }

        return narrowed[..samples];
    }

    /// <summary>
    /// Whether the chunk at <paramref name="position"/> carries the checksum its type and data produce.
    /// </summary>
    /// <remarks>
    /// CRC-32 by hand because <c>System.IO.Hashing</c> is a package, and B-28 is the decision not to take one.
    /// </remarks>
    private static bool ChecksumHolds(ReadOnlySpan<byte> bytes, int position, int length)
    {
        var crc = uint.MaxValue;

        foreach (var value in bytes.Slice(position + 4, 4 + length))
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return (crc ^ uint.MaxValue) == BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + 8 + length)..]);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint entry = 0; entry < table.Length; entry++)
        {
            var value = entry;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[entry] = value;
        }

        return table;
    }

    /// <summary>
    /// The compressed chunks of one file, read end to end as the single stream they are, in place.
    /// </summary>
    private sealed class ChunkStream(ReadOnlyMemory<byte> file, List<(int Offset, int Length)> chunks) : Stream
    {
        private int chunk;
        private int consumed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (chunk < chunks.Count)
            {
                var (offset, length) = chunks[chunk];
                var available = length - consumed;

                if (available == 0)
                {
                    chunk++;
                    consumed = 0;
                    continue;
                }

                var taken = Math.Min(available, buffer.Length);
                file.Span.Slice(offset + consumed, taken).CopyTo(buffer);
                consumed += taken;
                return taken;
            }

            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
