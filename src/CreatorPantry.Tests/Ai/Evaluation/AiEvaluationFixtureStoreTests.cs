using System.Reflection;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// The embedded-resource path end to end, mirroring
/// <c>CreatorPantry.Tests.Prompts.PromptTemplateStoreTests</c> for the sibling loader this one is modeled on.
/// </summary>
public sealed class AiEvaluationFixtureStoreTests
{
    private const string DuplicateFixtures = "CreatorPantry.Tests.Ai.Evaluation.DuplicateFixtures.";

    private static Assembly TestAssembly => typeof(AiEvaluationFixtureStoreTests).Assembly;

    [Fact]
    public void Two_resources_claiming_one_id_and_version_fail_the_load()
    {
        var failure = Assert.Throws<AiEvaluationException>(() => AiEvaluationFixtureStore.Load(
            name => name.StartsWith(DuplicateFixtures, StringComparison.Ordinal),
            TestAssembly));

        Assert.Contains("dup-1.0.0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("declared by two resources", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A filter matching nothing is a legitimate state, not a load failure.</summary>
    [Fact]
    public void A_filter_matching_nothing_loads_empty()
    {
        var store = AiEvaluationFixtureStore.Load(static _ => false, TestAssembly);

        Assert.Empty(store.All);
    }
}
