namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which property of a reference photograph one observation is about (IMG-004).
/// </summary>
/// <remarks>
/// <para>
/// A closed set, because it is what the stored rows are keyed by: an observation is written to
/// <c>observation.{Aspect}</c>, so a free-text aspect would let a model choose its own column names and a
/// later reader would have nothing stable to look for.
/// </para>
/// <para>
/// <strong>Every member is something a photograph shows.</strong> There is deliberately no <c>Ingredients</c>
/// and no <c>Recipe</c>: a reading of a picture may say "a pale crumb with an open structure is visible" under
/// <see cref="Subject"/>, and may not say what was in it. That line is IMG-004's RESTRICTION — do not infer
/// unseen ingredients — and leaving out the aspect that would invite it is the structural half of enforcing
/// it. The other half is the template's instruction and the evaluation set, because no validator can know
/// whether a named ingredient was visible.
/// </para>
/// </remarks>
public enum AiReferenceImageAspect
{
    /// <summary>
    /// Not declared. Never valid in an answer — the validator refuses it.
    /// </summary>
    /// <remarks>
    /// Zero is a real value to a deserializer, so without this member an answer that omitted the aspect would
    /// silently become whichever one happened to be first.
    /// </remarks>
    Unspecified = 0,

    /// <summary>How the frame is arranged: what sits where, and how the eye moves through it.</summary>
    Composition = 1,

    /// <summary>Where the light comes from and what it does — direction, hardness, shadow.</summary>
    Lighting = 2,

    /// <summary>The colours present and how they relate.</summary>
    Colour = 3,

    /// <summary>The props, linens, cutlery and arrangement around the subject.</summary>
    Styling = 4,

    /// <summary>What the subject sits on, and the surfaces behind it.</summary>
    Surface = 5,

    /// <summary>The food itself as photographed: its form, texture and visible state.</summary>
    Subject = 6,

    /// <summary>How the picture feels, which is the one aspect the creator is likeliest to want matched.</summary>
    Mood = 7,
}
