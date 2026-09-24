using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// Resolves free-form candidate text against the flattened ingredient/alias index — exact string equality on
/// normalized text only, never a fuzzy or partial match.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient.NormalizedName"/> and
/// <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.IngredientAlias.NormalizedAlias"/> are
/// each unique within their own table, but nothing stops an alias equalling a <em>different</em> ingredient's
/// canonical name — no single database index spans both tables. This resolver applies the documented
/// precedence for exactly that case (a canonical name always outranks an alias; see
/// <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.IngredientAlias.NormalizedAlias"/>'s own
/// remarks) and only calls a candidate genuinely ambiguous when two different ingredients tie at the very
/// same precedence tier, which a canonical-name/alias pair never can.
/// </para>
/// <para>
/// Pure and stateless, like <see cref="CreatorPantry.Domain.Modules.Recipes.Managers.RecipeComparer"/>: given
/// the same index and the same text twice, it answers the same way twice.
/// </para>
/// </remarks>
public static class IngredientMatcher
{
    public static IngredientMatchResult Resolve(string inputText, ILookup<string, IngredientMatchIndexEntry> index)
    {
        var normalized = NameNormalization.NormalizeName(inputText);
        var hits = index[normalized].ToList();

        if (hits.Count == 0)
        {
            return new IngredientMatchResult { InputText = inputText };
        }

        // Collapse to one entry per ingredient first: a canonical name and its own alias can normalize to the
        // same text, and that is not a tie between two things — it is one ingredient found two ways.
        var perIngredient = hits
            .GroupBy(hit => hit.IngredientId)
            .Select(group => group.OrderBy(hit => hit.Kind).First())
            .OrderBy(hit => hit.Kind)
            .ThenBy(hit => hit.CanonicalName, StringComparer.Ordinal)
            .ToList();

        var bestKind = perIngredient[0].Kind;
        var tiedAtBest = perIngredient.Where(hit => hit.Kind == bestKind).ToList();

        if (tiedAtBest.Count > 1)
        {
            return new IngredientMatchResult
            {
                InputText = inputText,
                IsAmbiguous = true,
                Alternates = perIngredient.Select(ToCandidate).ToList(),
            };
        }

        return new IngredientMatchResult
        {
            InputText = inputText,
            Resolved = ToCandidate(perIngredient[0]),
            Alternates = perIngredient.Skip(1).Select(ToCandidate).ToList(),
        };
    }

    private static IngredientMatchCandidate ToCandidate(IngredientMatchIndexEntry entry) =>
        new(entry.IngredientId, entry.CanonicalName, entry.Kind);
}
