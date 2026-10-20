using CreatorPantry.Domain.Managers.Reference;
namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// Limits and rules for the shared dietary and allergen vocabularies and the ingredient traits that cite
/// evidence about them. Global reference data: no <c>WorkspaceId</c>, never duplicated per workspace
/// (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// The governing rule of this model, stated once here because it is easier to keep than to recover: missing
/// trait data means unknown, never safe. A trait row that does not exist carries no meaning at all, and there
/// is deliberately nothing in the schema — no boolean, no "free from" flag, no certified-safe column — that a
/// missing row could default into. Every question this data answers is therefore three-way at minimum:
/// evidence for, evidence against, or nothing known.
/// </para>
/// <para>
/// Nor is there a confidence scalar. Weight comes from the cited <see cref="ReferenceSourceKind"/>, the
/// <see cref="TraitReviewStatus"/>, and the trait's own state value — three things that each say what they
/// mean, rather than one number that would end up rendered as a percentage next to an allergen.
/// </para>
/// </remarks>
public static class TraitPolicy
{
    /// <summary>Stable lowercase machine key, e.g. <c>vegan</c>, <c>gluten-free</c>, <c>tree-nuts</c>.</summary>

    /// <inheritdoc cref="CodeFormat.CodePattern"/>


    /// <summary>
    /// Room for a plain-language statement of what a profile or allergen actually covers. Required on both
    /// vocabularies: a profile named only <c>paleo</c> leaves every reader to supply their own definition, and
    /// they will not all supply the same one.
    /// </summary>

    /// <summary>What the cited source said, in its own terms. See <see cref="NoEvidenceNote"/>.</summary>
    public const int EvidenceNoteMaxLength = 512;

    /// <summary>
    /// The <c>EvidenceNote</c> for a trait that needs none.
    /// </summary>
    /// <remarks>
    /// Empty string rather than null, matching <see cref="DensityPolicy.UnspecifiedCondition"/>: the
    /// note-required check constraints compare against this value, and <c>NULL</c> would make those comparisons
    /// evaluate to unknown and pass silently on both providers.
    /// </remarks>
    public const string NoEvidenceNote = "";

    /// <summary>
    /// Whether a trait in this state must carry an <c>EvidenceNote</c> saying what the source actually stated.
    /// </summary>
    /// <remarks>
    /// True for exactly the states a reader could mistake for safety guidance. A bare
    /// <see cref="AllergenPresence.PossiblePresence"/> reads as a vague warning unless it says whose warning it
    /// is, and a bare <see cref="AllergenPresence.NotListedBySource"/> reads as "free from" unless it says what
    /// was and was not examined. <see cref="AllergenPresence.Present"/> needs no such defence, and
    /// <see cref="AllergenPresence.Unknown"/> claims nothing.
    /// </remarks>
    public static bool RequiresEvidenceNote(AllergenPresence presence) =>
        presence is AllergenPresence.PossiblePresence or AllergenPresence.NotListedBySource;

    /// <inheritdoc cref="RequiresEvidenceNote(AllergenPresence)"/>
    /// <remarks>
    /// True for <see cref="DietaryCompatibility.DependsOnProduct"/> alone: a trait that says "it depends" is
    /// useless, and quietly alarming, unless it says what it depends on.
    /// </remarks>
    public static bool RequiresEvidenceNote(DietaryCompatibility compatibility) =>
        compatibility is DietaryCompatibility.DependsOnProduct;

    /// <summary>
    /// Whether a source of this kind may assert <see cref="AllergenPresence.NotListedBySource"/>.
    /// </summary>
    /// <remarks>
    /// Absence is the direction in which a wrong allergen claim does harm: a false <c>Present</c> costs a
    /// creator an ingredient, while a false absence reaches someone who is allergic. An unvetted contributor's
    /// recollection and a model's generated text are not grounds for it, so the database refuses to store one
    /// at all rather than trusting a review step that does not exist yet.
    /// </remarks>
    public static bool MayAssertAllergenAbsence(ReferenceSourceKind kind) =>
        kind is not (ReferenceSourceKind.CommunityContributed or ReferenceSourceKind.AiEstimated);

    /// <summary>
    /// Whether a trait citing this source may reach <see cref="TraitReviewStatus.Approved"/> — the only state
    /// an analysis acts on (14.7, H-7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this closes.</strong> The schema checks a trait's source *kind* and its review status, and
    /// a composite foreign key stops a row misreporting its kind. Nothing checked that the source was one the
    /// product has actually approved for this evidence kind. Today no write path creates an approved trait at
    /// all, so B-33's "no approved allergen or dietary source" held by absence rather than by a rule — and the
    /// first writer added would have inherited nothing.
    /// </para>
    /// <para>
    /// <strong>Why a policy rather than a check constraint.</strong> The approved list lives in
    /// <see cref="EvidenceSourceCatalog"/>, in code, because approving a source is a decision recorded in the
    /// baseline and not a row somebody can insert. A SQL constraint cannot read it, and copying the codes into
    /// a constraint would be a second list to keep in step. Any writer of an approved trait calls this, and
    /// <c>DietaryAllergenTraitConstraintTests</c> pins that the kind and status rules stay in the schema
    /// underneath it.
    /// </para>
    /// <para>
    /// A trait from an unapproved source may still be *stored* — an unreviewed note about what some source
    /// said is a fact about that source — and an analysis will not count it, because it is not approved.
    /// </para>
    /// </remarks>
    public static bool MayBeApproved(string? sourceCode, ReferenceEvidenceKind evidenceKind)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return false;
        }

        var approved = EvidenceSourceCatalog.For(evidenceKind);

        return approved is not null
            && string.Equals(approved.Code, sourceCode, StringComparison.OrdinalIgnoreCase);
    }
}
