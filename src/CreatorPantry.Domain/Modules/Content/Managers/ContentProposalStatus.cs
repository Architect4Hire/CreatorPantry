namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Where a content proposal stands in the creator's review. Editorial state only: it says nothing about
/// delivery to any provider, which belongs to <c>Publication</c> records (content.md).
/// </summary>
/// <remarks>
/// <see cref="ContentProposalTransitions"/> owns which moves exist. Stored as its number; append, never
/// renumber.
/// </remarks>
public enum ContentProposalStatus
{
    /// <summary>The latest revision awaits the creator's decision. Nothing may treat it as accepted.</summary>
    Proposed = 0,

    /// <summary>The creator accepted a revision, and its pins still match the sources.</summary>
    Accepted = 1,

    /// <summary>The creator declined the latest revision. Earlier accepted content, if any, is retained.</summary>
    Rejected = 2,

    /// <summary>
    /// The accepted revision was pinned to sources that have since changed. Its content is untouched and is
    /// not claimed to be current.
    /// </summary>
    NeedsReview = 3,
}
