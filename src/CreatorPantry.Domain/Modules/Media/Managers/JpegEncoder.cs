using System.Numerics;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>How much of the colour detail a JPEG keeps beside its brightness detail.</summary>
public enum JpegChromaSubsampling
{
    /// <summary>4:2:0. One colour sample for every four pixels; what B-28's renditions use.</summary>
    Quarter = 1,

    /// <summary>4:4:4. A colour sample for every pixel, for about a quarter more bytes.</summary>
    Full = 2,
}

public enum JpegEncodeOutcome
{
    Encoded = 1,

    /// <summary>
    /// The picture has a pixel that is not fully opaque. A JPEG cannot carry that, and nothing was encoded.
    /// </summary>
    HasTransparency = 2,

    /// <summary>A side longer than the 65,535 pixels a JPEG frame header can state.</summary>
    TooLarge = 3,
}

/// <param name="Bytes">Present exactly when the outcome is encoded.</param>
public readonly record struct JpegEncodeResult(JpegEncodeOutcome Outcome, byte[]? Bytes = null);

/// <summary>
/// Writes a baseline JFIF JPEG with nothing but the framework: no package, no native codec.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Baseline, sequential, Huffman, eight bits</strong> — the one kind of JPEG every decoder reads.
/// The quantisation and Huffman tables are the ones the standard prints in Annex K, the former scaled by
/// quality the way libjpeg scales them, so a quality here means what it means everywhere else. There is no
/// progressive mode and there are no optimised tables; B-28 records both as left out.
/// </para>
/// <para>
/// <strong>It will not decide what transparency becomes.</strong> A JPEG has no alpha, and flattening a
/// picture onto a colour is a decision about a creator's work. A buffer with any non-opaque pixel is
/// reported and nothing is encoded; the rendition policy chooses what to do instead.
/// </para>
/// <para>
/// <strong>Nothing but pixels goes in the file.</strong> No EXIF, no comment, no colour profile, so a
/// rendition cannot carry a prompt or a location out of the workspace.
/// </para>
/// <para>
/// <strong>The same pixels and settings always give the same bytes,</strong> which is what lets a retried
/// rendition job be recognised as the one that already ran. The picture is converted a band of blocks at a
/// time, so the working memory is a few rows however large the picture is.
/// </para>
/// </remarks>
public static class JpegEncoder
{
    public const int MinQuality = 1;

    public const int MaxQuality = 100;

    private const int BlockSize = 8;
    private const int BlockLength = BlockSize * BlockSize;
    private const int MaxDimension = 65_535;

    /// <summary>The order coefficients are written in: low frequencies first, along the diagonals.</summary>
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // ITU-T T.81 Annex K, tables K.1 and K.2.
    private static readonly byte[] LuminanceQuantisation =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChrominanceQuantisation =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // Annex K, tables K.3 to K.6: how many codes there are of each length, then the symbols in code order.
    private static readonly HuffmanTable LuminanceDc = new(
        [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

    private static readonly HuffmanTable ChrominanceDc = new(
        [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

    private static readonly HuffmanTable LuminanceAc = new(
        [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D],
        [
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        ]);

    private static readonly HuffmanTable ChrominanceAc = new(
        [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77],
        [
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        ]);

    /// <summary>The cosine transform's basis, row per frequency, scaled so the transform is orthonormal.</summary>
    private static readonly float[] Basis = BuildBasis();

    public static JpegEncodeResult Encode(
        PixelBuffer pixels,
        int quality,
        JpegChromaSubsampling subsampling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, MinQuality);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, MaxQuality);

        if (!Enum.IsDefined(subsampling))
        {
            throw new ArgumentOutOfRangeException(nameof(subsampling));
        }

        if (pixels.HasTransparency)
        {
            return new JpegEncodeResult(JpegEncodeOutcome.HasTransparency);
        }

        int width = pixels.Width, height = pixels.Height;

        if (width > MaxDimension || height > MaxDimension)
        {
            return new JpegEncodeResult(JpegEncodeOutcome.TooLarge);
        }

        // A unit is the smallest piece that holds a whole number of blocks of every component: 16 pixels
        // square when colour is quartered, 8 when it is not.
        var sampling = subsampling == JpegChromaSubsampling.Quarter ? 2 : 1;
        var unit = BlockSize * sampling;
        var paddedWidth = (width + unit - 1) / unit * unit;
        var chromaWidth = paddedWidth / sampling;

        var luminanceSteps = ScaledTable(LuminanceQuantisation, quality);
        var chrominanceSteps = ScaledTable(ChrominanceQuantisation, quality);

        // A guess at the size, to save the buffer growing: a quarter of a byte a pixel is generous for a photograph.
        var output = new BitWriter((int)Math.Clamp((long)width * height / 4, 1024, 16 * 1024 * 1024));
        WriteHeaders(output, width, height, sampling, luminanceSteps, chrominanceSteps);

        // One band of units at a time: brightness at full size, the two colour planes at theirs.
        float[] luminance = new float[paddedWidth * unit];
        float[] blueness = new float[chromaWidth * BlockSize], redness = new float[chromaWidth * BlockSize];
        Span<float> scratch = stackalloc float[BlockLength * 2];
        int luminanceDc = 0, bluenessDc = 0, rednessDc = 0;

        for (var top = 0; top < height; top += unit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FillBand(pixels, top, unit, sampling, paddedWidth, luminance, blueness, redness);

            for (var left = 0; left < paddedWidth; left += unit)
            {
                for (var blockRow = 0; blockRow < sampling; blockRow++)
                {
                    for (var blockColumn = 0; blockColumn < sampling; blockColumn++)
                    {
                        luminanceDc = WriteBlock(
                            output, luminance, paddedWidth, left + (blockColumn * BlockSize), blockRow * BlockSize,
                            luminanceSteps, LuminanceDc, LuminanceAc, luminanceDc, scratch);
                    }
                }

                bluenessDc = WriteBlock(
                    output, blueness, chromaWidth, left / sampling, 0,
                    chrominanceSteps, ChrominanceDc, ChrominanceAc, bluenessDc, scratch);
                rednessDc = WriteBlock(
                    output, redness, chromaWidth, left / sampling, 0,
                    chrominanceSteps, ChrominanceDc, ChrominanceAc, rednessDc, scratch);
            }
        }

        output.FlushBits();
        output.Marker(0xD9);

        return new JpegEncodeResult(JpegEncodeOutcome.Encoded, output.ToArray());
    }

    /// <summary>
    /// Converts one band of the picture to brightness and two colour differences, as JFIF defines them.
    /// </summary>
    /// <remarks>
    /// Past the right and bottom edges the last column and row are repeated. Blocks have to be whole, and
    /// padding with the edge's own colour is what keeps the padding from showing as a fringe on it.
    /// </remarks>
    private static void FillBand(
        PixelBuffer pixels, int top, int unit, int sampling, int paddedWidth,
        float[] luminance, float[] blueness, float[] redness)
    {
        var rgba = pixels.Rgba;
        var chromaWidth = paddedWidth / sampling;
        var share = 1f / (sampling * sampling);
        Array.Clear(blueness);
        Array.Clear(redness);

        for (var row = 0; row < unit; row++)
        {
            var sourceRow = Math.Min(top + row, pixels.Height - 1) * pixels.Width;
            var chromaRow = row / sampling * chromaWidth;

            for (var column = 0; column < paddedWidth; column++)
            {
                var at = (sourceRow + Math.Min(column, pixels.Width - 1)) * PixelBuffer.BytesPerPixel;
                float red = rgba[at], green = rgba[at + 1], blue = rgba[at + 2];

                // Centred on zero, which is what the transform expects and why 128 is taken off.
                luminance[(row * paddedWidth) + column] = (0.299f * red) + (0.587f * green) + (0.114f * blue) - 128f;
                blueness[chromaRow + (column / sampling)] += ((-0.168736f * red) - (0.331264f * green) + (0.5f * blue)) * share;
                redness[chromaRow + (column / sampling)] += ((0.5f * red) - (0.418688f * green) - (0.081312f * blue)) * share;
            }
        }
    }

    /// <summary>
    /// Transforms, quantises and entropy-codes one block, and returns its DC value for the next block of
    /// the same component to be coded against.
    /// </summary>
    private static int WriteBlock(
        BitWriter output, float[] plane, int stride, int left, int top,
        byte[] steps, HuffmanTable dcTable, HuffmanTable acTable, int previousDc, Span<float> scratch)
    {
        var half = scratch[..BlockLength];
        var coefficients = scratch[BlockLength..];

        // Along the rows, then down the columns.
        for (var y = 0; y < BlockSize; y++)
        {
            var row = plane.AsSpan(((top + y) * stride) + left, BlockSize);

            for (var u = 0; u < BlockSize; u++)
            {
                var basis = Basis.AsSpan(u * BlockSize, BlockSize);
                float sum = 0;

                for (var x = 0; x < BlockSize; x++)
                {
                    sum += row[x] * basis[x];
                }

                half[(y * BlockSize) + u] = sum;
            }
        }

        for (var v = 0; v < BlockSize; v++)
        {
            var basis = Basis.AsSpan(v * BlockSize, BlockSize);

            for (var u = 0; u < BlockSize; u++)
            {
                float sum = 0;

                for (var y = 0; y < BlockSize; y++)
                {
                    sum += half[(y * BlockSize) + u] * basis[y];
                }

                coefficients[(v * BlockSize) + u] = sum;
            }
        }

        // The DC coefficient is the block's average, and neighbours are alike, so only the change is coded.
        var dc = (int)MathF.Round(coefficients[0] / steps[0]);
        WriteValue(output, dcTable, run: 0, dc - previousDc);

        var zeroes = 0;

        for (var i = 1; i < BlockLength; i++)
        {
            var position = ZigZag[i];
            var value = (int)MathF.Round(coefficients[position] / steps[position]);

            if (value == 0)
            {
                zeroes++;
                continue;
            }

            // A run longer than a symbol can state is written as sixteen zeroes at a time.
            for (; zeroes > 15; zeroes -= 16)
            {
                output.Bits(acTable.Code[0xF0], acTable.Length[0xF0]);
            }

            WriteValue(output, acTable, zeroes, value);
            zeroes = 0;
        }

        if (zeroes > 0)
        {
            // End of block: everything left is zero.
            output.Bits(acTable.Code[0x00], acTable.Length[0x00]);
        }

        return dc;
    }

    /// <summary>
    /// One coefficient: a Huffman symbol for the zeroes before it and how many bits it needs, then those bits.
    /// </summary>
    private static void WriteValue(BitWriter output, HuffmanTable table, int run, int value)
    {
        var size = 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));
        var symbol = (run << 4) | size;
        output.Bits(table.Code[symbol], table.Length[symbol]);

        if (size > 0)
        {
            // A negative value is written as one less than itself, so its top bit is a zero.
            output.Bits((value < 0 ? value - 1 : value) & ((1 << size) - 1), size);
        }
    }

    private static void WriteHeaders(
        BitWriter output, int width, int height, int sampling, byte[] luminanceSteps, byte[] chrominanceSteps)
    {
        output.Marker(0xD8);

        // JFIF 1.01, no units, square pixels, no thumbnail.
        output.Marker(0xE0);
        output.Word(16);
        output.Bytes("JFIF\0"u8);
        output.Bytes([1, 1, 0, 0, 1, 0, 1, 0, 0]);

        WriteQuantisation(output, 0, luminanceSteps);
        WriteQuantisation(output, 1, chrominanceSteps);

        // Baseline frame: eight bits a sample, three components, the brightness one at the larger scale.
        output.Marker(0xC0);
        output.Word(17);
        output.Byte(8);
        output.Word(height);
        output.Word(width);
        output.Bytes([3, 1, (byte)((sampling << 4) | sampling), 0, 2, 0x11, 1, 3, 0x11, 1]);

        WriteHuffman(output, 0x00, LuminanceDc);
        WriteHuffman(output, 0x10, LuminanceAc);
        WriteHuffman(output, 0x01, ChrominanceDc);
        WriteHuffman(output, 0x11, ChrominanceAc);

        // One scan of all three components, the whole spectrum of each.
        output.Marker(0xDA);
        output.Word(12);
        output.Bytes([3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
    }

    private static void WriteQuantisation(BitWriter output, byte id, byte[] steps)
    {
        output.Marker(0xDB);
        output.Word(3 + BlockLength);
        output.Byte(id);

        foreach (var position in ZigZag)
        {
            output.Byte(steps[position]);
        }
    }

    private static void WriteHuffman(BitWriter output, byte id, HuffmanTable table)
    {
        output.Marker(0xC4);
        output.Word(3 + table.Counts.Length + table.Symbols.Length);
        output.Byte(id);
        output.Bytes(table.Counts);
        output.Bytes(table.Symbols);
    }

    /// <summary>
    /// A published table scaled by quality the way libjpeg does it: 50 is the table as printed, 100 is
    /// every step at one, and below 50 the steps grow quickly.
    /// </summary>
    private static byte[] ScaledTable(byte[] published, int quality)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var steps = new byte[BlockLength];

        for (var i = 0; i < steps.Length; i++)
        {
            steps[i] = (byte)Math.Clamp(((published[i] * scale) + 50) / 100, 1, byte.MaxValue);
        }

        return steps;
    }

    private static float[] BuildBasis()
    {
        var basis = new float[BlockLength];

        for (var frequency = 0; frequency < BlockSize; frequency++)
        {
            var scale = 0.5 * (frequency == 0 ? Math.Sqrt(0.5) : 1.0);

            for (var position = 0; position < BlockSize; position++)
            {
                basis[(frequency * BlockSize) + position] =
                    (float)(scale * Math.Cos(((2 * position) + 1) * frequency * Math.PI / 16));
            }
        }

        return basis;
    }

    /// <summary>A Huffman table as the file states it, and as the codes that statement implies.</summary>
    private sealed class HuffmanTable
    {
        public HuffmanTable(byte[] counts, byte[] symbols)
        {
            Counts = counts;
            Symbols = symbols;

            // Codes of each length are consecutive, and each length starts where the last left off, doubled.
            int code = 0, next = 0;

            for (var length = 1; length <= counts.Length; length++)
            {
                for (var i = 0; i < counts[length - 1]; i++, next++)
                {
                    Code[symbols[next]] = (ushort)code++;
                    Length[symbols[next]] = (byte)length;
                }

                code <<= 1;
            }
        }

        public byte[] Counts { get; }

        public byte[] Symbols { get; }

        public ushort[] Code { get; } = new ushort[256];

        public byte[] Length { get; } = new byte[256];
    }

    /// <summary>The output: whole bytes for the headers, and a bit stream for the picture data.</summary>
    private sealed class BitWriter(int capacity)
    {
        private byte[] buffer = new byte[capacity];
        private int length;
        private ulong pending;
        private int pendingBits;

        public void Byte(int value)
        {
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            buffer[length++] = (byte)value;
        }

        public void Word(int value)
        {
            Byte(value >> 8);
            Byte(value);
        }

        public void Marker(byte code)
        {
            Byte(0xFF);
            Byte(code);
        }

        public void Bytes(ReadOnlySpan<byte> values)
        {
            foreach (var value in values)
            {
                Byte(value);
            }
        }

        public void Bits(int value, int count)
        {
            pending = (pending << count) | (uint)value;
            pendingBits += count;

            while (pendingBits >= 8)
            {
                var whole = (int)(pending >> (pendingBits - 8)) & 0xFF;
                Byte(whole);

                // A 0xFF in the data would read as the start of a marker, so it is followed by a zero.
                if (whole == 0xFF)
                {
                    Byte(0);
                }

                pendingBits -= 8;
            }

            pending &= (1UL << pendingBits) - 1;
        }

        /// <summary>
        /// Fills the last byte with ones, as the standard requires. No code is all ones, so a decoder cannot
        /// read the padding as one more coefficient.
        /// </summary>
        public void FlushBits()
        {
            if (pendingBits > 0)
            {
                Bits((1 << (8 - pendingBits)) - 1, 8 - pendingBits);
            }
        }

        public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
    }
}
