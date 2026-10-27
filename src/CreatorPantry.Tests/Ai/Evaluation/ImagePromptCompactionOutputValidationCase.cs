using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture straight through <see cref="AiImagePromptCompactionOutputValidator.Validate"/> — no envelope, no
/// gateway, no database — at the budget the fixture names. Covers SchemaValidity and RefusalSafety for AP.3.2's
/// shorter prompt, whose shape and ceiling differ from IMG-002's on purpose.
/// </summary>
internal sealed class ImagePromptCompactionOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.ImagePromptCompactionOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        if (input.PromptBudget < AiPolicy.ImagePromptMinLength)
        {
            throw new AiEvaluationException(
                fixture.Identity, $"'input.promptBudget' must be at least {AiPolicy.ImagePromptMinLength}.");
        }

        var result = AiImagePromptCompactionOutputValidator.Validate(
            input.Payload, input.ExpectedSchemaVersion, input.PromptBudget);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? AiEvaluationVerdict.Pass()
                : AiEvaluationVerdict.Fail(
                    $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity,
        AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument> result,
        Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the answer to be rejected, but it validated.");
        }

        if (result.Failure!.ReasonCode != expect.ReasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' "
                + $"({result.Failure.Message}).");
        }

        if (expect.Category is { } category && result.Failure.Category != category)
        {
            return AiEvaluationVerdict.Fail(
                $"expected category '{category}', got '{result.Failure.Category}'.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(string? Payload, string ExpectedSchemaVersion, int PromptBudget);

    private sealed record Expect(string Outcome, string? ReasonCode, AiFailureCategory? Category);
}
