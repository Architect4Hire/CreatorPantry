using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using CreatorPantry.Domain.Managers.FileInspection;

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
/// "Holds together" is structural, not a render: a PDF has its header and ends at its trailer with nothing but
/// white-space after it, a DOCX opens as a package with a main document part and no macro project inside its
/// expansion caps, and an image walks end to end through <see cref="ImageFileInspector"/> (15.2). Neither a PDF
/// nor an image may also be a ZIP. No pixel is decoded and no document is parsed; extraction (11A.10) does
/// that, and still treats the content as untrusted.
/// </para>
/// </remarks>
internal static class BrandSourceFileInspector
{
    public const string PdfMediaType = "application/pdf";

    public const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public const string PngMediaType = ImageFileInspector.PngMediaType;

    public const string JpegMediaType = ImageFileInspector.JpegMediaType;

    public const string WebpMediaType = ImageFileInspector.WebpMediaType;

    /// <summary>
    /// Accepted for IMG-004, which analyzes reference images and takes GIF alongside the other three.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An animated GIF is accepted rather than refused: a creator who saved one from somewhere should not have
    /// to convert it first. Which frame an analysis describes is IMG-004's concern and not this inspector's —
    /// what it validates is the file, and the size it reads is the logical screen, the canvas every frame
    /// shares.
    /// </para>
    /// <para>
    /// <strong>Adding a format here is only half the change.</strong> Every accepted type needs a text
    /// extractor registered in <c>BrandSourceTextExtractors</c>, or every version of it is queued, run and
    /// failed with "no reader is registered" — a server fault reported as if the creator's file were at fault.
    /// GIF maps to the same image reader as the other three, which reads nothing and says so.
    /// <c>BrandSourceTextExtractorTests</c> is what refuses to let the two lists drift.
    /// </para>
    /// </remarks>
    public const string GifMediaType = ImageFileInspector.GifMediaType;

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
        [GifMediaType] = [".gif"],
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
        [GifMediaType] = "gif",
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
            (PngMediaType or JpegMediaType or WebpMediaType or GifMediaType) and var image =>
                (image, await ImageAsync(content, length, cancellationToken)),
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

        return ImageFileInspector.MediaTypeOf(head);
    }

    private static async Task<BrandSourceInspectionOutcome> PdfAsync(
        Stream content, ReadOnlyMemory<byte> head, long length, CancellationToken cancellationToken)
    {
        // "%PDF-1.7" or "%PDF-2.0": a major version this decade has seen, a dot and a digit.
        if (head.Length < 8 || head.Span[5] is not ((byte)'1' or (byte)'2') || head.Span[6] != (byte)'.' || !char.IsAsciiDigit((char)head.Span[7]))
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }

        // The tail a ZIP reader would search, which also holds the trailer: a PDF ends at its last %%EOF and
        // whitespace, and a ZIP directory reachable from the end makes it an archive as well (15.2).
        var tail = new byte[(int)Math.Min(ArchiveTrailer.ZipSearchWindow, length)];
        var read = await ReadAtAsync(content, length - tail.Length, tail, cancellationToken);
        var span = tail.AsSpan(0, read);
        var trailerStart = Math.Max(0, span.Length - PdfTrailerWindow);
        var trailer = span[trailerStart..].LastIndexOf("%%EOF"u8);

        if (trailer < 0)
        {
            return BrandSourceInspectionOutcome.Corrupt;
        }

        var after = span[(trailerStart + trailer + "%%EOF"u8.Length)..];

        return after.ContainsAnyExcept(PdfWhitespace) || ArchiveTrailer.EndsInZipDirectory(span)
            ? BrandSourceInspectionOutcome.Corrupt
            : BrandSourceInspectionOutcome.Accepted;
    }

    /// <summary>How far from the end the final <c>%%EOF</c> is looked for.</summary>
    private const int PdfTrailerWindow = 2048;

    /// <summary>The six bytes PDF defines as white-space, the only thing that may follow the final <c>%%EOF</c>.</summary>
    private static readonly System.Buffers.SearchValues<byte> PdfWhitespace =
        System.Buffers.SearchValues.Create([0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20]);

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

    /// <summary>
    /// The whole file through <see cref="ImageFileInspector"/>, the one reading of a picture every module shares,
    /// held to <see cref="BrandPolicy.SourceImageMaxPixels"/>.
    /// </summary>
    /// <remarks>
    /// Read into memory whole, which the size check before it bounds at <see cref="BrandPolicy.SourceUploadMaxBytes"/>:
    /// the walk runs to the format's own end, and anything after it is refused as damage rather than kept.
    /// </remarks>
    private static async Task<BrandSourceInspectionOutcome> ImageAsync(
        Stream content, long length, CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        var read = await ReadAtAsync(content, 0, bytes, cancellationToken);

        return ImageFileInspector.Inspect(bytes.AsSpan(0, read), BrandPolicy.SourceImageMaxPixels).Outcome switch
        {
            ImageFileInspectionOutcome.Accepted => BrandSourceInspectionOutcome.Accepted,
            ImageFileInspectionOutcome.TooLarge => BrandSourceInspectionOutcome.ImageTooLarge,
            _ => BrandSourceInspectionOutcome.Corrupt,
        };
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
