namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>How a revision came to exist. Stored as its number; append, never renumber.</summary>
public enum ContentRevisionSource
{
    /// <summary>A model produced it through an AI proposal; <c>AiProposalId</c> names that proposal.</summary>
    AiGenerated = 0,

    /// <summary>The creator changed the content themselves.</summary>
    CreatorEdit = 1,

    /// <summary>
    /// The creator confirmed content still stands after its sources changed. Identical content to its
    /// parent, re-pinned to the current sources, so history records the decision without rewriting the old pins.
    /// </summary>
    Reaffirmed = 2,
}
