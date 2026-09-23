using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Limits and normalization rules for the shared ingredient vocabulary. Global reference data: no
/// <c>WorkspaceId</c>, never duplicated per workspace (tenancy.md).
/// </summary>
/// <remarks>
/// This vocabulary <em>enriches</em> what a creator typed and never replaces it (recipes.md). Matching an
/// ingredient records a reference beside the creator's own wording; the entered text stays canonical.
/// </remarks>
public static class IngredientPolicy
{
    public const int NameMaxLength = 128;

    public const int SearchTextMaxLength = 512;

    /// <summary>Separates whole phrases inside <see cref="Data.Ingredient.SearchText"/>.</summary>
    public const string SearchTextSeparator = " | ";

    /// <summary>
    /// The lookup form of an ingredient name or alias: diacritics folded, lowercased, punctuation reduced to
    /// single spaces. <c>All-Purpose Flour (Unbleached)</c> becomes <c>all purpose flour unbleached</c>, and
    /// <c>Jalapeño</c> becomes <c>jalapeno</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately does <em>not</em> singularize, stem, or drop stopwords. Stemming is lossy and
    /// English-specific; plural and colloquial forms are recorded explicitly as
    /// <see cref="Data.IngredientAlias"/> rows instead, where they stay inspectable and correctable.
    /// </para>
    /// <para>
    /// This differs from <see cref="MeasurementPolicy.NormalizeAlias"/>, which removes separators outright so
    /// that <c>fl. oz.</c> collapses to <c>floz</c>. Unit codes are short and drawn from a closed set, so
    /// collapsing them is safe. Ingredient names are multi-word and open-ended, where <c>cream cheese</c>
    /// collapsing to <c>creamcheese</c> would destroy the word boundaries search depends on. The two rules are
    /// intentionally distinct — do not unify them.
    /// </para>
    /// </remarks>
    public static string NormalizeName(string name)
    {
        var folded = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);

        foreach (var character in folded)
        {
            // Drop the combining marks that decomposing exposed, so "é" is indexed as "e".
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            // Any other character — hyphen, period, comma, ampersand, parenthesis — becomes a boundary.
            // Appending at most one trailing space keeps runs collapsed without a second pass.
            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Builds the denormalized <see cref="Data.Ingredient.SearchText"/>: the distinct normalized phrases for
    /// one ingredient — canonical name first, then aliases sorted — joined by
    /// <see cref="SearchTextSeparator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whole phrases rather than a bag of words, so a search for <c>cream cheese</c> still matches; a word set
    /// would reorder it to <c>cheese cream</c> and stop matching. This column is a candidate-generation aid for
    /// a <c>LIKE</c> prefilter — it is derived data, not a ranking signal and not authoritative. The alias rows
    /// remain the source of truth, and this must be rebuilt whenever the name or the aliases change.
    /// </para>
    /// <para>
    /// Aliases are sorted here rather than taken in the order supplied, which makes the output a pure function
    /// of the ingredient's data. Without that, the same ingredient rebuilt from a database query would produce
    /// a different string whenever the query planner returned the alias rows in a different order, and drift
    /// detection on a derived column would be comparing against a moving target.
    /// </para>
    /// <para>
    /// Truncates on a separator boundary rather than mid-phrase if the result would exceed
    /// <see cref="SearchTextMaxLength"/>, so the column never holds a partial phrase that could match something
    /// the ingredient is not.
    /// </para>
    /// </remarks>
    public static string BuildSearchText(string normalizedName, IEnumerable<string> normalizedAliases)
    {
        var phrases = new List<string> { normalizedName };
        phrases.AddRange(normalizedAliases.Order(StringComparer.Ordinal));

        var builder = new StringBuilder();
        foreach (var phrase in phrases.Where(phrase => !string.IsNullOrWhiteSpace(phrase)).Distinct(StringComparer.Ordinal))
        {
            var addition = builder.Length == 0 ? phrase : SearchTextSeparator + phrase;
            if (builder.Length + addition.Length > SearchTextMaxLength)
            {
                break;
            }

            builder.Append(addition);
        }

        return builder.ToString();
    }
}
