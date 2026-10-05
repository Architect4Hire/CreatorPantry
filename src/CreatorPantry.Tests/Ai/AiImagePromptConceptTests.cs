using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The seam between IMG-001 and IMG-002: a concept is stored as proposal rows and read back out of them.
/// </summary>
/// <remarks>
/// <strong>This is the drift tripwire for the pair.</strong> IMG-001's handler writes rows field-named by
/// <see cref="PhotographyConceptFields"/>, and IMG-002's reader looks those exact names up. Nothing else
/// connects the two — no shared document, no foreign key — so a rename on either side is only caught here.
/// </remarks>
public sealed class AiImagePromptConceptTests
{
    private static readonly Guid Concept = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_stored_concept_reads_back_with_the_requested_shot()
    {
        var concept = AiImagePromptConcept.From(Rows(), Concept, AiPhotographyShotKind.Hero);

        Assert.NotNull(concept);
        Assert.Equal("Morning window light", concept.Label);
        Assert.Equal("Unhurried", concept.Mood);
        Assert.Equal("Warm neutrals", concept.Palette);
        Assert.Equal("Overhead suits a flat loaf", concept.Rationale);
        Assert.Equal("Reads small", concept.ChannelFit);
        Assert.Equal(AiPhotographyShotKind.Hero, concept.ShotKind);
        Assert.Equal("Overhead, square crop", concept.Framing);
        Assert.Equal("Soft daylight", concept.Lighting);
        Assert.Equal("Pale oak", concept.Surface);
        Assert.Equal("A torn edge", concept.Styling);
        Assert.Equal(["Linen napkin", "Ceramic bowl"], concept.Props);
    }

    /// <summary>
    /// Only the requested shot's rows are taken, so the prompt describes one frame rather than three.
    /// </summary>
    [Fact]
    public void A_concept_with_several_shots_yields_only_the_one_asked_for()
    {
        var detail = AiImagePromptConcept.From(Rows(), Concept, AiPhotographyShotKind.DetailShot);

        Assert.NotNull(detail);
        Assert.Equal(AiPhotographyShotKind.DetailShot, detail.ShotKind);
        Assert.Equal("Close on the crumb", detail.Framing);

        // The hero's framing is in the same row set and must not leak into this shot.
        Assert.DoesNotContain("square crop", detail.Framing, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(detail.Props);
    }

    /// <summary>
    /// A concept id the proposal does not hold, and a shot the concept did not plan, are one answer.
    /// </summary>
    /// <remarks>
    /// Both mean the client named something it was not shown, and the request route reports them with a single
    /// code for that reason — distinguishing them would say which of a neighbour's things exist.
    /// </remarks>
    [Fact]
    public void An_unknown_concept_or_an_unplanned_shot_is_null()
    {
        Assert.Null(AiImagePromptConcept.From(Rows(), Other, AiPhotographyShotKind.Hero));
        Assert.Null(AiImagePromptConcept.From(Rows(), Concept, AiPhotographyShotKind.ProcessStep));
        Assert.Null(AiImagePromptConcept.From([], Concept, AiPhotographyShotKind.Hero));
    }

    /// <summary>
    /// Rows of another target kind are not a concept, however they are field-named.
    /// </summary>
    /// <remarks>
    /// A proposal holds rows of exactly one kind today, but the filter is on the kind as well as the id so a
    /// later proposal that mixed them could not have a brand sample read as a photography concept.
    /// </remarks>
    [Fact]
    public void Rows_of_another_target_kind_are_not_a_concept()
    {
        var foreign = Rows()
            .Select(row => (AiChangeTargetKind.BrandStyleSampleWithGuide, row.TargetId, row.FieldName, row.AfterValue));

        Assert.Null(AiImagePromptConcept.From(foreign, Concept, AiPhotographyShotKind.Hero));
    }

    /// <summary>
    /// A concept missing one of the look fields is not composable, even with its shot intact.
    /// </summary>
    [Fact]
    public void A_concept_missing_part_of_its_look_is_null()
    {
        var withoutPalette = Rows()
            .Where(row => row.FieldName != PhotographyConceptFields.Palette);

        Assert.Null(AiImagePromptConcept.From(withoutPalette, Concept, AiPhotographyShotKind.Hero));
    }

    /// <summary>
    /// The rows exactly as <c>PhotographyConceptAiTaskHandler.Translate</c> writes them.
    /// </summary>
    private static List<(AiChangeTargetKind TargetKind, Guid? TargetId, string? FieldName, string? AfterValue)> Rows() =>
    [
        (AiChangeTargetKind.PhotographyConcept, Concept, null, "Morning window light"),
        (AiChangeTargetKind.PhotographyConcept, Concept, PhotographyConceptFields.Mood, "Unhurried"),
        (AiChangeTargetKind.PhotographyConcept, Concept, PhotographyConceptFields.Palette, "Warm neutrals"),
        (AiChangeTargetKind.PhotographyConcept, Concept, PhotographyConceptFields.Rationale,
            "Overhead suits a flat loaf"),
        (AiChangeTargetKind.PhotographyConcept, Concept, PhotographyConceptFields.ChannelFit, "Reads small"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.Hero, "framing"), "Overhead, square crop"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.Hero, "lighting"), "Soft daylight"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.Hero, "surface"), "Pale oak"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.Hero, "styling"), "A torn edge"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.Hero, "props"), "Linen napkin; Ceramic bowl"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.DetailShot, "framing"), "Close on the crumb"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.DetailShot, "lighting"), "Raking light"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.DetailShot, "surface"), "The same board"),
        (AiChangeTargetKind.PhotographyConcept, Concept,
            PhotographyConceptFields.Shot(AiPhotographyShotKind.DetailShot, "styling"), "A slice tipped"),
    ];
}
