namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Where an ingredient line's <c>DisplayText</c> came from: wording the creator wrote, or wording assembled
/// from the structured fields they filled in.
/// </summary>
/// <remarks>
/// <para>
/// recipes.md keeps the line canonical and forbids rewriting it, and honouring that rule needs this
/// distinction rather than a guess. An editor that offers a creator a quantity, a unit, an ingredient name and
/// a preparation note — and no control for the line itself — has to assemble a line out of those fields, or
/// the line is empty and the whole row is lost. Having assembled one, it must keep it in step with the fields
/// it came from, while never touching a line the creator wrote. Recorded rather than inferred, because after a
/// reload the two are indistinguishable: a line that happens to read exactly like its own parts may still be
/// the creator's wording, and re-deriving it would be the rewrite the rule forbids.
/// </para>
/// <para>
/// A fact about wording, never a permission. Nothing here licenses changing a quantity, a unit, a time or an
/// instruction, and a <see cref="Composed"/> line is as canonical as any other once stored — it is what the
/// recipe says, and a derivative that quotes the line quotes it.
/// </para>
/// </remarks>
public enum IngredientDisplayTextSource
{
    /// <summary>
    /// The creator wrote or pasted this line. It stays verbatim, and nothing re-derives it. The default, and
    /// what every line predating this distinction is.
    /// </summary>
    Creator = 0,

    /// <summary>
    /// Assembled from this line's own quantity, unit, ingredient-name and preparation text, because the
    /// creator entered those instead of a line. Re-derived when one of them changes, and only from them.
    /// </summary>
    Composed = 1,
}
