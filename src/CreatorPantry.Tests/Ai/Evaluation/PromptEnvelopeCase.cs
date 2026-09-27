using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="PromptEnvelopeBuilder"/> and checks where untrusted content landed.
/// </summary>
/// <remarks>
/// Covers PromptInjection — and only the structural half of it. This proves untrusted bytes arrive inside
/// their own fenced segment of the user message and nowhere in the system message; it cannot and does not
/// claim to prove a model declines an instruction it finds there, which is behaviour no fixture against a
/// fake provider can demonstrate — see <c>PromptEnvelope.cs</c>'s own remarks.
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

        var envelope = new PromptEnvelopeBuilder(input.WorkspaceId)
            .WithTask(input.Task)
            .WithOutputSchema(input.OutputSchema)
            .WithUntrustedText(input.WorkspaceId, input.UntrustedText)
            .Build();

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

    private sealed record Input(Guid WorkspaceId, string Task, string OutputSchema, string UntrustedText);

    private sealed record Expect(bool UntrustedTextInUserMessage, bool UntrustedTextInSystemMessage);
}
