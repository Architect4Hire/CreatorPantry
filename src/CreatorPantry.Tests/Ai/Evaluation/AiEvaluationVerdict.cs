namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>What running one fixture found.</summary>
/// <param name="Passed">Whether the actual outcome matched <c>expect</c>.</param>
/// <param name="Detail">
/// On failure, what actually happened versus what was expected — enough to diagnose without re-running.
/// Empty on success.
/// </param>
public sealed record AiEvaluationVerdict(bool Passed, string Detail)
{
    public static AiEvaluationVerdict Pass() => new(true, string.Empty);

    public static AiEvaluationVerdict Fail(string detail) => new(false, detail);
}

/// <summary>Runs every fixture of one <see cref="AiEvaluationKind"/> and reports what happened.</summary>
public interface IAiEvaluationCase
{
    AiEvaluationKind Kind { get; }

    Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken);
}
