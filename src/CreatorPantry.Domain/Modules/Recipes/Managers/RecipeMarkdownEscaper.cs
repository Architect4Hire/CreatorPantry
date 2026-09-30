using System.Text;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Makes creator-supplied or imported text inert in Markdown: it can read as itself but cannot become
/// structure, raw HTML or a link. Unicode is preserved.
/// </summary>
internal static class RecipeMarkdownEscaper
{
    private const string InlineSpecials = "\\`*_[]<>|~&";

    /// <summary>One line of text; any line breaks collapse to a single space.</summary>
    public static string Line(string? text) =>
        Escape(Lines(text) is { Count: > 0 } lines ? string.Join(' ', lines) : string.Empty);

    /// <summary>
    /// Text that may span lines: each line is escaped on its own and joined with a hard break followed by
    /// <paramref name="continuationIndent"/>, so a continuation stays inside its list item.
    /// </summary>
    public static string Block(string? text, string continuationIndent = "") =>
        string.Join("  \n" + continuationIndent, Lines(text).Select(Escape));

    private static List<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            cleaned.Append(c switch
            {
                '\t' => ' ',
                '\r' => '\n',
                _ when c != '\n' && char.IsControl(c) => '\0',
                _ => c,
            });
        }

        return cleaned.ToString()
            .Replace("\0", string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string Escape(string line)
    {
        var sb = new StringBuilder(line.Length + 4);
        foreach (var c in line)
        {
            if (InlineSpecials.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }

        var escaped = sb.ToString();

        // A line that opens like a heading, bullet, rule or numbered item would be structure, not text.
        if (escaped.Length > 0 && "#+-=".Contains(escaped[0])) return "\\" + escaped;

        var digits = 0;
        while (digits < escaped.Length && char.IsAsciiDigit(escaped[digits])) digits++;
        if (digits is > 0 and <= 9 && digits < escaped.Length && escaped[digits] is '.' or ')')
        {
            return escaped.Insert(digits, "\\");
        }

        return escaped;
    }
}
