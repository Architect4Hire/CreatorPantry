using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Reads a creator's Markdown as CommonMark and writes it as HTML for the long-form export (LF.5.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Markdown HTML is not passed through.</strong> The pipeline is CommonMark with raw HTML switched off,
/// so a tag a creator typed comes out as the text of that tag. No extension is enabled: what is rendered is
/// what CommonMark says the Markdown means, and nothing a later extension might add.
/// </para>
/// <para>
/// <strong>A link goes somewhere a reader's browser would be sent on purpose, or it is not a link.</strong>
/// <c>http</c>, <c>https</c> and <c>mailto</c> stay links; a picture's address is <c>http</c> or <c>https</c>.
/// Anything else — a script, a data URI, a relative path with nothing to be relative to — is written as its
/// own text. Decided from the parsed address, so no spelling of a scheme gets past by not looking like one.
/// </para>
/// <para>
/// <strong>A heading inside a body sits under its section's.</strong> A section's heading is the document's
/// second level, so a body's first and second levels are written as the third: nothing a body contains can
/// read as the document's title or as another section. A third level and below is written as it is.
/// </para>
/// <para>
/// The one place Markdig is named. Nothing is fetched: a picture is an address in the output, never its bytes.
/// </para>
/// </remarks>
internal static class LongFormHtmlRenderer
{
    private const int HighestBodyHeading = 3;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>The Markdown as HTML blocks, each ending in a newline. Empty when there is nothing to render.</summary>
    public static string Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            heading.Level = Math.Max(heading.Level, HighestBodyHeading);
        }

        // Listed before any is replaced: replacing one moves its children, and the walk must not be under it.
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            link.GetDynamicUrl = null;

            if (!IsAllowed(link.Url, picture: link.IsImage))
            {
                // What the creator wrote between the brackets stays; where it pointed does not.
                link.ReplaceBy(new ContainerInline(), copyChildren: true);
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (!autolink.IsEmail && !IsAllowed(autolink.Url, picture: false))
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }

        using var writer = new StringWriter { NewLine = "\n" };
        var renderer = new HtmlRenderer(writer);

        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }

    private static bool IsAllowed(string? url, bool picture) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps
            || uri.Scheme == Uri.UriSchemeHttp
            || (!picture && uri.Scheme == Uri.UriSchemeMailto));
}
