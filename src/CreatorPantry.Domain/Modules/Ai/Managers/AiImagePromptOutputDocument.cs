namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract IMG-002's answer must satisfy: one editable image prompt for one shot of one concept, and the
/// negative guidance that goes with it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the artefact meant to be sent to an image model, which makes it the opposite of
/// <see cref="AiPhotographyConceptOutputDocument"/> in one specific way</strong>: a concept may write no
/// numeral, because any figure in a shoot plan would be invented, while a prompt legitimately says "4:5" and
/// "three loaves". The numeral rule is deliberately absent here, and that is a decision rather than an
/// omission.
/// </para>
/// <para>
/// <strong>No rendering parameter, and nowhere to put one.</strong> No provider, model, seed, step count,
/// sampler, guidance scale, dimension or output path — IMG-002 composes text and renders nothing (its
/// RESTRICTION), and 12.7 is where a renderer learns its own settings. A model that returned one fails strict
/// shape validation as an unknown field.
/// </para>
/// <para>
/// <strong>There is no "composed from" block, and that is a reversal worth recording.</strong> The plan for
/// this capability had one — a small summary of whether the brand guide, the recipe and the brief had been
/// used. It is not here because the <em>model</em> would be the one asserting it, which is the unfalsifiable
/// "this followed your guidance" that <see cref="AiTaskType.BrandStyleTestDrive"/>'s own remarks refuse. What
/// reached the prompt is the server's fact: the brand package is recorded as the proposal's provenance by
/// <see cref="AiProposalAssembler"/>, and anything the server could not supply is a server-written warning.
/// </para>
/// <para>
/// <strong>The creator's edit is authoritative</strong>, which is why this carries a draft and not a decision:
/// what reaches the library is whatever they save through PRM-001, with this text kept beside it as
/// <c>GeneratedText</c> so the library can still answer what they changed.
/// </para>
/// </remarks>
public sealed record AiImagePromptOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// The prompt, as one block of text a creator edits and an image model reads.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// What the image model should avoid, as short phrases.
    /// </summary>
    /// <remarks>
    /// A separate list rather than a sentence inside <see cref="Prompt"/>, because image models take negative
    /// guidance as its own input — and because the workspace's visual guide carries negative direction the
    /// creator wrote, which would be unusable folded into prose.
    /// </remarks>
    public IReadOnlyList<string> Avoid { get; init; } = [];

    /// <summary>What the creator should know about this prompt, including anything that could not be honoured.</summary>
    public IReadOnlyList<AiImagePromptOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One thing to tell the creator about the composed prompt.</summary>
public sealed record AiImagePromptOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }
}
