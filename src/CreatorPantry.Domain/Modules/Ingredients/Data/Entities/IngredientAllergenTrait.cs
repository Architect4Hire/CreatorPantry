using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

/// <summary>
/// What one cited source says about one allergen in one ingredient, as of one date. Global reference data: no
/// <c>WorkspaceId</c>, not <see cref="Tenancy.IWorkspaceOwned"/>.
/// </summary>
/// <remarks>
/// <para>
/// The row is a record of evidence, not a verdict. Its <see cref="Presence"/> vocabulary has no safe member
/// (see <see cref="AllergenPresence"/>), and the entity has no boolean property of any kind — an
/// <c>IsAllergenFree</c> here would be read as a guarantee by every screen that touched it, so there is
/// nowhere for one to hide. A guard test asserts that absence.
/// </para>
/// <para>
/// The absence of a row for an ingredient and allergen means unknown. It never means absent, and no default,
/// fallback, or inference anywhere may turn it into absent (recipes.md, ai.md).
/// </para>
/// <para>
/// Several sources may disagree about the same ingredient and allergen, and one source may revise itself on a
/// later date; the schema records them all. Choosing between them is a domain decision, made later with the
/// provenance and review state in hand.
/// </para>
/// </remarks>
public class IngredientAllergenTrait
{
    public Guid Id { get; set; }

    public Guid IngredientId { get; set; }

    public Guid AllergenId { get; set; }

    /// <summary>Required: provenance is not optional for a reference fact, least of all this one.</summary>
    public Guid ReferenceSourceId { get; set; }

    /// <summary>
    /// Always equal to the referenced source's <see cref="ReferenceSource.Kind"/>.
    /// </summary>
    /// <remarks>
    /// Redundant by design, exactly as on <see cref="IngredientDensityReference.ReferenceSourceKind"/>. The
    /// composite foreign key <c>(ReferenceSourceId, ReferenceSourceKind)</c> onto <c>ReferenceSources
    /// (Id, Kind)</c> means this column cannot disagree with the source it names, which is what lets two check
    /// constraints in this table compare provenance against <see cref="Presence"/> and
    /// <see cref="ReviewStatus"/> without a join: a model-generated trait can never be approved, and an
    /// unvetted source can never assert absence.
    /// </remarks>
    public ReferenceSourceKind ReferenceSourceKind { get; set; }

    public AllergenPresence Presence { get; set; }

    /// <summary>
    /// What the source actually stated, in its own terms — "labelled may contain traces of peanut",
    /// "ingredient list published 2026-03, no milk derivatives". Required for
    /// <see cref="AllergenPresence.PossiblePresence"/> and <see cref="AllergenPresence.NotListedBySource"/>,
    /// the two states a reader could otherwise mistake for a safety conclusion; see
    /// <see cref="TraitPolicy.RequiresEvidenceNote(AllergenPresence)"/>. Empty otherwise, never null.
    /// </summary>
    public string EvidenceNote { get; set; } = TraitPolicy.NoEvidenceNote;

    /// <summary>
    /// The date this claim applies from. A <see cref="DateOnly"/> rather than an instant, for the reason given
    /// on <see cref="IngredientDensityReference.EffectiveFrom"/>: sources are effective on a date, and a time
    /// and offset would invent precision they never stated.
    /// </summary>
    /// <remarks>
    /// Allergen information ages in a way densities do not — a manufacturer changes a line, a supplier changes
    /// a filler — which is what makes the date part of the natural key rather than an audit field. Supersession
    /// is a later row plus <see cref="TraitReviewStatus.Superseded"/> on the old one.
    /// </remarks>
    public DateOnly EffectiveFrom { get; set; }

    public TraitReviewStatus ReviewStatus { get; set; }
}
