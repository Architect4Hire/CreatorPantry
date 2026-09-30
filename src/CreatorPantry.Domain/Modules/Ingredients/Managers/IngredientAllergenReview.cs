namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// How complete the recorded allergen picture of one ingredient is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every member is a statement about the records, never about the food.</strong> This says how much has
/// been written down and vetted; it never says an ingredient contains an allergen, and — the direction that
/// matters more — it never says one does not. recipes.md forbids inferring allergen absence from missing data,
/// which is exactly why <see cref="NoTraitsRecorded"/> exists as its own answer rather than collapsing into
/// <see cref="Reviewed"/>: an ingredient nobody has recorded anything about is not a clean one.
/// </para>
/// <para>
/// <strong>One state per ingredient, by precedence</strong>, because a caller wants to know what to do next
/// rather than receive every trait row. Nothing recorded outranks a claim awaiting review, which outranks a
/// vetted claim that is itself uncertain. <c>Rejected</c> and <c>Superseded</c> traits are not current claims and
/// count for nothing here, so an ingredient whose only trait was rejected reads as
/// <see cref="NoTraitsRecorded"/>.
/// </para>
/// <para>
/// Not persisted; this is derived on read. Numbering starts at one so that a value nobody assigned is out of
/// range and fails loudly rather than defaulting to a claim about an ingredient.
/// </para>
/// </remarks>
public enum IngredientAllergenReviewState
{
    /// <summary>
    /// At least one allergen trait is recorded, every one of them has been reviewed, and none of the reviewed
    /// ones is itself uncertain. The most that can be said, and still not a guarantee about the food.
    /// </summary>
    Reviewed = 1,

    /// <summary>
    /// No current allergen trait exists for this ingredient. Nothing is known, which is emphatically not the
    /// same as nothing being present.
    /// </summary>
    NoTraitsRecorded = 2,

    /// <summary>At least one recorded trait is still <see cref="TraitReviewStatus.Unreviewed"/>.</summary>
    AwaitingReview = 3,

    /// <summary>
    /// Every trait has been reviewed, and at least one reviewed trait records
    /// <see cref="AllergenPresence.Unknown"/> or <see cref="AllergenPresence.PossiblePresence"/> — a vetted
    /// source that itself declines to settle the question.
    /// </summary>
    PresenceUncertain = 4,
}

/// <summary>
/// One ingredient whose recorded allergen picture is not complete, as another module reads it.
/// </summary>
/// <remarks>
/// <para>
/// Published by <see cref="Facade.IIngredientFacade.FindAllergenReviewGapsAsync"/>, which returns only the
/// ingredients that are <em>not</em> <see cref="IngredientAllergenReviewState.Reviewed"/>. A caller asking about
/// forty lines and getting nothing back has its answer in the emptiness.
/// </para>
/// <para>
/// <strong>It names no allergen, and that is deliberate.</strong> The question this answers is "has anybody
/// finished checking", and naming the allergen behind a gap would invite a reader to present an unreviewed or
/// uncertain trait as a finding about the food. The allergen belongs to a surface that states its provenance and
/// its uncertainty; a completeness report is not one.
/// </para>
/// </remarks>
/// <param name="CanonicalName">
/// The vocabulary's own name for the ingredient, so a caller can say which line it means without holding the
/// catalogue. Not the creator's entered text, which this module has never seen.
/// </param>
public sealed record IngredientAllergenReviewServiceModel(
    Guid IngredientId,
    string CanonicalName,
    IngredientAllergenReviewState State);

/// <summary>
/// The counts one ingredient's current allergen traits produce, before anything has decided what they mean.
/// </summary>
/// <remarks>
/// A repository row rather than a published shape. The precedence that turns these three numbers into one
/// <see cref="IngredientAllergenReviewState"/> is a domain rule and lives in Business, so the query stays a
/// query — and so the rule can be tested without a database.
/// </remarks>
/// <param name="CurrentTraitCount">
/// Traits that are current claims: <see cref="TraitReviewStatus.Approved"/> or
/// <see cref="TraitReviewStatus.Unreviewed"/>. Rejected and superseded rows are excluded, because a withdrawn
/// claim is not a claim.
/// </param>
/// <param name="AwaitingReviewCount">Of those, how many are still unreviewed.</param>
/// <param name="UncertainReviewedCount">
/// Of those, how many are reviewed and still record an unsettled presence.
/// </param>
public sealed record IngredientAllergenTraitCounts(
    Guid IngredientId,
    string CanonicalName,
    int CurrentTraitCount,
    int AwaitingReviewCount,
    int UncertainReviewedCount);
