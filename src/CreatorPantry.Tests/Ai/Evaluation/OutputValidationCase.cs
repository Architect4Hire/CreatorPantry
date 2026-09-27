using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture straight through <see cref="AiOutputValidator.Validate"/> — no envelope, no gateway, no
/// database. Covers SchemaValidity, the domain/out-of-scope half of RefusalSafety, and DeterministicToolRouting
/// (the schema has no field a model could use to supply an arithmetic result, so naming one is an unknown
/// field, not a value to trust).
/// </summary>
internal sealed class OutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.OutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var result = AiOutputValidator.Validate(input.Payload, input.ExpectedSchemaVersion, input.Scope);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? AiEvaluationVerdict.Pass()
                : AiEvaluationVerdict.Fail(
                    $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(result, expect),

            _ => throw new AiEvaluationException(fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Rejected(AiOutputValidationResult result, Expect expect)
    {
        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the answer to be rejected, but it validated.");
        }

        if (expect.ReasonCode is { } reasonCode && result.Failure!.ReasonCode != reasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{reasonCode}', got '{result.Failure!.ReasonCode}' ({result.Failure.Message}).");
        }

        if (expect.Category is { } category && result.Failure!.Category != category)
        {
            return AiEvaluationVerdict.Fail(
                $"expected category '{category}', got '{result.Failure!.Category}'.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(string? Payload, string ExpectedSchemaVersion, AiOperationScope Scope);

    private sealed record Expect(string Outcome, string? ReasonCode, AiFailureCategory? Category);
}
