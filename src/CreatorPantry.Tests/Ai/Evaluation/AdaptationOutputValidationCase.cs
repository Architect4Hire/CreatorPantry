using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture straight through <see cref="AiAdaptationOutputValidator.Validate"/> — no envelope, no
/// gateway, no database. Covers SchemaValidity, RefusalSafety (a limitation in place of a fabricated success),
/// and DeterministicToolRouting for AIREC-005's yield goal, over the same recipe-diff document
/// <see cref="OutputValidationCase"/> already covers for AIREC-003.
/// </summary>
/// <remarks>
/// A yield fixture's deterministic figures are never hand-typed into the fixture as arithmetic: it declares a
/// multiplier and a small set of ingredient lines, and this runner resolves them through the real
/// <see cref="RecipeScalingCalculator"/>, the same calculator the handler calls — so a fixture cannot assert an
/// expected figure the production calculator would not itself produce.
/// </remarks>
internal sealed class AdaptationOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.AdaptationOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var deterministicScaling = Scale(input.ScalingMultiplier, input.ScalingLines);

        var result = AiAdaptationOutputValidator.Validate(
            input.Payload, input.ExpectedSchemaVersion, input.Scope, input.Goal, deterministicScaling);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? AiEvaluationVerdict.Pass()
                : AiEvaluationVerdict.Fail(
                    $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiOutputDocument> result, Expect expect)
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

    /// <summary>No scaling declared for a non-yield fixture; a real computed preview otherwise.</summary>
    private static RecipeScalingPreview? Scale(decimal? multiplier, IReadOnlyList<ScalingLineInput>? lines)
    {
        if (multiplier is null)
        {
            return null;
        }

        var inputs = (lines ?? []).Select(line => new RecipeIngredientScalingInput
        {
            Id = line.Id,
            DisplayText = line.DisplayText ?? "line",
            Quantity = line.Quantity,
            QuantityUpper = null,
            ScalingBehavior = line.Fixed ? IngredientScaling.Fixed : IngredientScaling.Proportional,
            MeasurementUnitDimension = null,
            DisplayPrecision = RecipeScalingPolicy.DefaultDisplayPrecision,
        }).ToArray();

        var outcome = RecipeScalingCalculator.Scale(
            RecipeScalingRequest.ForMultiplier(Quantity.FromDecimal(multiplier.Value)), null, inputs);

        return outcome.Succeeded
            ? outcome.Preview
            : throw new AiEvaluationException("adaptation-fixture-scaling", "the fixture's own scaling inputs do not resolve to a factor.");
    }

    private sealed record Input(
        string? Payload,
        string ExpectedSchemaVersion,
        AiOperationScope Scope,
        AiAdaptationGoal Goal,
        decimal? ScalingMultiplier,
        IReadOnlyList<ScalingLineInput>? ScalingLines);

    private sealed record ScalingLineInput(Guid Id, string? DisplayText, decimal? Quantity, bool Fixed);

    private sealed record Expect(string Outcome, string? ReasonCode, AiFailureCategory? Category);
}
