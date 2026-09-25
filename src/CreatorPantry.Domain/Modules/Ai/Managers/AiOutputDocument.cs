namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract a model's answer must satisfy. This type <em>is</em> the schema: the JSON Schema the model is
/// shown is exported from it by <see cref="AiOutputSchema"/>, and the answer is held to it by strict
/// deserialization — so what the model is asked for and what it is judged against cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no before value anywhere in this document, and that is the point.</strong> A before value
/// is an assertion about content the server already holds; accepting one would let a forged or merely stale
/// claim decide what a diff appears to change. The server computes every before value from the pinned source
/// version, and a model that wanted to supply one has nowhere to put it.
/// </para>
/// <para>
/// Nothing here is persisted verbatim as a unit. The validated document is translated into
/// <c>AiStructuredChange</c> and <c>AiWarning</c> rows, and every field below has somewhere to land — a
/// property with no column behind it would be a contract the system cannot keep.
/// </para>
/// </remarks>
public sealed record AiOutputDocument
{
    /// <summary>
    /// The schema this answer claims to follow. Compared to the template's declared output schema version
    /// before anything else about the body is examined.
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// What the model proposes. May be empty: "I looked and there is nothing to change" is a real answer, and
    /// reporting it as a failure would tell the creator something untrue.
    /// </summary>
    public IReadOnlyList<AiOutputChange> Changes { get; init; } = [];

    /// <summary>What the creator should be told alongside the changes, including assumptions.</summary>
    public IReadOnlyList<AiOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One proposed change, addressed structurally rather than by a path string.</summary>
public sealed record AiOutputChange
{
    public required AiChangeKind ChangeKind { get; init; }

    public required AiChangeTargetKind TargetKind { get; init; }

    /// <summary>
    /// The row being changed, or the parent a row is added to. Null when the target is the recipe's own
    /// fields, or when a top-level child is being added.
    /// </summary>
    public Guid? TargetId { get; init; }

    /// <summary>The field being set. Required for a set, and meaningless for every other kind.</summary>
    public string? FieldName { get; init; }

    /// <summary>The value proposed. Null for a removal, and for a move that does not reword anything.</summary>
    public string? AfterValue { get; init; }

    /// <summary>Where an addition or a move puts the child, zero-based within its parent.</summary>
    public int? ProposedPosition { get; init; }
}

/// <summary>One thing to tell the creator: an assumption the model made, or a caution about the result.</summary>
public sealed record AiOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// The change this is about, as an index into <see cref="AiOutputDocument.Changes"/>. Null for a warning
    /// about the answer as a whole.
    /// </summary>
    /// <remarks>
    /// An index rather than an id, because the changes do not have ids until the server writes them. The
    /// validator checks that every index points at a change that exists.
    /// </remarks>
    public int? ChangeIndex { get; init; }
}
