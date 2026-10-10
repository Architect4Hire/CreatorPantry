using System.Buffers.Binary;
using System.IO.Compression;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Writes real PNG files for the decoder's tests, one decision at a time.
/// </summary>
/// <remarks>
/// A second implementation of the format on purpose: its filters and its checksum are written here rather
/// than borrowed from the decoder, so a mistake both sides share has to be made twice. It will also write a
/// file that is wrong in exactly one stated way, which is what most of the decoder's tests need.
/// </remarks>
public sealed class PngSampleFile
{
    public uint Width { get; init; } = 1;

    public uint Height { get; init; } = 1;

    public byte Depth { get; init; } = 8;

    public byte ColourType { get; init; } = 6;

    public byte Interlace { get; init; }

    /// <summary>The unfiltered sample bytes of every row, top to bottom, with no filter bytes.</summary>
    public byte[] Samples { get; init; } = [];

    /// <summary>The filter each row is written with, by row number.</summary>
    public Func<int, byte> Filter { get; init; } = _ => 0;

    public byte[]? Palette { get; init; }

    public byte[]? Transparency { get; init; }

    /// <summary>How many chunks the compressed data is split across.</summary>
    public int DataChunks { get; init; } = 1;

    /// <summary>Extra chunks written between the header and the picture data.</summary>
    public IReadOnlyList<(string Type, byte[] Data)> BeforeData { get; init; } = [];

    /// <summary>An extra chunk written between two data chunks, which the format forbids.</summary>
    public (string Type, byte[] Data)? BetweenData { get; init; }

    /// <summary>The filtered rows to compress instead of the ones <see cref="Samples"/> would produce.</summary>
    public byte[]? Scanlines { get; init; }

    /// <summary>The compressed bytes to write instead of compressing anything.</summary>
    public byte[]? CompressedData { get; init; }

    public bool OmitData { get; init; }

    public int Channels => ColourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };

    public byte[] Build()
    {
        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Height);
        header[8] = Depth;
        header[9] = ColourType;
        header[12] = Interlace;
        Chunk(file, "IHDR", header);

        if (Palette is not null)
        {
            Chunk(file, "PLTE", Palette);
        }

        if (Transparency is not null)
        {
            Chunk(file, "tRNS", Transparency);
        }

        foreach (var (type, data) in BeforeData)
        {
            Chunk(file, type, data);
        }

        if (!OmitData)
        {
            var compressed = CompressedData ?? Compress(Scanlines ?? FilteredScanlines());
            var size = Math.Max(1, (compressed.Length + DataChunks - 1) / DataChunks);

            for (var at = 0; at < compressed.Length; at += size)
            {
                if (at > 0 && BetweenData is { } between)
                {
                    Chunk(file, between.Type, between.Data);
                }

                Chunk(file, "IDAT", compressed.AsSpan(at, Math.Min(size, compressed.Length - at)));
            }
        }

        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    public static byte[] Compress(ReadOnlySpan<byte> bytes)
    {
        using var packed = new MemoryStream();

        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return packed.ToArray();
    }

    /// <summary>Every row of <see cref="Samples"/> behind its filter byte, filtered as that byte says.</summary>
    public byte[] FilteredScanlines()
    {
        var pixelBytes = Channels * Depth / 8;
        var stride = (int)Width * pixelBytes;
        var scanlines = new byte[(int)Height * (stride + 1)];

        for (var row = 0; row < Height; row++)
        {
            var filter = Filter(row);
            scanlines[row * (stride + 1)] = filter;

            for (var i = 0; i < stride; i++)
            {
                int left = i >= pixelBytes ? Samples[(row * stride) + i - pixelBytes] : 0;
                int above = row > 0 ? Samples[((row - 1) * stride) + i] : 0;
                int upperLeft = row > 0 && i >= pixelBytes ? Samples[((row - 1) * stride) + i - pixelBytes] : 0;

                var predicted = filter switch
                {
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Nearest(left, above, upperLeft),
                    _ => 0,
                };

                scanlines[(row * (stride + 1)) + 1 + i] = (byte)(Samples[(row * stride) + i] - predicted);
            }
        }

        return scanlines;
    }

    private static int Nearest(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        var candidates = new[] { left, above, upperLeft };

        // Ties go to the earliest of left, above, upper-left, which is the order the format gives.
        return candidates.MinBy(candidate => Math.Abs(estimate - candidate));
    }

    private static void Chunk(Stream file, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body.AsSpan(4));

        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        file.Write(word);
        file.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, Checksum(body));
        file.Write(word);
    }

    /// <summary>CRC-32, bit by bit rather than by table — slow, and obviously the polynomial.</summary>
    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in bytes)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
