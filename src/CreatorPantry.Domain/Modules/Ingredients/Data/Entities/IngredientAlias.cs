using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

/// <summary>
/// A surface form that resolves to one <see cref="Ingredient"/> — "scallion" for green onion, "cornflour"
/// for cornstarch, "aubergine" for eggplant. Global reference data with no <c>WorkspaceId</c>.
/// </summary>
/// <remarks>
/// <para>
/// Aliases exist so hand-typed and imported ingredient lines can be <em>recognised</em>. A match records a
/// reference alongside the creator's wording; it never rewrites the line (recipes.md).
/// </para>
/// <para>
/// An alias is another name for the <em>same</em> thing, never a narrower or a richer one. "Cornflour" is
/// cornstarch; US "corn flour" is milled maize and a different ingredient entirely. "Whipping cream" is not
/// heavy cream, and "extra virgin olive oil" is not olive oil in general. Matching across such a difference
/// would quietly change what a recipe means, which is the one thing a reference is forbidden to do.
/// </para>
/// </remarks>
public class IngredientAlias
{
    public Guid Id { get; set; }

    public Guid IngredientId { get; set; }

    /// <summary>The alias as written, casing and punctuation intact, for display and provenance.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// The lookup key from <see cref="NameNormalization.NormalizeName"/>. Unique across every ingredient, not
    /// merely within one: an alias matching two ingredients would make a recipe line ambiguous, and there is
    /// no context at this layer to break the tie, so the database refuses to store the ambiguity.
    /// </summary>
    /// <remarks>
    /// One collision this cannot catch: an alias here equal to some <em>other</em> ingredient's
    /// <see cref="Ingredient.NormalizedName"/>. No single unique index spans two tables. Resolution precedence
    /// is therefore a documented rule — a canonical name always wins over an alias — verified by the seed-set
    /// tests rather than by the schema.
    /// </remarks>
    public string NormalizedAlias { get; set; } = string.Empty;
}
