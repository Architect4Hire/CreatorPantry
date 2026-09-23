using CreatorPantry.Domain.Managers.Reference;
namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// What a cited source says about one ingredient's compatibility with one <see cref="CreatorPantry.Domain.Modules.Vocabulary.Data.Entities.DietaryProfile"/>.
/// Descriptive metadata about composition — never a statement of medical suitability (recipes.md).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="AllergenPresence"/>, a positive claim is meaningful here: whether an ingredient is vegan
/// or contains gluten is a fact about what it is made of, which a source can state outright. That is why this
/// enum has <see cref="Compatible"/> and the allergen one has no equivalent. The asymmetry is the model.
/// </para>
/// <para>
/// <see cref="Compatible"/> still says only that an ingredient fits a dietary pattern as the cited source
/// describes it. It does not say a dish containing it is suitable for a particular person, and a profile whose
/// stakes are medical — coeliac disease rather than a gluten preference — is governed by the allergen traits
/// and their restrictions, not by this.
/// </para>
/// <para>
/// A missing trait row means <see cref="Unknown"/>, always.
/// </para>
/// <para>
/// These numeric values are persisted and named literally by the check constraints on
/// <c>IngredientDietaryTraits</c>. Do not renumber them.
/// </para>
/// </remarks>
public enum DietaryCompatibility
{
    /// <summary>
    /// The source was consulted and does not say. Identical in meaning to no row at all; see
    /// <see cref="AllergenPresence.Unknown"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>The source states the ingredient fits the profile.</summary>
    Compatible = 10,

    /// <summary>The source states the ingredient does not fit the profile — butter against a vegan profile.</summary>
    Incompatible = 20,

    /// <summary>
    /// Compatibility genuinely varies by brand, process, or certification: cane sugar filtered through bone
    /// char, oats milled alongside wheat, wine fined with isinglass. A two-state model would have to answer
    /// one of these wrongly, so the varying case gets its own state and a required note explaining what it
    /// depends on.
    /// </summary>
    DependsOnProduct = 30,
}
