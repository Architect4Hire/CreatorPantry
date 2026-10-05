namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract IMG-001's answer must satisfy: photography concepts for a subject, each planned as a short
/// shot list. This type <em>is</em> the schema — <see cref="AiPhotographyConceptOutputSchema"/> exports it for
/// the prompt — and the answer is held to it by strict deserialization, as every other capability's is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no field for a finished image prompt, and that is IMG-001's restriction made
/// structural.</strong> Composing the final, editable prompt is IMG-002's task, from a concept the creator
/// approved. A concept that carried a ready-to-send prompt string would make this capability the prompt
/// composer by the back door, and a model that tried to add one — <c>"prompt": "..."</c> — fails strict shape
/// validation as an unknown field rather than being quietly stored.
/// </para>
/// <para>
/// <strong>No field here can hold a recipe fact.</strong> There is nowhere to put a quantity, a time, a
/// temperature, a yield or a serving count, and <see cref="AiPhotographyConceptOutputValidator"/> refuses one
/// smuggled into prose. A photograph is planned from how the dish should look, and nothing in this request
/// tells the model what any of those figures are, so any figure in an answer would be invented (ai.md).
/// </para>
/// <para>
/// <strong>Nothing is persisted verbatim as a unit.</strong> <c>PhotographyConceptAiTaskHandler</c> translates
/// a validated document into <c>AiStructuredChange</c> rows against a server-minted id per concept — the same
/// storage every capability uses — never <see cref="AiDiffCalculator"/>, which has no pinned recipe to resolve
/// a photography concept against.
/// </para>
/// </remarks>
public sealed record AiPhotographyConceptOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// One to three distinct concepts. See <see cref="AiPolicy.MinPhotographyConceptCount"/>.
    /// </summary>
    public required IReadOnlyList<AiPhotographyConcept> Concepts { get; init; }

    /// <summary>What the creator should be told alongside the concepts, including assumptions.</summary>
    public IReadOnlyList<AiPhotographyConceptOutputWarning> Warnings { get; init; } = [];
}

/// <summary>
/// One photography concept: a look, and the short list of shots that realise it.
/// </summary>
/// <remarks>
/// The concept-level fields are the ones a whole shoot shares — the palette, the mood, why this look suits the
/// subject — and the per-shot fields are what changes between frames. The split is what lets IMG-002 compose a
/// prompt for one named shot while still carrying the look it belongs to.
/// </remarks>
public sealed record AiPhotographyConcept
{
    /// <summary>A short name a creator can recognise the concept by, in a list of three.</summary>
    public required string Label { get; init; }

    /// <summary>The feeling the set should carry. Descriptive, never a claim about the food.</summary>
    public required string Mood { get; init; }

    /// <summary>The colour story, as descriptive words rather than codes or brand names.</summary>
    public required string Palette { get; init; }

    /// <summary>Why this look suits this subject, so a creator can tell three concepts apart.</summary>
    public required string Rationale { get; init; }

    /// <summary>
    /// How the set serves the channel it was planned for — orientation, crop, whether it reads at thumbnail
    /// size. Null when no channel was named, and never an invented engagement or reach figure.
    /// </summary>
    public string? ChannelFit { get; init; }

    /// <summary>
    /// One to three shots, exactly one of them the <see cref="AiPhotographyShotKind.Hero"/>.
    /// </summary>
    public required IReadOnlyList<AiPhotographyShot> Shots { get; init; }
}

/// <summary>One frame to shoot: what is in it, how it is lit, and what it sits on.</summary>
public sealed record AiPhotographyShot
{
    /// <summary>What the frame is for. Distinct within a concept, and exactly one Hero per concept.</summary>
    public required AiPhotographyShotKind Kind { get; init; }

    /// <summary>Angle, crop and arrangement in frame.</summary>
    public required string Framing { get; init; }

    /// <summary>Direction, quality and time of day of the light.</summary>
    public required string Lighting { get; init; }

    /// <summary>What the subject sits on, and what is behind it.</summary>
    public required string Surface { get; init; }

    /// <summary>
    /// How the food is arranged and presented for the camera.
    /// </summary>
    /// <remarks>
    /// Presentation only. It may not introduce an ingredient the subject does not have, and it may not assert a
    /// doneness, temperature or texture as a fact about a dish nothing here has seen — a garnish nobody wrote
    /// down is a recipe change made by photograph (recipes.md).
    /// </remarks>
    public required string Styling { get; init; }

    /// <summary>
    /// Props in the frame, as short phrases. No brand, logo, trademark or person.
    /// </summary>
    public IReadOnlyList<string> Props { get; init; } = [];
}

/// <summary>One thing to tell the creator about the answer as a whole, or about one concept.</summary>
public sealed record AiPhotographyConceptOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// The concept this is about, as an index into <see cref="AiPhotographyConceptOutputDocument.Concepts"/>.
    /// Null for a warning about the answer as a whole.
    /// </summary>
    public int? ConceptIndex { get; init; }
}
