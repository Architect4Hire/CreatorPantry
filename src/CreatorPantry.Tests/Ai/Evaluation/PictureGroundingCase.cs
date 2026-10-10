using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through the creative-context package and <see cref="CreativeContextPromptRenderer"/>: what a
/// task is told about a picture, and what it is not (AF.6.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a kind of its own.</strong> The rule AF.6.6 has to hold is about <em>grounding</em> rather than
/// about an answer: a picture nobody has read contributes one fixed sentence, and a picture a model has read
/// contributes its observations with their confidences and never as the creator's words. No validator can
/// check that — by the time a validator sees a post, what the model was told is gone — so the fixture has to
/// be about the prompt the server builds.
/// </para>
/// <para>
/// <strong>And it is not circular.</strong> A fixture supplies a picture's id, its kind and its version, which
/// the package really carries; the assertion is that none of them reaches the prompt while the fixed sentence
/// does. The values excluded are values the renderer is holding.
/// </para>
/// </remarks>
internal sealed class PictureGroundingCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Guid Workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public AiEvaluationKind Kind => AiEvaluationKind.PictureGrounding;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        if (input.Pictures is null or { Count: 0 })
        {
            throw new AiEvaluationException(fixture.Identity, "'input.pictures' must name at least one picture.");
        }

        var pictures = input.Pictures.Select(Picture(fixture)).ToList();
        var package = Package(input, pictures);
        var rendered = CreativeContextPromptRenderer.Sources(package)
            ?? throw new AiEvaluationException(fixture.Identity, "the package rendered to nothing.");

        foreach (var wanted in expect.Contains ?? [])
        {
            if (!rendered.Contains(wanted, StringComparison.Ordinal))
            {
                return Task.FromResult(AiEvaluationVerdict.Fail($"the prompt does not carry '{wanted}'."));
            }
        }

        foreach (var forbidden in expect.Excludes ?? [])
        {
            if (rendered.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AiEvaluationVerdict.Fail($"the prompt carries '{forbidden}'."));
            }
        }

        // Every picture's own id, kind and version, checked for whether a fixture asked or not: each is a
        // value the renderer is holding, and none of them is a fact about the pixels.
        foreach (var picture in pictures)
        {
            foreach (var identifier in new[] { picture.PictureId.ToString("D"), picture.PictureId.ToString("N") })
            {
                if (rendered.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(AiEvaluationVerdict.Fail($"the prompt names a picture's id."));
                }
            }
        }

        return Task.FromResult(AiEvaluationVerdict.Pass());
    }

    /// <summary>One picture as the package would carry it, built from what the fixture says describes it.</summary>
    private static Func<PictureInput, CreativeContextPictureEntry> Picture(AiEvaluationFixture fixture) =>
        picture =>
        {
            var id = picture.PictureId ?? Guid.Parse("22222222-2222-2222-2222-222222222222");
            var kind = picture.Kind ?? CreativeContextReferenceKind.GeneratedImage;

            return picture.DescribedBy switch
            {
                "creator" => new CreativeContextPictureEntry(
                    Guid.NewGuid(),
                    kind,
                    id,
                    picture.VersionNumber,
                    CreativeContextPictureDescriptionSource.CreatorAltText,
                    picture.Description
                        ?? throw new AiEvaluationException(
                            fixture.Identity, "a picture described by the creator needs a 'description'.")),

                "model" => new CreativeContextPictureEntry(
                    Guid.NewGuid(),
                    kind,
                    id,
                    picture.VersionNumber,
                    CreativeContextPictureDescriptionSource.StoredAnalysis,
                    Description: null,
                    new CreativeContextPictureReading(
                        (picture.Observations ?? throw new AiEvaluationException(
                            fixture.Identity, "a picture read by a model needs 'observations'."))
                        .Select(each => new CreativeContextPictureObservation(each.Aspect, each.Text, each.Confidence))
                        .ToList(),
                        DateTimeOffset.UnixEpoch)),

                "nobody" => new CreativeContextPictureEntry(
                    Guid.NewGuid(),
                    kind,
                    id,
                    picture.VersionNumber,
                    CreativeContextPictureDescriptionSource.NotDescribed,
                    Description: null),

                var other => throw new AiEvaluationException(
                    fixture.Identity, $"'describedBy' must be 'creator', 'model' or 'nobody'; got '{other}'."),
            };
        };

    private static CreativeContextPackage Package(Input input, IReadOnlyList<CreativeContextPictureEntry> pictures) =>
        new(
            Workspace,
            AiTaskType.ChannelPosts,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ContextVersion: null,
            input.WorkingTitle is null ? null : new CreativeContextWords(input.WorkingTitle, null),
            Channels: [],
            Day: null,
            Recipes: [],
            Concepts: [],
            pictures,
            Prompts: [],
            Dropped: [],
            Omissions: [],
            EstimatedTokens: 0,
            Checksum: "sha256:fixture",
            DateTimeOffset.UnixEpoch);

    private sealed record Input(
        string? WorkingTitle,
        IReadOnlyList<PictureInput>? Pictures);

    /// <param name="DescribedBy">`creator`, `model` or `nobody` — the three ways a package carries a picture.</param>
    private sealed record PictureInput(
        string DescribedBy,
        Guid? PictureId,
        CreativeContextReferenceKind? Kind,
        int? VersionNumber,
        string? Description,
        IReadOnlyList<ObservationInput>? Observations);

    private sealed record ObservationInput(string Aspect, string Text, string Confidence);

    /// <param name="Contains">Strings the rendered prompt must carry.</param>
    /// <param name="Excludes">Strings it must not, matched case-insensitively.</param>
    private sealed record Expect(
        IReadOnlyList<string>? Contains,
        IReadOnlyList<string>? Excludes);
}
