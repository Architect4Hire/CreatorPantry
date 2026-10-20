namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>
/// Where a workspace's mapping of one platform ingredient to a nutrition reference stands (14.2).
/// </summary>
/// <remarks>
/// Persisted, and named in check constraints by value, so members are never renumbered. Numbered from 10 so an
/// unset column is never mistaken for a state.
/// </remarks>
public enum NutritionMappingStatus
{
    /// <summary>Proposed automatically and not yet confirmed. Never used for an estimate.</summary>
    Candidate = 10,

    /// <summary>A creator confirmed the reference. The only state an estimate reads.</summary>
    Confirmed = 20,

    /// <summary>
    /// Was confirmed, and the source has since published a newer version of the record. The confirmed
    /// reference is kept, and so is the confirmation, until a creator confirms the new version or remaps.
    /// </summary>
    NeedsReview = 30,

    /// <summary>No reference: none was found, the creator said none fits, or the one confirmed went away.</summary>
    Unresolved = 40,
}

/// <summary>How the reference on a mapping was chosen.</summary>
public enum NutritionMatchMethod
{
    /// <summary>The first approved search hit for the platform ingredient's name that was not rejected.</summary>
    AutomaticSearch = 1,

    /// <summary>A creator picked the record — a remap, which overrides whatever was proposed.</summary>
    CreatorSelected = 2,
}

/// <summary>
/// How sure an automatic proposal is. Recorded, not scored: the codebase keeps no numeric confidence, because
/// how a match was made is the honest measure of it.
/// </summary>
/// <remarks>Either way, an automatic proposal is a <see cref="NutritionMappingStatus.Candidate"/> until confirmed.</remarks>
public enum NutritionMatchConfidence
{
    /// <summary>The top of several hits, with a name that is not the ingredient's.</summary>
    Low = 1,

    /// <summary>The only approved hit, or one whose name is the ingredient's own once normalized.</summary>
    High = 2,
}

/// <summary>Why a mapping has no reference.</summary>
public enum NutritionUnresolvedReason
{
    /// <summary>The source had nothing approved for the ingredient that had not been rejected.</summary>
    NoCandidates = 1,

    /// <summary>A creator said none of the references fits, or that this ingredient has no nutrition to count.</summary>
    CreatorDeclined = 2,

    /// <summary>A creator rejected the reference the mapping held. The next proposal skips it.</summary>
    ReferenceRejected = 3,

    /// <summary>The source no longer has the record, or it is no longer from an approved data set.</summary>
    ReferenceWithdrawn = 4,
}

/// <summary>Who said what a portion weighs.</summary>
public enum NutritionPortionOrigin
{
    /// <summary>One of the reference's own household portions, as the source recorded it.</summary>
    Reference = 1,

    /// <summary>The creator's own figure — this workspace's override, and never shared beyond it.</summary>
    Creator = 2,
}
