using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture straight through <see cref="AiDishFacetsOutputValidator.Validate"/> — no envelope, no
/// gateway, no database, and no vocabulary. Covers SchemaValidity and RefusalSafety for the dish-name
/// reading's own document, which is not shaped like a recipe diff and so cannot run through
/// <see cref="OutputValidationCase"/>.
/// </summary>
/// <remarks>
/// Whether a particular dish name <em>ought</em> to read as Levantine is not something this can judge, and no
/// fixture here claims otherwise. What it demonstrates is that an unqualified guess, a contradictory pair of
/// readings and an unexplained one are all refused — which is what stops the surface pre-filling a creator's
/// control from something the model never committed to.
/// </remarks>
internal sealed class DishFacetsOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.DishFacetsOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var result = AiDishFacetsOutputValidator.Validate(input.Payload, input.ExpectedSchemaVersion);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => Accepted(result, expect),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    /// <remarks>
    /// A fixture may also state how many facets it expects to survive, which is how "declining is a real
    /// answer" is demonstrated rather than described: an answer that reads none of the three validates, and
    /// the count says so.
    /// </remarks>
    private static AiEvaluationVerdict Accepted(
        AiOutputValidationOutcome<AiDishFacetsOutputDocument> result, Expect expect)
    {
        if (!result.Succeeded)
        {
            return AiEvaluationVerdict.Fail(
                $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                + $"({result.Failure.Message})");
        }

        if (expect.SuggestionCount is { } count && result.Document!.Suggestions.Count != count)
        {
            return AiEvaluationVerdict.Fail(
                $"expected {count} suggestions, got {result.Document!.Suggestions.Count}.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiDishFacetsOutputDocument> result, Expect expect)
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

    private sealed record Input(string? Payload, string ExpectedSchemaVersion);

    private sealed record Expect(
        string Outcome, string? ReasonCode, AiFailureCategory? Category, int? SuggestionCount);
}
