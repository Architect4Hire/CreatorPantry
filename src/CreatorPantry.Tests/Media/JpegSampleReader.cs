using System.Buffers.Binary;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Reads a baseline JPEG back, for the encoder's tests: its structure, and its pixels.
/// </summary>
/// <remarks>
/// <para>
/// A decoder written from the standard rather than from the encoder, so the encoder is checked against
/// something that does not share its tables or its arithmetic: the Huffman codes are rebuilt from what the
/// file declares, and the inverse transform is the textbook double sum.
/// </para>
/// <para>
/// It is strict where a browser is forgiving. Anything the standard does not allow in a baseline file —
/// an unstuffed 0xFF, data after the last block, padding that is not ones, a code no table defines — is an
/// <see cref="InvalidDataException"/>, which is how a test learns the stream is not conformant.
/// </para>
/// </remarks>
public sealed class JpegSampleReader
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private JpegSampleReader()
    {
    }

    /// <summary>Every segment in file order, by its usual name.</summary>
    public List<string> Segments { get; } = [];

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Precision { get; private set; }

    /// <summary>The APP0 payload, after its length.</summary>
    public byte[] Application { get; private set; } = [];

    public List<(int Id, int Horizontal, int Vertical, int Table)> Components { get; } = [];

    /// <summary>Quantisation tables by id, in natural (row by row) order.</summary>
    public Dictionary<int, byte[]> Quantisation { get; } = [];

    /// <summary>Huffman tables keyed as the file keys them: class in the high nibble, id in the low.</summary>
    public Dictionary<int, (byte[] Counts, byte[] Symbols)> Huffman { get; } = [];

    /// <summary>The decoded picture, three bytes a pixel.</summary>
    public byte[] Rgb { get; private set; } = [];

    public (byte R, byte G, byte B) Pixel(int x, int y) =>
        (Rgb[((y * Width) + x) * 3], Rgb[(((y * Width) + x) * 3) + 1], Rgb[(((y * Width) + x) * 3) + 2]);

    public static JpegSampleReader Read(byte[] file)
    {
        var reader = new JpegSampleReader();
        Require(file.Length >= 4 && file[0] == 0xFF && file[1] == 0xD8, "no start of image");
        reader.Segments.Add("SOI");
        var position = 2;
        (int Component, int Tables)[] scan = [];

        while (scan.Length == 0)
        {
            Require(position + 4 <= file.Length && file[position] == 0xFF, "a segment does not start with a marker");
            var marker = file[position + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(position + 2));
            Require(length >= 2 && position + 2 + length <= file.Length, "a segment runs past the file");
            var body = file.AsSpan(position + 4, length - 2);

            switch (marker)
            {
                case 0xE0:
                    reader.Segments.Add("APP0");
                    reader.Application = body.ToArray();
                    break;

                case 0xDB:
                    reader.Segments.Add("DQT");
                    Require(body.Length == 65 && body[0] >> 4 == 0, "not one eight-bit quantisation table");
                    var table = new byte[64];

                    for (var i = 0; i < 64; i++)
                    {
                        table[ZigZag[i]] = body[1 + i];
                    }

                    reader.Quantisation[body[0]] = table;
                    break;

                case 0xC0:
                    reader.Segments.Add("SOF0");
                    reader.Precision = body[0];
                    reader.Height = BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
                    reader.Width = BinaryPrimitives.ReadUInt16BigEndian(body[3..]);
                    Require(body.Length == 6 + (body[5] * 3), "the frame header is the wrong length");

                    for (var i = 0; i < body[5]; i++)
                    {
                        var component = body.Slice(6 + (i * 3), 3);
                        reader.Components.Add((component[0], component[1] >> 4, component[1] & 15, component[2]));
                    }

                    break;

                case 0xC4:
                    reader.Segments.Add("DHT");
                    var counts = body.Slice(1, 16).ToArray();
                    Require(body.Length == 17 + counts.Sum(count => count), "a Huffman table is the wrong length");
                    reader.Huffman[body[0]] = (counts, body[17..].ToArray());
                    break;

                case 0xDA:
                    reader.Segments.Add("SOS");
                    Require(body.Length == 4 + (body[0] * 2), "the scan header is the wrong length");
                    Require(body[^3] == 0 && body[^2] == 63 && body[^1] == 0, "not a whole-spectrum baseline scan");
                    scan = new (int, int)[body[0]];

                    for (var i = 0; i < scan.Length; i++)
                    {
                        scan[i] = (body[1 + (i * 2)], body[2 + (i * 2)]);
                    }

                    break;

                default:
                    throw new InvalidDataException($"an unexpected marker 0x{marker:X2}");
            }

            position += 2 + length;
        }

        reader.Decode(Unstuff(file, position), scan);
        reader.Segments.Add("EOI");
        return reader;
    }

    /// <summary>The scan's bytes with the stuffing taken out, up to an end marker that must close the file.</summary>
    private static byte[] Unstuff(byte[] file, int position)
    {
        var data = new List<byte>(file.Length - position);

        while (true)
        {
            Require(position < file.Length, "the scan has no end marker");
            var value = file[position++];

            if (value != 0xFF)
            {
                data.Add(value);
                continue;
            }

            Require(position < file.Length, "the scan ends on half a marker");
            var next = file[position++];

            if (next == 0x00)
            {
                data.Add(0xFF);
            }
            else
            {
                Require(next == 0xD9, $"an unstuffed 0xFF, followed by 0x{next:X2}, in the picture data");
                Require(position == file.Length, "bytes after the end of the image");
                return [.. data];
            }
        }
    }

    private void Decode(byte[] data, (int Component, int Tables)[] scan)
    {
        Require(scan.Length == Components.Count, "the scan does not cover every component");
        var maxHorizontal = Components.Max(c => c.Horizontal);
        var maxVertical = Components.Max(c => c.Vertical);
        var unitsAcross = (Width + (8 * maxHorizontal) - 1) / (8 * maxHorizontal);
        var unitsDown = (Height + (8 * maxVertical) - 1) / (8 * maxVertical);
        var planes = Components.Select(c => new float[unitsAcross * c.Horizontal * 8 * unitsDown * c.Vertical * 8]).ToArray();
        var previous = new int[Components.Count];
        var bits = new BitSource(data);
        var coefficients = new float[64];

        for (var unitRow = 0; unitRow < unitsDown; unitRow++)
        {
            for (var unitColumn = 0; unitColumn < unitsAcross; unitColumn++)
            {
                for (var index = 0; index < Components.Count; index++)
                {
                    var component = Components[index];
                    Require(scan[index].Component == component.Id, "the scan lists components out of order");
                    var dc = Codes(Huffman[scan[index].Tables >> 4]);
                    var ac = Codes(Huffman[0x10 | (scan[index].Tables & 15)]);
                    var steps = Quantisation[component.Table];
                    var stride = unitsAcross * component.Horizontal * 8;

                    for (var blockRow = 0; blockRow < component.Vertical; blockRow++)
                    {
                        for (var blockColumn = 0; blockColumn < component.Horizontal; blockColumn++)
                        {
                            Array.Clear(coefficients);
                            var size = Symbol(bits, dc);
                            previous[index] += Extend(bits.Take(size), size);
                            coefficients[0] = previous[index] * steps[0];

                            for (var i = 1; i < 64; i++)
                            {
                                var symbol = Symbol(bits, ac);
                                int run = symbol >> 4, magnitude = symbol & 15;

                                if (magnitude == 0)
                                {
                                    if (run != 15)
                                    {
                                        Require(run == 0, "an end-of-block symbol with a run");
                                        break;
                                    }

                                    i += 15;
                                    continue;
                                }

                                i += run;
                                Require(i < 64, "a run past the end of a block");
                                coefficients[ZigZag[i]] = Extend(bits.Take(magnitude), magnitude) * steps[ZigZag[i]];
                            }

                            Inverse(
                                coefficients,
                                planes[index],
                                stride,
                                ((unitColumn * component.Horizontal) + blockColumn) * 8,
                                ((unitRow * component.Vertical) + blockRow) * 8);
                        }
                    }
                }
            }
        }

        bits.RequirePaddingOnly();
        Rgb = new byte[Width * Height * 3];

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                float Sample(int index)
                {
                    var component = Components[index];
                    var stride = unitsAcross * component.Horizontal * 8;
                    return planes[index][(y * component.Vertical / maxVertical * stride) + (x * component.Horizontal / maxHorizontal)];
                }

                float luminance = Sample(0) + 128, blueness = Sample(1), redness = Sample(2);
                var at = ((y * Width) + x) * 3;
                Rgb[at] = Clamp(luminance + (1.402f * redness));
                Rgb[at + 1] = Clamp(luminance - (0.344136f * blueness) - (0.714136f * redness));
                Rgb[at + 2] = Clamp(luminance + (1.772f * blueness));
            }
        }
    }

    /// <summary>The inverse transform as the standard writes it: every coefficient's cosine, summed.</summary>
    private static void Inverse(float[] coefficients, float[] plane, int stride, int left, int top)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                double sum = 0;

                for (var v = 0; v < 8; v++)
                {
                    for (var u = 0; u < 8; u++)
                    {
                        sum += (u == 0 ? Math.Sqrt(0.5) : 1) * (v == 0 ? Math.Sqrt(0.5) : 1)
                            * coefficients[(v * 8) + u]
                            * Math.Cos(((2 * x) + 1) * u * Math.PI / 16)
                            * Math.Cos(((2 * y) + 1) * v * Math.PI / 16);
                    }
                }

                plane[((top + y) * stride) + left + x] = (float)(sum / 4);
            }
        }
    }

    /// <summary>For each code length: the first code of that length, and where its symbols start.</summary>
    private static (int[] First, int[] Offset, byte[] Counts, byte[] Symbols) Codes((byte[] Counts, byte[] Symbols) table)
    {
        int[] first = new int[17], offset = new int[17];
        int code = 0, seen = 0;

        for (var length = 1; length <= 16; length++)
        {
            first[length] = code;
            offset[length] = seen;
            code += table.Counts[length - 1];
            seen += table.Counts[length - 1];
            Require(code <= 1 << length, "a Huffman table declares more codes than a length can hold");
            code <<= 1;
        }

        return (first, offset, table.Counts, table.Symbols);
    }

    private static int Symbol(BitSource bits, (int[] First, int[] Offset, byte[] Counts, byte[] Symbols) codes)
    {
        var code = 0;

        for (var length = 1; length <= 16; length++)
        {
            code = (code << 1) | bits.Take(1);
            var index = code - codes.First[length];

            if (index >= 0 && index < codes.Counts[length - 1])
            {
                return codes.Symbols[codes.Offset[length] + index];
            }
        }

        throw new InvalidDataException("a code no Huffman table defines");
    }

    private static int Extend(int value, int size) =>
        size == 0 || value >= 1 << (size - 1) ? value : value - (1 << size) + 1;

    private static byte Clamp(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);

    private static void Require(bool holds, string otherwise)
    {
        if (!holds)
        {
            throw new InvalidDataException(otherwise);
        }
    }

    private sealed class BitSource(byte[] data)
    {
        private int position;

        public int Take(int count)
        {
            var value = 0;

            for (var i = 0; i < count; i++, position++)
            {
                Require(position < data.Length * 8, "the picture data ran out");
                value = (value << 1) | ((data[position / 8] >> (7 - (position % 8))) & 1);
            }

            return value;
        }

        /// <summary>After the last block there may only be the ones that fill out the final byte.</summary>
        public void RequirePaddingOnly()
        {
            var left = (data.Length * 8) - position;
            Require(left < 8, "bytes of picture data after the last block");
            Require(Take(left) == (1 << left) - 1, "padding that is not all ones");
        }
    }
}
