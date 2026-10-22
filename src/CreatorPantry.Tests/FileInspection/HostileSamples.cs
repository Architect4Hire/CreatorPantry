using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Media;

namespace CreatorPantry.Tests.FileInspection;

/// <summary>
/// Files built to be two things at once, or one thing and then more: the polyglots, trailers and metadata the
/// 15.2 upload pass refuses or reports. Built in code so each test states exactly which bytes make the attack.
/// </summary>
internal static class HostileSamples
{
    private static ReadOnlySpan<byte> ZipEndSignature => [0x50, 0x4B, 0x05, 0x06];

    /// <summary>A real ZIP holding one small entry, with an empty comment.</summary>
    public static byte[] Zip(string entry = "payload.txt", string content = "not a picture")
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// <see cref="Zip"/> with its offsets moved as if it began <paramref name="at"/> bytes into a file — what a
    /// polyglot's author does, so that every reader, not only a forgiving one, opens it.
    /// </summary>
    public static byte[] ZipAt(int at)
    {
        var zip = Zip();
        var end = zip.Length - 22;
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(end + 16), (uint)(directory + at));

        for (var entry = 0; entry < entries; entry++)
        {
            var local = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(directory + 42));
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(directory + 42), (uint)(local + at));
            directory += 46
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(directory + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(directory + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(directory + 32));
        }

        return zip;
    }

    /// <summary><paramref name="host"/> with a working ZIP appended after it.</summary>
    public static byte[] AppendZip(byte[] host) => Concat(host, ZipAt(host.Length));

    /// <summary>Whether an ordinary ZIP reader opens <paramref name="bytes"/> and finds an entry: proof a sample is a polyglot.</summary>
    public static bool OpensAsZip(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

            return archive.Entries.Count > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    /// <summary>
    /// A PNG whose private ancillary chunk carries a ZIP, with the ZIP's comment running over every byte after it —
    /// so the PNG walks cleanly to IEND <em>and</em> a ZIP reader opens the file.
    /// </summary>
    public static byte[] PngHidingZip()
    {
        // The signature, the IHDR chunk, and the carrying chunk's own length and type come first.
        var zip = ZipAt(8 + 25 + 8);

        byte[] Build(byte[] carried) => new PngSampleFile
        {
            Width = 2, Height = 2, ColourType = 2, Samples = new byte[12], BeforeData = [("ruSt", carried)],
        }.Build();

        var first = Build(zip);
        var recordEnd = first.AsSpan().LastIndexOf(ZipEndSignature) + 22;
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(zip.Length - 2), checked((ushort)(first.Length - recordEnd)));

        return Build(zip);
    }

    /// <summary>
    /// A PDF followed by a ZIP whose comment is the PDF's own closing <c>%%EOF</c>: it ends exactly as a PDF should
    /// and opens as an archive.
    /// </summary>
    public static byte[] PdfHidingZip()
    {
        var closing = Encoding.ASCII.GetBytes("\n%%EOF\n");
        var zip = ZipAt(BrandSourceSampleFiles.Pdf().Length);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(zip.Length - 2), (ushort)closing.Length);

        return Concat(BrandSourceSampleFiles.Pdf(), zip, closing);
    }

    /// <summary>The opening box of an MP4: what a phone's motion photo appends after the still.</summary>
    public static byte[] Mp4Box() =>
        [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8, 0x00, 0x00, 0x00, 0x00, .. "mp42isom"u8];

    /// <summary>A JPEG with one more segment straight after SOI.</summary>
    public static byte[] JpegWithSegment(byte marker, byte[] payload)
    {
        var jpeg = BrandSourceSampleFiles.Jpeg();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(segment, 4);

        return Concat(jpeg[..2], segment, jpeg[2..]);
    }

    /// <summary>A JPEG whose APP1 is EXIF, with or without a GPS position in it.</summary>
    public static byte[] JpegWithExif(bool withPosition) =>
        JpegWithSegment(0xE1, Concat("Exif\0\0"u8.ToArray(), Tiff(withPosition)));

    /// <summary>A JPEG whose APP1 is an XMP packet naming a latitude.</summary>
    public static byte[] JpegWithXmpPosition() =>
        JpegWithSegment(0xE1, Concat(
            "http://ns.adobe.com/xap/1.0/\0"u8.ToArray(),
            Encoding.ASCII.GetBytes("<x:xmpmeta><rdf:Description exif:GPSLatitude=\"51,30.0N\"/></x:xmpmeta>")));

    /// <summary>
    /// A little-endian TIFF structure: IFD0 pointing at a GPS directory that holds either a latitude or only a
    /// version.
    /// </summary>
    public static byte[] Tiff(bool withPosition)
    {
        var tiff = new byte[44];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);

        // IFD0: one entry, GPSInfo (0x8825), LONG, count 1, pointing at offset 26.
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x8825);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18), 26);

        // GPS directory: one entry, GPSLatitude (2) or GPSVersionID (0).
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(28), withPosition ? (ushort)2 : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(30), withPosition ? (ushort)5 : (ushort)1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(32), withPosition ? 3u : 4u);

        return tiff;
    }

    /// <summary>An extended WebP built from the chunks given, each sized and padded, inside an exact RIFF.</summary>
    public static byte[] Webp(byte vp8xFlags, params (string FourCc, byte[] Payload)[] chunks)
    {
        var vp8x = new byte[10];
        vp8x[0] = vp8xFlags;
        vp8x[4] = 3; // canvas 4x3, each stored less one
        vp8x[7] = 2;

        using var body = new MemoryStream();
        body.Write("WEBP"u8);

        foreach (var (fourCc, payload) in chunks.Prepend(("VP8X", vp8x)))
        {
            body.Write(Encoding.ASCII.GetBytes(fourCc));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
            body.Write(size);
            body.Write(payload);

            if (payload.Length % 2 == 1)
            {
                body.WriteByte(0);
            }
        }

        var riff = new byte[8];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Length);

        return Concat(riff, body.ToArray());
    }

    /// <summary>A VP8L bitstream chunk payload for a 4x3 picture.</summary>
    public static byte[] Vp8l() => [0x2F, 0x03, 0x80, 0x00, 0x00];
}
