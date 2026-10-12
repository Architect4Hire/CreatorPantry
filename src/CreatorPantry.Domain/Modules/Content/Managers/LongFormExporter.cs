using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Projects one accepted long-form revision onto Markdown or HTML (LF.5.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A projection, not a stored competing document.</strong> It reads, formats and writes nothing, and
/// the same input is byte-identical output: LF newlines, no timestamp, no clock, no culture.
/// </para>
/// <para>
/// <strong>Only the article of record leaves.</strong> A draft that is not accepted is refused. One that was
/// accepted and whose sources have since moved is still its creator's accepted article: it is exported, and
/// the report says the sources moved. Nothing about that is written into the document.
/// </para>
/// <para>
/// <strong>The Markdown is what was accepted, with a title above it and an attribution below.</strong> The
/// sections are <see cref="LongFormDocumentAssembler"/>'s bytes unchanged. The title, an issue's subject, the
/// headings and the recipe titles are escaped to one inert line each; a section's body and an issue's note are
/// the creator's Markdown (B-37) and are written as they stand.
/// </para>
/// <para>
/// <strong>The HTML escapes everything.</strong> A body and a note are rendered by
/// <see cref="LongFormHtmlRenderer"/>, which passes no Markdown HTML through; every other creator string is
/// escaped here. The document carries no script, no style and no address of anything but what the creator
/// linked to. Nothing is fetched and no bytes are embedded.
/// </para>
/// <para>
/// <strong>The title is the creator's.</strong> An issue's subject line, or else the working title of the
/// piece of work; an article's working title. With neither, the document has no title rather than an invented
/// one.
/// </para>
/// </remarks>
public static class LongFormExporter
{
    private const string UnavailableRecipe = "A recipe that is no longer available";

    private const string UntitledRecipe = "An untitled recipe";

    public static LongFormExportReport Export(LongFormExportInput input, LongFormExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Status is not (LongFormDraftStatus.Accepted or LongFormDraftStatus.NeedsReview))
        {
            return Refused(
                ContentErrorCodes.LongFormExportNotAccepted,
                "This piece has not been accepted yet. Accept it to export it.");
        }

        var assembled = LongFormDocumentAssembler.Assemble(input.Kind, input.Document);
        var written = assembled.Sections.Where(section => section.IsWritten).ToList();

        if (written.Count == 0)
        {
            return Refused(
                ContentErrorCodes.LongFormExportNothingWritten,
                "None of this piece has been written, so there is nothing to export.");
        }

        var warnings = new List<LongFormExportIssue>();
        var unwritten = assembled.Sections.Count - written.Count;

        if (unwritten > 0)
        {
            warnings.Add(new(
                LongFormExportCodes.SectionsUnwritten,
                $"{Number(unwritten)} section(s) of the plan have not been written and are not in the document."));
        }

        if (input.Status is LongFormDraftStatus.NeedsReview)
        {
            warnings.Add(new(
                LongFormExportCodes.SourcesMoved,
                "Something this piece was written from has changed since it was accepted. This is the piece as it was accepted."));
        }

        if (input.Recipes.Any(recipe => recipe is null))
        {
            warnings.Add(new(
                LongFormExportCodes.RecipeUnavailable,
                "A recipe this piece was written from can no longer be read, so the document does not name it."));
        }

        var framing = input.Kind is LongFormKind.NewsletterIssue ? input.Document.Framing : null;
        var titleSource = LongFormMarkdownEscaper.Plain(framing?.Subject).Length > 0 ? framing!.Subject : input.Title;
        var title = LongFormMarkdownEscaper.Plain(titleSource);
        var note = string.IsNullOrWhiteSpace(framing?.Note) ? null : LongFormDocumentAssembler.Normalise(framing.Note);

        var content = format switch
        {
            LongFormExportFormat.Markdown => Markdown(input, assembled, titleSource, note),
            LongFormExportFormat.Html => Html(input, written, title, note),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an export format."),
        };

        return new(content, title.Length > 0 ? title : null, [], warnings);
    }

    private static string Markdown(
        LongFormExportInput input, LongFormAssembledDocument assembled, string? titleSource, string? note)
    {
        var sb = new StringBuilder();

        if (LongFormMarkdownEscaper.Line(titleSource) is { Length: > 0 } title)
        {
            sb.Append("# ").Append(title).Append("\n\n");
        }

        if (note is not null)
        {
            sb.Append(note).Append("\n\n");
        }

        // The accepted sections, byte for byte as the creator was shown them.
        sb.Append(assembled.Markdown);

        sb.Append("\n---\n\n");
        sb.Append(Exported(input)).Append('\n');

        if (input.Recipes.Count > 0)
        {
            sb.Append("\nWritten from:\n\n");

            foreach (var recipe in input.Recipes)
            {
                sb.Append("- ").Append(Recipe(recipe, LongFormMarkdownEscaper.Line)).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string Html(
        LongFormExportInput input, List<LongFormAssembledSection> written, string title, string? note)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<title>").Append(Escape(title.Length > 0 ? title : KindName(input.Kind))).Append("</title>\n");
        sb.Append("</head>\n<body>\n<article>\n");

        if (title.Length > 0)
        {
            sb.Append("<h1>").Append(Escape(title)).Append("</h1>\n");
        }

        if (note is not null)
        {
            sb.Append("<div>\n").Append(LongFormHtmlRenderer.Render(note)).Append("</div>\n");
        }

        foreach (var section in written)
        {
            sb.Append("<section>\n");
            sb.Append("<h2>").Append(Escape(LongFormMarkdownEscaper.Plain(section.Heading))).Append("</h2>\n");
            sb.Append(LongFormHtmlRenderer.Render(section.Body!));
            sb.Append("</section>\n");
        }

        sb.Append("<footer>\n");
        sb.Append("<p>").Append(Escape(Exported(input))).Append("</p>\n");

        if (input.Recipes.Count > 0)
        {
            sb.Append("<p>Written from:</p>\n<ul>\n");

            foreach (var recipe in input.Recipes)
            {
                sb.Append("<li>").Append(Recipe(recipe, text => Escape(LongFormMarkdownEscaper.Plain(text)))).Append("</li>\n");
            }

            sb.Append("</ul>\n");
        }

        sb.Append("</footer>\n</article>\n</body>\n</html>\n");

        return sb.ToString();
    }

    private static string Exported(LongFormExportInput input) =>
        $"Exported from CreatorPantry · {KindName(input.Kind)} · Revision {Number(input.RevisionNumber)}";

    private static string Recipe(LongFormExportRecipe? recipe, Func<string?, string> escape)
    {
        if (recipe is null)
        {
            return UnavailableRecipe;
        }

        var title = escape(recipe.Title);

        return $"{(title.Length > 0 ? title : UntitledRecipe)} · Version {Number(recipe.VersionNumber)}";
    }

    private static string KindName(LongFormKind kind) => kind switch
    {
        LongFormKind.BlogArticle => "Blog article",
        LongFormKind.NewsletterIssue => "Newsletter issue",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a long-form kind."),
    };

    /// <summary>
    /// Text made inert in HTML, in an element or a quoted attribute. Only the five characters that can open
    /// markup or close a quote are touched, so every other character — accents, emoji — is written as itself.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);

        foreach (var character in text)
        {
            sb.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => character.ToString(),
            });
        }

        return sb.ToString();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static LongFormExportReport Refused(string code, string message) =>
        new(null, null, [new(code, message)], []);
}
