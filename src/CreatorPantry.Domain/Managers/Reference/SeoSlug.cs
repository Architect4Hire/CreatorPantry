using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Derives a URL slug from a title, deterministically (ai.md: identifier resolution stays in code).
/// </summary>
/// <remarks>
/// <para>
/// Lowercase ASCII letters and digits separated by single hyphens, with no leading or trailing hyphen, cut at a
/// hyphen boundary where one exists inside the limit. Accents are folded to their base letter ("crème" becomes
/// "creme"); any other character with no ASCII base separates words, so "smørrebrød" becomes "sm-rrebr-d" and
/// "350°F" becomes "350-f"; "&amp;" becomes "and". A title with nothing usable
/// yields the empty string, and the caller chooses a fallback — this function never invents one.
/// </para>
/// <para>
/// Whether the slug is unique among a creator's published pages is not something this can know: there is no
/// publication store to ask. Nothing here claims it.
/// </para>
/// </remarks>
public static class SeoSlug
{
    public static string FromTitle(string? title, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var folded = title.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(folded.Length);
        var pendingHyphen = false;

        foreach (var character in folded)
        {
            if (char.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);

            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(lower);
                pendingHyphen = false;
            }
            else if (lower is not '\'' and not '’')
            {
                // An apostrophe joins ("don't" is "dont"); everything else separates.
                pendingHyphen = true;
            }
        }

        var result = slug.ToString();

        if (result.Length <= maxLength)
        {
            return result;
        }

        var cut = result[..maxLength];
        var lastHyphen = cut.LastIndexOf('-');

        return (lastHyphen > 0 && result[maxLength] != '-' ? cut[..lastHyphen] : cut).TrimEnd('-');
    }

    /// <summary>
    /// Path segments a site commonly serves itself. A recipe at one of these would be shadowed by, or shadow, the
    /// site's own page, so such a slug is never used as it stands.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin", "api", "auth", "blog", "cart", "category", "categories", "checkout", "favicon", "feed", "home",
        "index", "login", "logout", "null", "page", "pages", "robots", "rss", "search", "sitemap", "tag", "tags",
        "undefined", "wp-admin", "wp-content", "wp-json",
    };

    public static bool IsReserved(string slug) => Reserved.Contains(slug);

    /// <summary>
    /// A slug that is safe to offer: <paramref name="slug"/> unless it is reserved, in which case it gains a
    /// <c>-recipe</c> suffix inside the limit. An empty slug becomes <c>recipe-</c> and the first eight characters
    /// of the recipe's id, so two recipes with nothing usable in their titles do not share one slug.
    /// </summary>
    public static string Offer(string slug, Guid recipeId, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        if (slug.Length == 0)
        {
            return ("recipe-" + recipeId.ToString("N")[..8])[..Math.Min(maxLength, 15)].TrimEnd('-');
        }

        if (!IsReserved(slug))
        {
            return slug;
        }

        var suffix = "-recipe";
        var room = Math.Max(1, maxLength - suffix.Length);

        return (slug.Length > room ? slug[..room].TrimEnd('-') : slug) + suffix;
    }
}
