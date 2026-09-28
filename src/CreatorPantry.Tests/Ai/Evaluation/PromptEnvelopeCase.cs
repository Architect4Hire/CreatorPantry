using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="PromptEnvelopeBuilder"/> and checks where untrusted content landed.
/// </summary>
/// <remarks>
/// <para>
/// Covers PromptInjection — and only the structural half of it. This proves untrusted bytes arrive inside
/// their own fenced segment of the user message and nowhere in the system message; it cannot and does not
/// claim to prove a model declines an instruction it finds there, which is behaviour no fixture against a
/// fake provider can demonstrate — see <c>PromptEnvelope.cs</c>'s own remarks.
/// </para>
/// <para>
/// A fixture names the segment its content travels in. <c>untrustedText</c> is the default and what every
/// fixture written before AIREC-002 used; <c>preferences</c> exists because that is where a rendered brief
/// goes — and, since AIREC-002, a selected concept, which is a previous generation's own words re-entering a
/// prompt. <c>source</c> exists for AIREC-006, whose only creator-supplied content is the recipe snapshot
/// itself — a headnote or a step can carry an injection as easily as a typed reason can. All are content
/// rather than instruction, and the point of naming the segment is that the same containment claim has to
/// hold for whichever one a capability actually uses.
/// </para>
/// </remarks>
internal sealed class PromptEnvelopeCase : IAiEvaluationCase
{
    public AiEvaluationKind Kind => AiEvaluationKind.PromptEnvelope;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var builder = new PromptEnvelopeBuilder(input.WorkspaceId)
            .WithTask(input.Task)
            .WithOutputSchema(input.OutputSchema);

        var envelope = (input.Segment ?? UntrustedTextSegment) switch
        {
            UntrustedTextSegment => builder.WithUntrustedText(input.WorkspaceId, input.UntrustedText).Build(),
            PreferencesSegment => builder.WithPreferences(input.WorkspaceId, input.UntrustedText).Build(),
            SourceSegment => builder.WithSource(input.WorkspaceId, input.UntrustedText).Build(),
            var unknown => throw new AiEvaluationException(
                fixture.Identity,
                $"'{unknown}' is not a segment this case can build. "
                    + $"Known: {UntrustedTextSegment}, {PreferencesSegment}, {SourceSegment}."),
        };

        var inUser = envelope.UserMessage.Contains(input.UntrustedText, StringComparison.Ordinal);
        var inSystem = envelope.SystemMessage.Contains(input.UntrustedText, StringComparison.Ordinal);

        if (inUser != expect.UntrustedTextInUserMessage)
        {
            return Task.FromResult(AiEvaluationVerdict.Fail(
                $"expected untrusted text present in the user message: {expect.UntrustedTextInUserMessage}, was {inUser}."));
        }

        if (inSystem != expect.UntrustedTextInSystemMessage)
        {
            return Task.FromResult(AiEvaluationVerdict.Fail(
                $"expected untrusted text present in the system message: {expect.UntrustedTextInSystemMessage}, was {inSystem}."));
        }

        return Task.FromResult(AiEvaluationVerdict.Pass());
    }

    private const string UntrustedTextSegment = "untrustedText";

    private const string PreferencesSegment = "preferences";

    private const string SourceSegment = "source";

    /// <param name="UntrustedText">
    /// The content to place, whichever segment <paramref name="Segment"/> names. Still called this because the
    /// claim is the same either way: wherever it goes, it is content and it must not reach the system message.
    /// </param>
    /// <param name="Segment">Defaults to <c>untrustedText</c>, so fixtures written before this stayed valid.</param>
    private sealed record Input(
        Guid WorkspaceId, string Task, string OutputSchema, string UntrustedText, string? Segment = null);

    private sealed record Expect(bool UntrustedTextInUserMessage, bool UntrustedTextInSystemMessage);
}
