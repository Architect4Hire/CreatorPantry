namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// What a cited source says about one allergen in one ingredient. There is no member meaning safe, free from,
/// or suitable for an allergic person, and none may be added (recipes.md, ai.md).
/// </summary>
/// <remarks>
/// <para>
/// Note what is missing and why. Whether a food is <em>safe</em> for someone is a claim about a person, a
/// facility, and a production run — not a property of an ingredient — so this vocabulary cannot express it.
/// The strongest thing a source can say is <see cref="NotListedBySource"/>: that the allergen did not appear
/// in a composition the source published. That is evidence, not a guarantee, and its name is chosen so it
/// cannot be rendered in an interface as one.
/// </para>
/// <para>
/// This is asymmetric with <see cref="DietaryCompatibility"/> on purpose. Being vegan is a compositional fact
/// about an ingredient, so a positive compatibility claim is meaningful. Being safe for an allergic person is
/// not, so no equivalent member exists here. Do not unify the two enums.
/// </para>
/// <para>
/// A missing trait row means <see cref="Unknown"/>, always. Nothing in this model defaults toward absence:
/// there is no boolean to leave <c>false</c> and no safe member to fall back on, so silence can only ever
/// resolve to "we do not know".
/// </para>
/// <para>
/// These numeric values are persisted and named literally by the check constraints on
/// <c>IngredientAllergenTraits</c>. Do not renumber them.
/// </para>
/// </remarks>
public enum AllergenPresence
{
    /// <summary>
    /// The source was consulted and does not say. Identical in meaning to no row at all; recorded explicitly
    /// so that "nobody has looked" and "we looked and it is not stated" are distinguishable to a reviewer
    /// without ever being distinguishable to an analysis, which must treat both as unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>The source states the allergen is present.</summary>
    Present = 10,

    /// <summary>
    /// The source states a possibility — shared equipment, a "may contain" declaration, a variable supply.
    /// Only ever recorded because a source said so. Cross-contact is never inferred from an ingredient's
    /// category, its neighbours in a recipe, or a model's guess.
    /// </summary>
    PossiblePresence = 20,

    /// <summary>
    /// The source published a composition and this allergen was not in it. The strongest absence claim
    /// available, and still not a safety statement: it speaks about one source's listing, not about
    /// manufacturing, cross-contact, or an individual's tolerance. Restricted at the database level to vetted
    /// sources, because absence is the direction in which a bad claim does harm.
    /// </summary>
    NotListedBySource = 30,
}
