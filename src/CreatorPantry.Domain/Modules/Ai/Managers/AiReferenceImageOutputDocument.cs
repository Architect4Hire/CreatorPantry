namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract IMG-004's answer must satisfy: what was observed in a reference photograph the creator
/// supplied, each observation with its own confidence, and an editable prompt drawn from them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Observations and prompt, not one or the other.</strong> A prompt alone would be a black box — the
/// creator could not tell which parts of their reference it had actually read, and would have no way to
/// correct a misreading except by rewriting the prompt. The observations are what make the prompt
/// reviewable, which is why they are required rather than a nicety.
/// </para>
/// <para>
/// <strong>Every observation states how sure it is</strong> (<see cref="AiReferenceImageConfidence"/>), and
/// the shape is what enforces it: an answer that omits a confidence fails validation rather than arriving as
/// confident. This is IMG-004's "with uncertainty" made structural.
/// </para>
/// <para>
/// <strong>No identity, no ownership, no ingredient list, no verdict on the food.</strong> There is no field
/// for who is in the picture, whose brand is in frame, what the dish contained, or whether it is cooked
/// through — so a model has nowhere to put any of them, and strict shape validation refuses an invented
/// field. What a model can still do is write one into the prose, which
/// <see cref="AiReferenceImageOutputValidator"/> catches for three of the four; the fourth — an ingredient
/// inferred rather than seen — is the one no validator can judge, and is the template's instruction and the
/// evaluation set's question.
/// </para>
/// <para>
/// <strong>No rendering parameter</strong>, for the reason <see cref="AiImagePromptOutputDocument"/> gives:
/// this composes text, renders nothing, and a seed written into a creator's saved prompt is something they
/// would have to edit out.
/// </para>
/// </remarks>
public sealed record AiReferenceImageOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// What the photograph shows, at most one observation per aspect.
    /// </summary>
    /// <remarks>
    /// One per aspect because the stored rows are keyed by it (<c>observation.{Aspect}</c>), so two readings
    /// of the lighting would collide. The validator refuses a duplicate rather than letting the second
    /// silently replace the first.
    /// </remarks>
    public IReadOnlyList<AiReferenceImageObservation> Observations { get; init; } = [];

    /// <summary>
    /// A prompt that would photograph something like the reference, as one block of editable text.
    /// </summary>
    /// <remarks>
    /// Held to the same rules as IMG-002's prompt, including the ban on rendering directives: it is the same
    /// kind of artefact, saved to the same library through the same route, and a creator editing it should
    /// not have to tell the two apart.
    /// </remarks>
    public required string Prompt { get; init; }

    /// <inheritdoc cref="AiImagePromptOutputDocument.Avoid"/>
    public IReadOnlyList<string> Avoid { get; init; } = [];

    /// <summary>What the creator should know about this reading, including what could not be determined.</summary>
    public IReadOnlyList<AiReferenceImageOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One reading of one property of the reference photograph.</summary>
public sealed record AiReferenceImageObservation
{
    /// <summary>Which property this is about. Must be declared, and at most once across the answer.</summary>
    public required AiReferenceImageAspect Aspect { get; init; }

    /// <summary>What the photograph shows of it, in the creator's reading language rather than a model's.</summary>
    public required string Text { get; init; }

    /// <summary>How sure this reading is. Required; see <see cref="AiReferenceImageConfidence"/>.</summary>
    public required AiReferenceImageConfidence Confidence { get; init; }
}

/// <summary>One thing to tell the creator about the reading or the prompt drawn from it.</summary>
public sealed record AiReferenceImageOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }
}
