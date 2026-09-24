namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Whether a recipe's ingredient lines have all been resolved against the shared ingredient vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not a dietary, allergen, or nutrition state, and must never be presented as one.</strong>
/// It reports one thing only: whether every <see cref="IngredientMatchStatus"/> on the recipe is
/// <see cref="IngredientMatchStatus.Matched"/>. That matters because an unmatched line has no vocabulary entry
/// behind it, and therefore no dietary or allergen traits to read — so an unresolved line is what *blocks* a
/// later analysis, not a finding produced by one. recipes.md and ai.md both forbid presenting a dietary or
/// allergen conclusion as settled, and a filter named for a conclusion the system has not reached would be
/// exactly that. The requirement calls this the recipe's dietary-review state; the field is named for what the
/// data actually says.
/// </para>
/// <para>
/// An enum rather than a <c>bool</c> so a narrower question — lines that are specifically
/// <see cref="IngredientMatchStatus.Ambiguous"/>, say, which the creator can resolve, as distinct from
/// <see cref="IngredientMatchStatus.NoMatch"/>, which they cannot — can be added without turning a shipped
/// <c>bool</c> field into an enum, which api-contract.md counts as breaking.
/// </para>
/// <para>
/// Not persisted; this is a filter, never a column. Numbering starts at one so that a value nobody assigned
/// is out of range and fails loudly, rather than defaulting to a claim about a recipe.
/// </para>
/// </remarks>
public enum RecipeIngredientReviewFilter
{
    /// <summary>At least one ingredient line is not matched to the vocabulary.</summary>
    HasUnmatched = 1,

    /// <summary>
    /// Every ingredient line is matched. Also true of a recipe with no ingredient lines at all, which is
    /// vacuous but correct: there is nothing on it left to resolve.
    /// </summary>
    AllMatched = 2,
}
