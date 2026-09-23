namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// How far a recorded dietary or allergen trait has got through review. Only <see cref="Approved"/> rows may
/// drive an analysis a creator sees; everything else is on record without being actionable.
/// </summary>
/// <remarks>
/// <para>
/// A state, not a ranking — compare with equality, never with <c>&gt;=</c>. These numeric values are persisted
/// and named literally by the <c>AiEstimate_NotApproved</c> check constraints on both trait tables, so they
/// must not be renumbered. Values leave gaps so a state can be inserted without renumbering.
/// </para>
/// <para>
/// Deliberately a separate enum from <see cref="DensityReviewStatus"/> despite sharing its members and
/// numbering today. A density is a measurement that is either right or wrong; a trait is a claim about what an
/// ingredient contains or is compatible with, whose review may well need states a measurement never does. Two
/// enums let that happen without touching the density constraint.
/// </para>
/// <para>
/// This status is <em>not</em> a confidence score, and there is no separate confidence column anywhere in this
/// model. How much weight a trait carries is read from three things that cannot be quietly overwritten: the
/// cited source's <see cref="ReferenceSourceKind"/>, this review state, and the trait's own
/// <see cref="AllergenPresence"/>/<see cref="DietaryCompatibility"/> value. A numeric confidence would invite
/// both a percentage in the interface and a threshold comparison in analysis code, and neither belongs
/// anywhere near an allergen (ai.md, recipes.md).
/// </para>
/// </remarks>
public enum TraitReviewStatus
{
    /// <summary>Recorded but not yet checked. The default for imported and contributed traits.</summary>
    Unreviewed = 0,

    /// <summary>Checked against its source and cleared for use.</summary>
    Approved = 10,

    /// <summary>Checked and found wrong or unusable. Kept so the same bad claim is not re-imported.</summary>
    Rejected = 20,

    /// <summary>Replaced by a later trait from the same source for the same ingredient.</summary>
    Superseded = 30,
}
