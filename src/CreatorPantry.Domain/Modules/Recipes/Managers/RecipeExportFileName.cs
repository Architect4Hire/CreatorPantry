using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A file name for an exported recipe that is safe to put in a <c>Content-Disposition</c> header and on any
/// file system, and is the same for the same recipe every time.
/// </summary>
/// <remarks>
/// ASCII only, lower case, <c>a-z0-9</c> and single hyphens, so there is nothing to quote, encode or escape and
/// no way for a title to smuggle in a path separator, a quote, a line break or a dot segment. It carries the
/// version number and nothing that identifies a workspace, a recipe id or any storage location.
/// </remarks>
public static class RecipeExportFileName
{
    public const int MaxSlugLength = 60;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <param name="title">The recipe's title; any text at all.</param>
    /// <param name="versionNumber">The exported version, appended as <c>-v{n}</c>.</param>
    /// <param name="extension">The extension without its dot, from the caller's own constants.</param>
    public static string For(string? title, int versionNumber, string extension) =>
        $"{Slug(title)}-v{versionNumber.ToString(CultureInfo.InvariantCulture)}.{extension}";

    public static string Slug(string? title)
    {
        var slug = new StringBuilder();
        var pendingHyphen = false;

        foreach (var c in Fold(title ?? string.Empty))
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && slug.Length > 0) slug.Append('-');
                pendingHyphen = false;
                slug.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var text = slug.Length > MaxSlugLength ? slug.ToString(0, MaxSlugLength).TrimEnd('-') : slug.ToString();

        return text.Length == 0 ? "recipe" : Reserved.Contains(text) ? "recipe-" + text : text;
    }

    /// <summary>Lower-cases, drops accents, and spells out the few letters that have no decomposition.</summary>
    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var folded = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            folded.Append(c switch
            {
                'æ' => "ae",
                'œ' => "oe",
                'ß' => "ss",
                'ø' => "o",
                'đ' => "d",
                'ł' => "l",
                'þ' => "th",
                'ð' => "d",
                _ => c.ToString(),
            });
        }

        return folded.ToString();
    }
}
