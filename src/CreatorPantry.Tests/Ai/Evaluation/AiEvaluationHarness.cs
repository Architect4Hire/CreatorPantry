namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>What running one fixture through the harness produced.</summary>
public sealed record AiEvaluationCaseResult(AiEvaluationFixture Fixture, AiEvaluationVerdict Verdict);

/// <summary>Every fixture's result, for building a baseline report from.</summary>
public sealed record AiEvaluationReport(IReadOnlyList<AiEvaluationCaseResult> Results)
{
    public int Total => Results.Count;

    public int Passed => Results.Count(result => result.Verdict.Passed);

    public int Failed => Total - Passed;

    public IEnumerable<IGrouping<AiEvaluationCategory, AiEvaluationCaseResult>> ByCategory() =>
        Results.GroupBy(result => result.Fixture.Category);
}

/// <summary>
/// Loads every fixture and runs each through the case runner registered for its <see cref="AiEvaluationKind"/>.
/// </summary>
/// <remarks>
/// Independent of xUnit — <see cref="AiEvaluationHarnessTests"/> is a thin adapter over this, one
/// <c>[Theory]</c> case per fixture so CI reports each by name, but the dispatch and aggregation logic itself
/// is plain code any caller (a baseline-report generator, for instance) can run directly.
/// </remarks>
public sealed class AiEvaluationHarness(AiEvaluationFixtureStore store, IReadOnlyDictionary<AiEvaluationKind, IAiEvaluationCase> cases)
{
    /// <summary>
    /// Every case runner, keyed by the kind it handles. The one list.
    /// </summary>
    /// <remarks>
    /// <see cref="AiEvaluationHarnessTests"/> dispatches through this rather than keeping a second copy. It
    /// kept one until a new kind was added to that copy and not to this — or rather, the other way about,
    /// which was worse: the runner was registered here and the fixtures simply threw, so the failure said
    /// "no case runner is wired up" for a runner that plainly existed.
    /// </remarks>
    public static IReadOnlyDictionary<AiEvaluationKind, IAiEvaluationCase> Cases { get; } =
        new IAiEvaluationCase[]
        {
            new OutputValidationCase(),
            new PromptEnvelopeCase(),
            new ProposalAssemblyCase(),
            new WorkerOperationCase(),
            new ConceptOutputValidationCase(),
            new RecipeDraftOutputValidationCase(),
            new SubstitutionOutputValidationCase(),
            new AdaptationOutputValidationCase(),
            new RecipeReviewOutputValidationCase(),
            new EditorialPackageOutputValidationCase(),
            new SeoPackageOutputValidationCase(),
            new SeoSlugDerivationCase(),
        }.ToDictionary(evaluationCase => evaluationCase.Kind);

    public static AiEvaluationHarness Default(AiEvaluationFixtureStore store) => new(store, Cases);

    public async Task<AiEvaluationReport> RunAllAsync(CancellationToken cancellationToken)
    {
        var results = new List<AiEvaluationCaseResult>(store.All.Count);

        foreach (var fixture in store.All)
        {
            if (!cases.TryGetValue(fixture.Kind, out var runner))
            {
                throw new AiEvaluationException(fixture.Identity, $"no case runner is registered for kind '{fixture.Kind}'.");
            }

            results.Add(new AiEvaluationCaseResult(fixture, await runner.RunAsync(fixture, cancellationToken)));
        }

        return new AiEvaluationReport(results);
    }
}
