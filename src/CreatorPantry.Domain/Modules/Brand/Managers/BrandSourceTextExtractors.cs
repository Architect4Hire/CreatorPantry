namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Which parser reads which media type, and the only thing that decides.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed on the media type a version's bytes were established to be</strong> — never on a filename, an
/// extension or anything a client declared. <see cref="BrandSourceFileInspector"/> settled that at upload by
/// reading the file, and the row recorded it; this looks it up and nothing more.
/// </para>
/// <para>
/// <strong>Every accepted type has an entry, images included.</strong> An image's "parser" reports
/// <see cref="BrandSourceTextExtractionOutcome.Unsupported"/> with a reason, which is the whole reason it is
/// here rather than absent: a version with no entry would have to be left unextracted, and "not extracted" reads
/// as <em>pending</em> in the library rather than <em>reviewed and there is nothing to read</em>. A creator who
/// uploaded a photograph deserves the second answer. <c>BrandSourceTextExtractorsTests</c> asserts this map
/// covers <see cref="BrandSourceFileInspector.AcceptedMediaTypes"/> exactly, so a format added to the upload
/// cannot quietly arrive here with no reader.
/// </para>
/// </remarks>
internal static class BrandSourceTextExtractors
{
    private static readonly BrandSourcePlainTextExtractor Text = new();

    private static readonly BrandSourceHtmlTextExtractor Html = new();

    private static readonly BrandSourceDocxTextExtractor Docx = new();

    private static readonly BrandSourcePdfTextExtractor Pdf = new();

    private static readonly BrandSourceImageTextExtractor Image = new();

    private static readonly Dictionary<string, IBrandSourceTextExtractor> ByMediaType = new(StringComparer.Ordinal)
    {
        [BrandSourceFileInspector.PdfMediaType] = Pdf,
        [BrandSourceFileInspector.DocxMediaType] = Docx,
        ["text/markdown"] = Text,
        ["text/plain"] = Text,
        ["text/html"] = Html,
        [BrandSourceFileInspector.PngMediaType] = Image,
        [BrandSourceFileInspector.JpegMediaType] = Image,
        [BrandSourceFileInspector.WebpMediaType] = Image,
    };

    /// <summary>
    /// The parser for a stored media type, or null for one nothing reads.
    /// </summary>
    /// <remarks>
    /// Null is unreachable for anything an upload could have stored, and is still answered rather than thrown:
    /// a version written before a format was removed from the accepted set would otherwise crash the worker
    /// instead of recording a review state.
    /// </remarks>
    public static IBrandSourceTextExtractor? For(string mediaType) => ByMediaType.GetValueOrDefault(mediaType);

    /// <summary>Every media type this knows a reader for. The set the upload's accepted list must match.</summary>
    public static IEnumerable<string> CoveredMediaTypes => ByMediaType.Keys;
}

/// <summary>
/// The reader for images, which reads nothing.
/// </summary>
/// <remarks>
/// A real entry rather than an absence, so an uploaded photograph reaches a recorded review state instead of
/// sitting unextracted forever. It opens no pixel and makes no claim about what the picture shows: alt text and
/// image description are their own features with their own provenance (media.md), and inventing either from a
/// filename is exactly what that rule forbids.
/// </remarks>
internal sealed class BrandSourceImageTextExtractor : IBrandSourceTextExtractor
{
    public string ExtractorId => "image/none@1";

    public Task<BrandSourceExtractedText> ExtractAsync(Stream content, CancellationToken cancellationToken) =>
        Task.FromResult(BrandSourceExtractedText.Unsupported(BrandSourceExtractionReasons.Image));
}
