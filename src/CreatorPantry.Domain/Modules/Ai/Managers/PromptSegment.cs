namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>How much authority a segment's content carries.</summary>
public enum PromptSegmentTrust
{
    /// <summary>Written by this application. The only content a model may treat as instruction.</summary>
    Instruction = 1,

    /// <summary>
    /// The creator's own material — their recipe, their preferences. Trusted as to provenance, and still data:
    /// a creator can paste an instruction into a headnote as easily as anyone else, and an imported recipe may
    /// carry one deliberately.
    /// </summary>
    CreatorData = 2,

    /// <summary>Retrieved, imported, or user-supplied text. Data, and assumed hostile.</summary>
    Untrusted = 3,
}

/// <summary>
/// The kinds of segment an envelope can carry. The kind determines the trust level; a caller cannot choose it.
/// </summary>
/// <remarks>
/// That is the point of keeping them separate enums with a fixed mapping between them. If trust were a field
/// on a segment, the way to leak an injection into the instruction position would be one wrong argument at one
/// call site — and it would look entirely reasonable in review.
/// </remarks>
public enum PromptSegmentKind
{
    /// <summary>The rules, and the declaration of the fence token. Always present, always first.</summary>
    Policy = 1,

    /// <summary>The rendered prompt template body: task instructions only.</summary>
    Task = 2,

    /// <summary>
    /// The JSON Schema the answer must satisfy.
    /// </summary>
    /// <remarks>
    /// Not one of the six segments microprompt 8.7 lists. It is here because that prompt's own restriction says
    /// retrieved text must not be able to redefine the output schema, which requires the schema to be a
    /// distinguishable part of the envelope rather than a sentence buried in the policy prose.
    /// </remarks>
    OutputSchema = 3,

    /// <summary>The workspace's and creator's stated preferences — voice, audience, conventions.</summary>
    Preferences = 4,

    /// <summary>The canonical recipe snapshot the task is about, as the server serialized it.</summary>
    Source = 5,

    /// <summary>Retrieved reference material. Untrusted.</summary>
    References = 6,

    /// <summary>User-supplied or imported text. Untrusted.</summary>
    UntrustedText = 7,

    /// <summary>
    /// A closing restatement that everything above was data.
    /// </summary>
    /// <remarks>
    /// Also not one of the six. Instructions placed only at the start lose ground to recency on a long input,
    /// and a recipe snapshot plus retrieved references is a long input. This costs a few dozen tokens and is
    /// the cheapest mitigation available.
    /// </remarks>
    Reminder = 8,
}

/// <summary>One fenced block of an envelope.</summary>
/// <param name="Kind">Which segment this is; determines <see cref="Trust"/>.</param>
/// <param name="Content">The content, carried through byte-for-byte.</param>
/// <param name="WorkspaceId">
/// The workspace this content was read from. Required for every segment that is not
/// <see cref="PromptSegmentTrust.Instruction"/>, and checked against the envelope's workspace at build time.
/// </param>
public sealed record PromptSegment(PromptSegmentKind Kind, string Content, Guid? WorkspaceId = null)
{
    public PromptSegmentTrust Trust => PromptEnvelopePolicy.TrustOf(Kind);
}
