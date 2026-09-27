using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// The defensive branches a fixture author's own mistake should hit: an unrecognized <c>expect.outcome</c> or
/// <c>input.scenario</c> value. No real fixture exercises these paths, so they get their own direct coverage
/// rather than staying untested dead code.
/// </summary>
public sealed class AiEvaluationCaseRobustnessTests
{
    [Fact]
    public async Task An_unrecognized_output_validation_outcome_is_refused()
    {
        var fixture = Fixture(
            AiEvaluationKind.OutputValidation,
            input: """{ "payload": "{}", "expectedSchemaVersion": "fixture.v1", "scope": "WholeRecipe" }""",
            expect: """{ "outcome": "SomethingElse" }""");

        var failure = await Assert.ThrowsAsync<AiEvaluationException>(
            () => new OutputValidationCase().RunAsync(fixture, TestContext.Current.CancellationToken));

        Assert.Contains("SomethingElse", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrecognized_worker_operation_scenario_is_refused()
    {
        var fixture = Fixture(
            AiEvaluationKind.WorkerOperation,
            input: """{ "scenario": "no-such-scenario" }""",
            expect: """{ "outcome": "Proposed" }""");

        var failure = await Assert.ThrowsAsync<AiEvaluationException>(
            () => new WorkerOperationCase().RunAsync(fixture, TestContext.Current.CancellationToken));

        Assert.Contains("no-such-scenario", failure.Message, StringComparison.Ordinal);
    }

    private static AiEvaluationFixture Fixture(AiEvaluationKind kind, string input, string expect) => new(
        "fixture.robustness",
        new PromptTemplateVersion(1, 0, 0),
        AiEvaluationCategory.SchemaValidity,
        kind,
        "Exercises a case runner's own defensive branch.",
        JsonDocument.Parse(input).RootElement,
        JsonDocument.Parse(expect).RootElement);
}
