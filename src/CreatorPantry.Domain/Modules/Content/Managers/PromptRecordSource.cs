namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// How a saved prompt came to exist. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// Every value but <see cref="Manual"/> names an AI proposal, so <c>PromptRecord.AiProposalId</c> is required for
/// those and forbidden for that one — a check constraint holds it, so provenance cannot disagree with the source.
/// The three AI values are distinguished rather than collapsed into one "AiGenerated" because they are different
/// tasks with different inputs and different evaluation sets (12.4, 12.4a, 12.5), and a library that could not
/// tell "composed from a concept" from "read off a reference photograph" would lose the one fact a creator wants
/// when deciding whether to reuse a prompt.
/// </remarks>
public enum PromptRecordSource
{
    /// <summary>The creator wrote the prompt themselves. No proposal, no template, no generated draft.</summary>
    Manual = 0,

    /// <summary>A photography concept (IMG-001) the creator approved and then turned into a prompt.</summary>
    PhotographyConcept = 1,

    /// <summary>An image prompt composed from an approved concept and channel memory (IMG-002).</summary>
    ImagePromptComposition = 2,

    /// <summary>A prompt drawn from observations of a reference image the creator supplied (IMG-004).</summary>
    ReferenceImageAnalysis = 3,
}
