using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// The browser's quote outcomes are the server's (AP.2.6): the TypeScript union and the set its decoder consults
/// both name every <see cref="GeneratedImageQuoteOutcome"/> the API can send, so a new outcome cannot reach a
/// creator as a quote that failed to decode — which reads as "we couldn't work out what this costs".
/// </summary>
/// <remarks>
/// <c>Unspecified</c> is left out on purpose: it is the enum's zero and never sent, so the browser refusing it is
/// right. The same file-reading approach as <see cref="WireEnumMirrorTests"/>, kept separate because that test owns
/// one models file and this union lives in another.
/// </remarks>
public sealed class GeneratedImageQuoteWireMirrorTests
{
    private const string ModelsPath = "src/web/src/app/models/generated-image.models.ts";

    private static readonly string[] Sent = Enum.GetNames<GeneratedImageQuoteOutcome>()
        .Where(name => name != nameof(GeneratedImageQuoteOutcome.Unspecified))
        .Order(StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void The_TypeScript_union_names_every_outcome_the_server_sends()
    {
        var union = Between(Read(), "export type GeneratedImageQuoteOutcome =", ";");

        Assert.Equal(Sent, Names(union));
    }

    [Fact]
    public void The_decoders_value_set_names_every_outcome_the_server_sends()
    {
        var set = Between(
            Read(),
            "const GENERATED_IMAGE_QUOTE_OUTCOME_VALUES: ReadonlySet<string> = new Set<GeneratedImageQuoteOutcome>([",
            "]);");

        Assert.Equal(Sent, Names(set));
    }

    // The quality classes the picker offers (AP.3.1A). A class the browser does not know is dropped from the price
    // list rather than refusing it, so a new one would never be offered — quietly. This is what makes it loud.
    private static readonly string[] Classes = Enum.GetNames<GeneratedImageQualityClass>()
        .Order(StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void The_TypeScript_union_names_every_quality_class_the_server_offers()
    {
        var union = Between(Read(), "export type GeneratedImageQualityClass =", ";");

        Assert.Equal(Classes, Names(union));
    }

    [Fact]
    public void The_decoders_class_set_names_every_quality_class_the_server_offers()
    {
        var set = Between(
            Read(),
            "const GENERATED_IMAGE_QUALITY_CLASS_VALUES: ReadonlySet<string> = new Set<GeneratedImageQualityClass>([",
            "]);");

        Assert.Equal(Classes, Names(set));
    }

    private static string[] Names(string body) =>
        Regex.Matches(body, @"'([A-Za-z][A-Za-z0-9]*)'").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    private static string Between(string source, string opening, string closing)
    {
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{opening}' is not in {ModelsPath}");

        var from = start + opening.Length;
        var end = source.IndexOf(closing, from, StringComparison.Ordinal);
        Assert.True(end > from, $"'{opening}' is never closed in {ModelsPath}");

        return source[from..end];
    }

    /// <summary>The models file without its comments, so a member named in prose is not read as a member.</summary>
    private static string Read([CallerFilePath] string callerPath = "") =>
        Regex.Replace(
            File.ReadAllText(Path.Combine(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "..")), ModelsPath)),
            @"/\*.*?\*/|//[^\n]*",
            string.Empty,
            RegexOptions.Singleline);
}
