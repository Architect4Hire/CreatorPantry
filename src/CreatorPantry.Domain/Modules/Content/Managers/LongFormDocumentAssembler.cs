using System.Text;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Makes a creator's section heading inert as one line of Markdown: it reads as itself and cannot become a
/// link, raw HTML, emphasis or a second structure.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The recipe export's convention, restated here on purpose.</strong> The rule and the character set
/// are <c>RecipeMarkdownEscaper</c>'s, which lives in the recipes module and may not be called from this one:
/// only a facade crosses a module boundary, and an escaper is not one. <c>LongFormDocumentAssemblerTests</c>
/// runs the two over the same lines and fails if they ever part company.
/// </para>
/// <para>
/// Only headings pass through this. A section's body is the creator's Markdown and is written as it stands.
/// </para>
/// </remarks>
internal static class LongFormMarkdownEscaper
{
    private const string InlineSpecials = "\\`*_[]<>|~&";

    /// <summary>One line of text; any line breaks collapse to a single space.</summary>
    public static string Line(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            cleaned.Append(character switch
            {
                '\t' => ' ',
                '\r' => '\n',
                _ when character != '\n' && char.IsControl(character) => '\0',
                _ => character,
            });
        }

        var lines = cleaned.ToString()
            .Replace("\0", string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

        return Escape(string.Join(' ', lines));
    }

    private static string Escape(string line)
    {
        var escaped = new StringBuilder(line.Length + 4);

        foreach (var character in line)
        {
            if (InlineSpecials.Contains(character))
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        var text = escaped.ToString();

        // A line that opens like a heading, bullet, rule or numbered item would be structure, not text.
        if (text.Length > 0 && "#+-=".Contains(text[0]))
        {
            return "\\" + text;
        }

        var digits = 0;

        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        return digits is > 0 and <= 9 && digits < text.Length && text[digits] is '.' or ')'
            ? text.Insert(digits, "\\")
            : text;
    }
}

/// <summary>One section as the assembled document reports it.</summary>
/// <param name="Body">The section's text as stored, or null when it has not been written.</param>
/// <param name="CharacterCount">Counted the way the ceiling is. Zero when unwritten.</param>
public sealed record LongFormAssembledSection(
    Guid Id,
    LongFormSectionRole Role,
    string Heading,
    string? Body,
    bool IsWritten,
    bool Settled,
    int CharacterCount,
    bool OverCeiling,
    LongFormSectionProvenance? Provenance,
    IReadOnlyList<LongFormSectionFinding> Findings);

/// <summary>A revision rendered whole: the Markdown, and what is true of it.</summary>
/// <param name="Markdown">Every written section in plan order. Empty when none is written.</param>
/// <param name="DocumentOverCeiling">Whether the written text together is longer than a document may be.</param>
public sealed record LongFormAssembledDocument(
    string Markdown,
    int WordCount,
    int CharacterCount,
    LongFormWordRange TargetWords,
    LongFormWordTargetStatus WordTarget,
    bool DocumentOverCeiling,
    IReadOnlyList<LongFormAssembledSection> Sections);

/// <summary>
/// Renders one revision's sections, in plan order, into one Markdown document (LF.3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A projection.</strong> The same document in gives the same bytes out: it reads no clock, no
/// culture and no setting, carries no timestamp, and writes nothing. That is what lets a creator be shown
/// exactly what they would accept, and what lets a test hold the output to a file.
/// </para>
/// <para>
/// <strong>The shape.</strong> Each written section is a second-level heading — the creator's own, escaped to
/// one inert line — a blank line, and its body exactly as stored but for its line endings and any blank lines
/// at its end. Sections are a blank line apart and the document ends with one newline. There is no title: a
/// title belongs to wherever the piece is published, and the export decides it (LF.5.1).
/// </para>
/// <para>
/// <strong>An unwritten section is not in the Markdown.</strong> A heading with nothing under it reads as a
/// mistake in an article. It is still in <see cref="LongFormAssembledDocument.Sections"/>, marked unwritten,
/// so whoever shows the document can say it is incomplete.
/// </para>
/// <para>
/// <strong>Nothing is labelled.</strong> The document is the creator's article, so it says nothing of which
/// words were generated or reused; that is reported beside it, per section, for a screen to show.
/// </para>
/// </remarks>
public static class LongFormDocumentAssembler
{
    public static LongFormAssembledDocument Assemble(LongFormKind kind, LongFormDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var measured = LongFormDocumentValidator.Validate(kind, document);
        var measures = measured.Sections.ToDictionary(measure => measure.SectionId);
        var markdown = new StringBuilder();
        var sections = new List<LongFormAssembledSection>(document.Sections.Count);

        foreach (var section in document.Sections)
        {
            var written = !string.IsNullOrWhiteSpace(section.Body);
            var measure = measures.GetValueOrDefault(section.Id);

            sections.Add(new LongFormAssembledSection(
                section.Id,
                section.Role,
                section.Heading,
                written ? section.Body : null,
                written,
                section.Settled,
                measure?.CharacterCount ?? 0,
                measure?.OverCeiling ?? false,
                written ? section.Provenance : null,
                written ? section.Findings ?? [] : []));

            if (!written)
            {
                continue;
            }

            if (markdown.Length > 0)
            {
                markdown.Append('\n');
            }

            markdown.Append("## ").Append(LongFormMarkdownEscaper.Line(section.Heading)).Append("\n\n");
            markdown.Append(Body(section.Body!)).Append('\n');
        }

        return new LongFormAssembledDocument(
            markdown.ToString(),
            measured.WordCount,
            measured.CharacterCount,
            measured.TargetWords,
            measured.WordTarget,
            measured.Findings.Any(finding => finding.Code == LongFormFindingCodes.DocumentOverCeiling),
            sections);
    }

    /// <summary>
    /// A body as it is written into the document: one kind of line ending, and no blank lines trailing it.
    /// </summary>
    /// <remarks>
    /// Nothing inside the body is touched — not its indentation, not the spaces that end a line and make a
    /// Markdown break. Leading blank lines go too, so a heading is always exactly one blank line above its text.
    /// </remarks>
    private static string Body(string body) =>
        body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n').TrimEnd();
}
