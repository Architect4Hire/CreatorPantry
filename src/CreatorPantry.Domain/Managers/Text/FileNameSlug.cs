using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Managers.Text;

/// <summary>
/// Creator text reduced to a file name that is safe in a <c>Content-Disposition</c> header and on any file
/// system, and is the same for the same text every time.
/// </summary>
/// <remarks>
/// <para>
/// ASCII only, lower case, <c>a-z0-9</c> and single hyphens, so there is nothing to quote, encode or escape
/// and no way for a title to smuggle in a path separator, a quote, a line break or a dot segment.
/// </para>
/// <para>
/// In the shared kernel because two modules now put creator text in a download header — a recipe export and a
/// brand source document — and the alternative was two copies of the same Unicode folding, which would drift
/// the first time one of them learned about a new letter. It takes no entity and knows no module: a string,
/// a fallback and a length.
/// </para>
/// <para>
/// It is deliberately <strong>not</strong> a whole file name. The extension and any version suffix belong to
/// the module that knows the format, so nothing here has to be told what a media type means.
/// </para>
/// </remarks>
public static class FileNameSlug
{
    public const int MaxLength = 60;

    /// <summary>
    /// Device names Windows refuses as a file name whatever the extension. Prefixed rather than rejected, so
    /// a creator who titles something "aux" still gets a download.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <param name="text">Any text at all, including null — a title, a filename, anything the creator wrote.</param>
    /// <param name="fallback">
    /// What to use when nothing survives folding, and the prefix for a reserved device name. The caller's own
    /// word, so a recipe says "recipe" and a document says "document"; it must already be a valid slug.
    /// </param>
    /// <param name="maxLength">The most characters to keep, trimmed of a trailing hyphen.</param>
    public static string From(string? text, string fallback, int maxLength = MaxLength)
    {
        var slug = new StringBuilder();
        var pendingHyphen = false;

        foreach (var c in Fold(text ?? string.Empty))
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && slug.Length > 0) slug.Append('-');
                pendingHyphen = false;
                slug.Append(c);
            }
            else
            {
                // Any run of anything else becomes at most one hyphen, and never a leading or trailing one.
                pendingHyphen = true;
            }
        }

        var folded = slug.Length > maxLength ? slug.ToString(0, maxLength).TrimEnd('-') : slug.ToString();

        return folded.Length == 0 ? fallback : Reserved.Contains(folded) ? $"{fallback}-{folded}" : folded;
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
