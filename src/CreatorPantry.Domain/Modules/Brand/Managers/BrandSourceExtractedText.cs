namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>How one parser's attempt on one file ended.</summary>
public enum BrandSourceTextExtractionOutcome
{
    /// <summary>Text was recovered. The only outcome that carries any.</summary>
    Extracted = 1,

    /// <summary>
    /// The format holds no text to read — an image, or a PDF with no text layer. A review state, not an error,
    /// and never an OCR claim: nothing here looks at pixels.
    /// </summary>
    Unsupported = 2,

    /// <summary>
    /// The format is one this knows and the file does not hold together as one — a broken package, a PDF this
    /// cannot open, an encrypted document. Permanent: retrying the same bytes produces the same answer.
    /// </summary>
    Unreadable = 3,
}

/// <summary>
/// What a parser made of one document version's bytes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every field is untrusted data</strong> however plausible it looks. It came out of a file somebody
/// uploaded, and ai.md's rule holds for all of it: when this text later reaches a prompt it is delimited source
/// material, never instruction, and nothing in it may redefine what a tool may do.
/// </para>
/// <para>
/// <see cref="Text"/> is the normalized artifact exactly as it will be stored — UTF-8, ATX headings,
/// blank-line-separated blocks — so the thing checksummed and written is the thing the parser produced, with
/// no later reshaping to drift from it.
/// </para>
/// </remarks>
/// <param name="Text">Present exactly when <paramref name="Outcome"/> is <see cref="BrandSourceTextExtractionOutcome.Extracted"/>.</param>
/// <param name="Reason">
/// A short creator-facing note: why there is no text, or that there was more of it than is stored. Null when
/// there is nothing to say. Recorded on the extraction row, so it must describe rather than quote.
/// </param>
/// <param name="Truncated">
/// Whether the document held more text than <see cref="BrandPolicy.ExtractedTextMaxBytes"/> allows. The stored
/// artifact is then a prefix, which <paramref name="Reason"/> states.
/// </param>
public sealed record BrandSourceExtractedText(
    BrandSourceTextExtractionOutcome Outcome,
    string? Text = null,
    string? Reason = null,
    bool Truncated = false)
{
    public static BrandSourceExtractedText Unsupported(string reason) =>
        new(BrandSourceTextExtractionOutcome.Unsupported, Reason: reason);

    public static BrandSourceExtractedText Unreadable(string reason) =>
        new(BrandSourceTextExtractionOutcome.Unreadable, Reason: reason);
}

/// <summary>
/// Turns one accepted media type's bytes into the normalized text artifact.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic, synchronous in spirit, and <strong>inert</strong>: an implementation reads a stream and
/// returns text. It opens no network connection, resolves no URL or external entity, executes no macro or
/// script, writes no file, and calls no model. Those are not conventions to keep — they are what makes it safe
/// to run this over a file a stranger uploaded.
/// </para>
/// <para>
/// Internal, like <see cref="BrandSourceFileInspector"/>: the parsers are the module's own mechanism and
/// nothing outside it chooses one. The stream is seekable and positioned anywhere; an implementation seeks
/// what it needs.
/// </para>
/// </remarks>
internal interface IBrandSourceTextExtractor
{
    /// <summary>
    /// This parser's stable identity, recorded as the operation's provenance: <c>pdf/pdfpig@0.1.16</c>.
    /// </summary>
    /// <remarks>
    /// Versioned where a library does the reading, because a library upgrade can change what a document's text
    /// comes out as, and an artifact nobody can attribute to a parser is an artifact nobody can re-derive.
    /// </remarks>
    string ExtractorId { get; }

    Task<BrandSourceExtractedText> ExtractAsync(Stream content, CancellationToken cancellationToken);
}
