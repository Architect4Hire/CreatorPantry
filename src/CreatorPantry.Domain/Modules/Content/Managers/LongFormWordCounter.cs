using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Counts a long-form body the two ways the policy needs: characters for the ceilings, words for the targets.
/// Deterministic, culture-independent, and no model is involved.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Characters</strong> are grapheme clusters of the Markdown exactly as stored, syntax included — what a
/// reader would call a character, so an emoji or an accented letter is one. The same rule the <c>blog</c> and
/// <c>newsletter</c> channel profiles count by.
/// </para>
/// <para>
/// <strong>Words.</strong> Markdown that is not prose is set aside, the rest is split on whitespace, and a
/// piece is a word when it holds at least one letter or digit.
/// </para>
/// <list type="bullet">
/// <item>A hyphenated compound is one word: <c>well-known</c>, <c>slow-cooked</c>.</item>
/// <item>An em dash, an en dash, or two or more hyphens together separate words, spaced or not.</item>
/// <item>A numeral run is one word: <c>350</c>, <c>2.5</c>, <c>1/2</c>, <c>350°F</c>, <c>9x13</c>. So
/// <c>1 1/2 cups</c> is three.</item>
/// <item>A contraction or possessive is one word: <c>don't</c>, <c>baker's</c>.</item>
/// <item>Heading marks, bullets, blockquote marks, emphasis marks, backticks, table pipes and horizontal rules
/// count for nothing, and neither does an ordered list's number.</item>
/// <item>A link counts its text and not its address; an image counts for nothing; a bare address is one
/// word.</item>
/// <item>A code fence's marker line counts for nothing; what is inside the fence is counted as prose.</item>
/// </list>
/// <para>
/// A change to any of this changes results for stored documents, so it bumps <see cref="LongFormPolicy.Version"/>.
/// </para>
/// </remarks>
public static partial class LongFormWordCounter
{
    public static int CountCharacters(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

    public static int CountWords(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return 0;
        }

        var prose = new StringBuilder(markdown.Length);

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                continue;
            }

            line = Image().Replace(line, " ");
            line = Link().Replace(line, " $1 ");
            line = OrderedListMarker().Replace(line, string.Empty);
            line = Dash().Replace(line, " ");

            prose.Append(line.Replace('|', ' ')).Append(' ');
        }

        var words = 0;
        var inPiece = false;
        var pieceIsWord = false;

        foreach (var rune in prose.ToString().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                words += inPiece && pieceIsWord ? 1 : 0;
                inPiece = false;
                pieceIsWord = false;

                continue;
            }

            inPiece = true;
            pieceIsWord |= Rune.IsLetterOrDigit(rune);
        }

        return words + (inPiece && pieceIsWord ? 1 : 0);
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    // After any blockquote marks and bullets a list item may be nested under.
    [GeneratedRegex(@"^(?:[>\-*+]\s*)*\d{1,9}[.)](?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListMarker();

    [GeneratedRegex(@"[–—]|-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Dash();
}
