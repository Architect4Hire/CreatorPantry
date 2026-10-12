namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Where a long-form draft stands (baseline B-33). Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// <code>
/// Draft → Outlined → Writing → Assembled → Accepted
///                                       └→ NeedsReview (a pinned source moved)
/// </code>
/// Not a publication state: nothing here says the article went anywhere.
/// </remarks>
public enum LongFormDraftStatus
{
    /// <summary>Started; no section plan has been approved.</summary>
    Draft = 0,

    /// <summary>The creator approved a section plan. No prose is required to exist yet.</summary>
    Outlined = 1,

    /// <summary>Sections are being written, one generation or edit at a time.</summary>
    Writing = 2,

    /// <summary>Every planned section has a body; the document awaits the creator's decision as a whole.</summary>
    Assembled = 3,

    /// <summary>The creator accepted a revision as the article of record, and its pins still match the sources.</summary>
    Accepted = 4,

    /// <summary>
    /// The accepted revision was pinned to sources that have since changed. Its content is untouched and is
    /// not claimed to be current.
    /// </summary>
    NeedsReview = 5,
}
