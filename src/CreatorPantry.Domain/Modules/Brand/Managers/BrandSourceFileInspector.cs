using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

public enum BrandSourceInspectionOutcome
{
    Accepted = 1,

    Empty = 2,

    /// <summary>More bytes than the format allows.</summary>
    TooLarge = 3,

    /// <summary>An image that declares more pixels than are accepted.</summary>
    ImageTooLarge = 4,

    /// <summary>Not one of the accepted formats, whatever the name says.</summary>
    Unsupported = 5,

    /// <summary>An accepted format, but not the one the filename claims.</summary>
    NameMismatch = 6,

    /// <summary>Starts like an accepted format and does not hold together as one.</summary>
    Corrupt = 7,
}

/// <param name="MediaType">Established from the bytes. Present exactly when the outcome is accepted.</param>
/// <param name="ContentChecksum"><c>sha256:</c> and the digest of the whole file, in the form the object store reports.</param>
public sealed record BrandSourceInspection(
    BrandSourceInspectionOutcome Outcome, string? MediaType = null, long SizeBytes = 0, string? ContentChecksum = null);

/// <summary>
/// Decides what an uploaded brand source file is by reading it: PDF, DOCX, PNG, JPEG, WebP, or UTF-8 text
/// offered as Markdown, plain text or HTML. Deterministic, and the only thing that names a version's media type.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The declared content type is never an input</strong>, and the filename is trusted for one thing
/// only: text has no signature, so the extension chooses among the three text labels once the bytes have
/// proved to be text. A binary file must also carry its own format's extension, so a PDF named
/// <c>notes.txt</c> is refused rather than quietly relabelled.
/// </para>
/// <para>
/// "Holds together" is structural, not a render: a PDF has its header and trailer, a DOCX opens as a package
/// with a main document part and no macro project inside its expansion caps, and an image's header yields
/// sane dimensions. No pixel is decoded and no document is parsed; extraction (11A.10) does that, and still
/// treats the content as untrusted.
/// </para>
/// </remarks>
internal static class BrandSourceFileInspector
{
    public const string PdfMediaType = "application/pdf";

    public const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public const string PngMediaType = "image/png";

    public const string JpegMediaType = "image/jpeg";

    public const string WebpMediaType = "image/webp";

    private const string DocxMainPartContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    private const int ContentTypesMaxBytes = 1024 * 1024;

    private static readonly Dictionary<string, string> TextMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".txt"] = "text/plain",
        [".html"] = "text/html",
        [".htm"] = "text/html",
    };

    private static readonly Dictionary<string, string[]> BinaryExtensions = new(StringComparer.Ordinal)
    {
        [PdfMediaType] = [".pdf"],
        [DocxMediaType] = [".docx"],
        [PngMediaType] = [".png"],
        [JpegMediaType] = [".jpg", ".jpeg"],
        [WebpMediaType] = [".webp"],
    };

    /// <summary>
    /// The one extension a download should use for each accepted media type, without its dot.
    /// </summary>
    /// <remarks>
    /// Here rather than next to the download, because this class is what decides a file's media type from its
    /// bytes: a format this knows and that had no extension to offer would be a drift between two lists, and
    /// <c>BrandSourceFileInspectorTests</c> asserts the two stay in step. One per type where the accepted
    /// extensions are several — a JPEG comes back as <c>.jpg</c> whichever spelling it arrived under, because a
    /// download name is ours to choose and determinism is the point.
    /// </remarks>
    private static readonly Dictionary<string, string> CanonicalExtensions = new(StringComparer.Ordinal)
    {
        [PdfMediaType] = "pdf",
        [DocxMediaType] = "docx",
        [PngMediaType] = "png",
        [JpegMediaType] = "jpg",
        [WebpMediaType] = "webp",
        ["text/markdown"] = "md",
        ["text/plain"] = "txt",
        ["text/html"] = "html",
    };

    /// <summary>Every media type an upload can be stored as. The set a download has to be able to name.</summary>
    public static IEnumerable<string> AcceptedMediaTypes =>
        BinaryExtensions.Keys.Concat(TextMediaTypes.Values).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The extension to give a download of this media type, without its dot, or null for a type this does not
    /// know.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess: a name with no extension is awkward, and a wrong one is a lie about the
    /// bytes. Unreachable for anything this class stored, because <see cref="AcceptedMediaTypes"/> is covered.
    /// </remarks>
    public static string? CanonicalExtension(string mediaType) =>
        CanonicalExtensions.GetValueOrDefault(mediaType);

    /// <summary>Inspects a seekable stream from its start and leaves it rewound.</summary>
    public static async Task<BrandSourceInspection> InspectAsync(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanSeek)
        {
            throw new ArgumentException("An upload is inspected, scanned and stored from one buffered stream.", nameof(content));
        }

        var length = content.Length;

        if (length == 0)
        {
            return new BrandSourceInspection(BrandSourceInspectionOutcome.Empty);
        }

        if (length > BrandPolicy.SourceUploadMaxBytes)
        {
            return new BrandSourceInspection(BrandSourceInspectionOutcome.TooLarge);
        }

        var extension = Path.GetExtension(fileName);
        var head = new byte[64];
        var headLength = await ReadAtAsync(content, 0, head, cancellationToken);
        var signature = head.AsMemory(0, headLength);

        var (mediaType, outcome) = Signature(signature.Span) switch
        {
            PdfMediaType => (PdfMediaType, await PdfAsync(content, signature, length, cancellationToken)),
            DocxMediaType => (DocxMediaType, Docx(content)),
            PngMediaType => (PngMediaType, Png(signature.Span)),
            JpegMediaType => (JpegMediaType, await JpegAsync(content, length, cancellationToken)),
            WebpMediaType => (WebpMediaType, Webp(signature.Span, length)),
            _ => await TextAsync(content, extension, length, cancellationToken),
        };

        if (outcome == BrandSourceInspectionOutcome.Accepted
            && BinaryExtensions.TryGetValue(mediaType!, out var extensions)
            && !extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            outcome = BrandSourceInspectionOutcome.NameMismatch;
        }

        if (outcome != BrandSourceInspectionOutcome.Accepted)
        {
            content.Position = 0;

            return new BrandSourceInspection(outcome);
        }

        content.Position = 0;
        var digest = await SHA256.HashDataAsync(content, cancellationToken);
        content.Position = 0;

        return new BrandSourceInspection(
            BrandSourceInspectionOutcome.Accepted, mediaType, length, "sha256:" + Convert.ToHexStringLower(digest));
    }

    private static string? Signature(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("%PDF-"u8))
        {
            return PdfMediaType;
        }

        if (head.StartsWith("PK\u0003\u0004"u8))
        {
            return DocxMediaType;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return PngMediaType;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return JpegMediaType;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return WebpMediaType;
        }

        return null;
    }

    private static async Task<BrandSourceInspectionOutcome> PdfAsync(
        Stream content, ReadOnlyMemory<byte> head, long length, CancellationToken cancellationToken)
    {
        // "%PDF-1.7" or "%PDF-2.0": a major version this decade has seen, a dot and a digit.
        if (head.Length < 8 || head.Span[5] is not ((byte)'1' or (byte)'2') || head.Span[6] != (byte)'.' || !char.IsAsciiDigit((char)head.Span[7]))
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }

        var tail = new byte[(int)Math.Min(2048, length)];
        var read = await ReadAtAsync(content, length - tail.Length, tail, cancellationToken);

        return tail.AsSpan(0, read).IndexOf("%%EOF"u8) >= 0
            ? BrandSourceInspectionOutcome.Accepted
            : BrandSourceInspectionOutcome.Corrupt;
    }

    private static BrandSourceInspectionOutcome Docx(Stream content)
    {
        content.Position = 0;

        try
        {
            using var package = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

            // The declared sizes are the archive's own claim. Nothing here expands an entry past
            // ContentTypesMaxBytes, so a lie costs nothing; the cap refuses the honest bomb.
            if (package.Entries.Count > BrandPolicy.SourceDocxMaxEntries
                || package.Entries.Sum(entry => entry.Length) > BrandPolicy.SourceDocxMaxUncompressedBytes)
            {
                return BrandSourceInspectionOutcome.Unsupported;
            }

            // A macro project makes it a .docm whatever it is called.
            if (package.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            {
                return BrandSourceInspectionOutcome.Unsupported;
            }

            var contentTypes = package.GetEntry("[Content_Types].xml");

            // A ZIP, but not a Word package: a spreadsheet, a JAR, an archive of anything.
            if (contentTypes is null || package.GetEntry("word/document.xml") is null)
            {
                return BrandSourceInspectionOutcome.Unsupported;
            }

            using var types = contentTypes.Open();
            var buffer = new byte[ContentTypesMaxBytes];
            var read = types.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            return buffer.AsSpan(0, read).IndexOf(Encoding.ASCII.GetBytes(DocxMainPartContentType)) >= 0
                ? BrandSourceInspectionOutcome.Accepted
                : BrandSourceInspectionOutcome.Unsupported;
        }
        catch (InvalidDataException)
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }
    }

    private static BrandSourceInspectionOutcome Png(ReadOnlySpan<byte> head)
    {
        // The first chunk is always IHDR, thirteen bytes long, and opens with the width and height.
        if (head.Length < 24
            || BinaryPrimitives.ReadUInt32BigEndian(head[8..]) != 13
            || !head[12..16].SequenceEqual("IHDR"u8))
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }

        return Dimensions(BinaryPrimitives.ReadUInt32BigEndian(head[16..]), BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
    }

    private static async Task<BrandSourceInspectionOutcome> JpegAsync(Stream content, long length, CancellationToken cancellationToken)
    {
        var segment = new byte[9];
        long position = 2;

        // Walks the marker segments to the frame header, which is where a JPEG states its size.
        while (position + 4 <= length)
        {
            var read = await ReadAtAsync(content, position, segment, cancellationToken);

            if (read < 4 || segment[0] != 0xFF)
            {
                return BrandSourceInspectionOutcome.Corrupt;
            }

            var marker = segment[1];

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

            // The image data or the end arrived before any frame header.
            if (marker is 0xD9 or 0xDA)
            {
                return BrandSourceInspectionOutcome.Corrupt;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(2));

            if (segmentLength < 2)
            {
                return BrandSourceInspectionOutcome.Corrupt;
            }

            // SOF0 to SOF15, less the three in that range that are tables rather than frames.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return read < 9
                    ? BrandSourceInspectionOutcome.Corrupt
                    : Dimensions(
                        BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(7)),
                        BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(5)));
            }

            position += 2 + segmentLength;
        }

        return BrandSourceInspectionOutcome.Corrupt;
    }

    private static BrandSourceInspectionOutcome Webp(ReadOnlySpan<byte> head, long length)
    {
        if (head.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) > length - 8)
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }

        var chunk = head[12..16];

        if (chunk.SequenceEqual("VP8 "u8))
        {
            return head[23..26].SequenceEqual((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A])
                ? Dimensions(
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF))
                : BrandSourceInspectionOutcome.Corrupt;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);

            return head[20] == 0x2F
                ? Dimensions((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1)
                : BrandSourceInspectionOutcome.Corrupt;
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return Dimensions(
                (uint)(head[24] | (head[25] << 8) | (head[26] << 16)) + 1,
                (uint)(head[27] | (head[28] << 8) | (head[29] << 16)) + 1);
        }

        return BrandSourceInspectionOutcome.Corrupt;
    }

    private static BrandSourceInspectionOutcome Dimensions(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }

        return (long)width * height > BrandPolicy.SourceImageMaxPixels
            ? BrandSourceInspectionOutcome.ImageTooLarge
            : BrandSourceInspectionOutcome.Accepted;
    }

    private static async Task<(string? MediaType, BrandSourceInspectionOutcome Outcome)> TextAsync(
        Stream content, string extension, long length, CancellationToken cancellationToken)
    {
        // No signature matched. Only a text extension can still be right, and only if the bytes are text.
        if (!TextMediaTypes.TryGetValue(extension, out var mediaType))
        {
            return (null, BrandSourceInspectionOutcome.Unsupported);
        }

        if (length > BrandPolicy.SourceTextUploadMaxBytes)
        {
            return (null, BrandSourceInspectionOutcome.TooLarge);
        }

        var bytes = new byte[length];
        var read = await ReadAtAsync(content, 0, bytes, cancellationToken);

        return IsText(bytes.AsSpan(0, read))
            ? (mediaType, BrandSourceInspectionOutcome.Accepted)
            : (null, BrandSourceInspectionOutcome.Unsupported);
    }

    /// <summary>Strict UTF-8 with no control bytes beyond tab, line feed, form feed and carriage return.</summary>
    private static bool IsText(ReadOnlySpan<byte> bytes)
    {
        // Every byte of a multi-byte UTF-8 sequence is 0x80 or above, so a byte scan cannot misread one.
        foreach (var value in bytes)
        {
            if (value == 0x7F || (value < 0x20 && value is not (0x09 or 0x0A or 0x0C or 0x0D)))
            {
                return false;
            }
        }

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(bytes);

            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static async Task<int> ReadAtAsync(Stream content, long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        content.Position = offset;

        return await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
    }
}
