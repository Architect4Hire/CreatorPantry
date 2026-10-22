using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The smallest files that are structurally each accepted format, and the near-misses the upload refuses.
/// Built in code so a test states exactly which bytes make a file what it is.
/// </summary>
internal static class BrandSourceSampleFiles
{
    public const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>The marker <see cref="FakeMalwareScanGateway"/> answers Infected for.</summary>
    public const string MalwareMarker = "CREATORPANTRY-TEST-MALWARE-SIGNATURE";

    public static byte[] Pdf(string body = "house style", int padding = 0) =>
        Encoding.ASCII.GetBytes($"%PDF-1.7\n1 0 obj\n<< /Note ({body}) >>\nendobj\n{new string(' ', padding)}\ntrailer\n<< >>\n%%EOF\n");

    public static byte[] PdfWithoutTrailer() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< >>\nendobj\n");

    /// <summary>
    /// A whole PNG: IHDR, image data and IEND, every chunk with its CRC — what the upload walk requires (15.2).
    /// </summary>
    /// <remarks>
    /// Small sizes carry real black scanlines, so the same file decodes. A size too big to hold in a test carries
    /// one byte of compressed data instead: still a file that holds together, refused on the size it declares.
    /// </remarks>
    public static byte[] Png(uint width = 4, uint height = 3) =>
        (ulong)width * height <= 2_000_000
            ? new Media.PngSampleFile
            {
                Width = width, Height = height, ColourType = 2, Samples = new byte[checked((int)(width * height * 3))],
            }.Build()
            : new Media.PngSampleFile
            {
                Width = width, Height = height, ColourType = 2, CompressedData = Media.PngSampleFile.Compress([0]),
            }.Build();

    /// <summary>
    /// A structurally valid GIF: the header, the logical screen descriptor, and one image block per frame.
    /// </summary>
    /// <remarks>
    /// GIF became an accepted upload format when IMG-004 shipped, because a creator's reference photograph is
    /// as likely to be one as a PNG. The inspector reads the logical screen size from the descriptor, which
    /// is the canvas every frame shares — so a test can state a size here and expect it back.
    /// </remarks>
    public static byte[] Gif(ushort width = 4, ushort height = 3, int frames = 1, bool legacyHeader = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange(legacyHeader ? "GIF87a"u8 : "GIF89a"u8);

        var descriptor = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0), width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2), height);

        // Packed field with the global-colour-table flag set, then background index and aspect ratio.
        descriptor[4] = 0x80;
        bytes.AddRange(descriptor);

        // The table itself: two RGB triples, which is the smallest a flag of 0x80 implies.
        bytes.AddRange([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);

        for (var frame = 0; frame < frames; frame++)
        {
            // Image descriptor at 0,0 sized 1x1 with no local colour table.
            bytes.AddRange([0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

            // LZW minimum code size, one sub-block of data, terminator.
            bytes.AddRange([0x02, 0x02, 0x4C, 0x01, 0x00]);
        }

        bytes.Add(0x3B);

        return [.. bytes];
    }

    /// <summary>
    /// A whole JPEG: SOI, an APP0 the walk steps over, the frame header, one scan with entropy-coded data that
    /// carries a stuffed byte and a restart marker, and EOI.
    /// </summary>
    public static byte[] Jpeg(ushort width = 4, ushort height = 3)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        // APP0, sixteen bytes long, before the frame header: the walk has to step over it.
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[9]);

        bytes.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00]);

        // Start of scan: one component, then entropy-coded data in which 0xFF 0x00 is a stuffed byte and
        // 0xFF 0xD0 a restart, neither of which ends the scan.
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        bytes.AddRange([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56]);

        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>
    /// A whole extended WebP: the <c>VP8X</c> canvas, then a <c>VP8L</c> bitstream chunk padded to an even
    /// length, inside a RIFF whose size is exactly the rest of the file.
    /// </summary>
    public static byte[] Webp(int width = 4, int height = 3)
    {
        var bytes = new byte[12 + 18 + 14];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        bytes[24] = (byte)(width - 1);
        bytes[25] = (byte)((width - 1) >> 8);
        bytes[26] = (byte)((width - 1) >> 16);
        bytes[27] = (byte)(height - 1);
        bytes[28] = (byte)((height - 1) >> 8);
        bytes[29] = (byte)((height - 1) >> 16);

        // The bitstream: its signature byte and its own 14-bit size fields, then a pad byte.
        "VP8L"u8.CopyTo(bytes.AsSpan(30));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34), 5);
        bytes[38] = 0x2F;
        var edge = (uint)(Math.Min(width, 16384) - 1) | ((uint)(Math.Min(height, 16384) - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(39), edge);
        return bytes;
    }

    public static byte[] Docx(bool withMacros = false) => DocxWith("<w:document/>", withMacros);

    /// <summary>
    /// A Word package whose main document part is <paramref name="documentXml"/> verbatim.
    /// </summary>
    /// <remarks>
    /// Written as raw WordprocessingML rather than through a document library, so an extraction test states
    /// exactly which markup it claims produces which text — a heading style, a numbered item, a field code.
    /// <see cref="WordNamespaceDeclaration"/> is what makes those elements the parser's namespace rather than an
    /// undeclared prefix.
    /// </remarks>
    public static byte[] DocxWith(string documentXml, bool withMacros = false) => Zip(
        ("[Content_Types].xml",
            $"<Types><Override PartName=\"/word/document.xml\" ContentType=\"{DocxMediaType}.main+xml\"/></Types>"),
        ("word/document.xml", documentXml),
        withMacros ? ("word/vbaProject.bin", "macro") : default);

    /// <summary>The <c>w:</c> prefix declaration a test's document markup needs to be read as WordprocessingML.</summary>
    public const string WordNamespaceDeclaration =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    /// <summary>A Word package whose main part is a document holding <paramref name="bodyXml"/>.</summary>
    public static byte[] DocxBody(string bodyXml) =>
        DocxWith($"<w:document {WordNamespaceDeclaration}><w:body>{bodyXml}</w:body></w:document>");

    /// <summary>One Word paragraph of plain runs, optionally styled.</summary>
    public static string DocxParagraph(string text, string? style = null, bool numbered = false)
    {
        var properties = style is null && !numbered
            ? string.Empty
            : "<w:pPr>"
                + (style is null ? string.Empty : $"<w:pStyle w:val=\"{style}\"/>")
                + (numbered ? "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>" : string.Empty)
                + "</w:pPr>";

        return $"<w:p>{properties}<w:r><w:t>{text}</w:t></w:r></w:p>";
    }

    /// <summary>A real ZIP that is not a Word package.</summary>
    public static byte[] PlainZip() => Zip(("notes.txt", "not a document"));

    /// <summary>A Windows executable's opening bytes.</summary>
    public static byte[] Executable() => [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00];

    public static byte[] Text(string text = "# House style\n\nWarm, plain, never breathless.\n") => Encoding.UTF8.GetBytes(text);

    public static byte[] Infected() => Pdf(MalwareMarker);

    private static byte[] Zip(params (string? Name, string? Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries.Where(entry => entry.Name is not null))
            {
                using var writer = new StreamWriter(archive.CreateEntry(name!).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
