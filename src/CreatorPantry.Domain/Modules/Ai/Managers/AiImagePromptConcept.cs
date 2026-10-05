using System.Globalization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// One photography concept read back out of the rows IMG-001 stored, for the one shot IMG-002 is composing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reconstructed from <c>AiStructuredChange</c> rows rather than from a stored document</strong>,
/// because that is where a concept lives: IMG-001 translated its answer into one <c>Add</c> row carrying the
/// label and <c>Set</c> rows for everything else, field-named by <see cref="PhotographyConceptFields"/>. This
/// reads exactly those names back, so the two halves of the pair cannot drift without a test failing.
/// </para>
/// <para>
/// Only the requested shot's rows are taken. A concept may plan three frames and IMG-002 composes for one, so
/// carrying the others into the prompt would describe pictures the creator did not ask for.
/// </para>
/// </remarks>
public sealed record AiImagePromptConcept(
    string Label,
    string Mood,
    string Palette,
    string Rationale,
    string? ChannelFit,
    AiPhotographyShotKind ShotKind,
    string Framing,
    string Lighting,
    string Surface,
    string Styling,
    IReadOnlyList<string> Props)
{
    /// <summary>
    /// Reads one concept and one of its shots out of a proposal's rows, or null when the rows do not describe
    /// that pair.
    /// </summary>
    /// <param name="changes">Every row of the proposal, as stored.</param>
    /// <param name="conceptId">The concept's server-minted target id.</param>
    /// <param name="shotKind">Which shot of it to compose for.</param>
    /// <remarks>
    /// Null for a concept id this proposal does not hold and for a shot that concept did not plan — one answer
    /// for both, because a client naming either was naming something it was not shown.
    /// </remarks>
    public static AiImagePromptConcept? From(
        IEnumerable<(AiChangeTargetKind TargetKind, Guid? TargetId, string? FieldName, string? AfterValue)> changes,
        Guid conceptId,
        AiPhotographyShotKind shotKind)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var rows = changes
            .Where(change => change.TargetKind is AiChangeTargetKind.PhotographyConcept
                && change.TargetId == conceptId)
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        var label = rows.FirstOrDefault(row => row.FieldName is null).AfterValue;

        string? Field(string name) =>
            rows.FirstOrDefault(row => row.FieldName == name).AfterValue;

        var framing = Field(PhotographyConceptFields.Shot(shotKind, "framing"));
        var lighting = Field(PhotographyConceptFields.Shot(shotKind, "lighting"));
        var surface = Field(PhotographyConceptFields.Shot(shotKind, "surface"));
        var styling = Field(PhotographyConceptFields.Shot(shotKind, "styling"));

        // Every one of the four is written for every shot IMG-001 stored, so a missing one means this concept
        // did not plan this shot. Props may legitimately be absent.
        if (label is null || framing is null || lighting is null || surface is null || styling is null)
        {
            return null;
        }

        var mood = Field(PhotographyConceptFields.Mood);
        var palette = Field(PhotographyConceptFields.Palette);
        var rationale = Field(PhotographyConceptFields.Rationale);

        if (mood is null || palette is null || rationale is null)
        {
            return null;
        }

        var props = Field(PhotographyConceptFields.Shot(shotKind, "props")) is { } joined
            ? joined.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new AiImagePromptConcept(
            label,
            mood,
            palette,
            rationale,
            Field(PhotographyConceptFields.ChannelFit),
            shotKind,
            framing,
            lighting,
            surface,
            styling,
            props);
    }

    /// <summary>The shot's role as the prompt's source segment names it.</summary>
    public string ShotKindName => ShotKind.ToString();

    /// <summary>How many props the shot lists, for a diagnostic that must not quote their text.</summary>
    public string PropCount => Props.Count.ToString(CultureInfo.InvariantCulture);
}
