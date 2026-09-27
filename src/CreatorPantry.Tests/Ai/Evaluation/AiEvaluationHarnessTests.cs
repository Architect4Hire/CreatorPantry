namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// The versioned AI evaluation harness: every fixture under <c>Fixtures/</c> runs as its own named test case,
/// against fakes only (an in-memory SQLite database and scripted <c>IChatClient</c>s) — never a network call,
/// a provider credential, or a paid model.
/// </summary>
public sealed class AiEvaluationHarnessTests
{
    public static TheoryData<string, AiEvaluationFixture> Fixtures()
    {
        var data = new TheoryData<string, AiEvaluationFixture>();

        foreach (var fixture in Store().All)
        {
            data.Add(fixture.Identity, fixture);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_passes(string identity, AiEvaluationFixture fixture)
    {
        var verdict = await Case(fixture.Kind).RunAsync(fixture, TestContext.Current.CancellationToken);

        Assert.True(verdict.Passed, $"{identity}: {verdict.Detail}");
    }

    /// <summary>
    /// The scan found what it claims to be checking. Without this, a resource-name filter that stopped
    /// matching anything (a renamed folder, a changed default namespace) would make every fixture-driven test
    /// above vanish rather than fail, and the harness would report success while checking nothing.
    /// </summary>
    [Fact]
    public void The_fixture_store_loaded_every_known_fixture()
    {
        Assert.Equal(9, Store().All.Count);
    }

    /// <summary>Every category this task's SCOPE names has at least one fixture demonstrating it.</summary>
    [Fact]
    public void Every_category_has_at_least_one_fixture()
    {
        var covered = Store().All.Select(fixture => fixture.Category).ToHashSet();

        foreach (var category in Enum.GetValues<AiEvaluationCategory>())
        {
            Assert.Contains(category, covered);
        }
    }

    private static AiEvaluationFixtureStore Store() => AiEvaluationFixtureStore.Load(
        static name => name.Contains(".Ai.Evaluation.Fixtures.", StringComparison.Ordinal),
        typeof(AiEvaluationHarnessTests).Assembly);

    private static IAiEvaluationCase Case(AiEvaluationKind kind) => kind switch
    {
        AiEvaluationKind.OutputValidation => new OutputValidationCase(),
        AiEvaluationKind.PromptEnvelope => new PromptEnvelopeCase(),
        AiEvaluationKind.ProposalAssembly => new ProposalAssemblyCase(),
        AiEvaluationKind.WorkerOperation => new WorkerOperationCase(),
        _ => throw new NotSupportedException($"No case runner is wired up for '{kind}'."),
    };
}
