using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The worked example approved with the contract (13A.1) — since 13A.4 the catalogue's own first entry, read from
/// there — plus stand-ins and helpers for building definitions that differ from it.
/// </summary>
internal static class WorkflowDefinitionFixtures
{
    public static WorkflowDefinition CreateRecipe => WorkflowCatalogue.RecipeCreateV1;

    /// <summary>The definitions the example suggests, as the smallest valid stand-ins, so a catalogue can resolve them.</summary>
    public static IReadOnlyList<WorkflowDefinition> SuggestedStandIns { get; } =
    [
        StandIn("recipe.test-and-approve"),
        StandIn("content.blog-and-seo"),
        StandIn("images.create"),
    ];

    public static WorkflowDefinition StandIn(string key, int version = 1) => new()
    {
        Key = key,
        Version = version,
        Goal = "Do one thing",
        Description = "A stand-in for a workflow the catalogue will define.",
        ExpectedOutcome = "The one thing, done.",
        Steps =
        [
            new()
            {
                Key = "only",
                Title = "Do it",
                Help = "The only step.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesNew),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.Recipe),
            },
        ],
    };

    /// <summary>Replaces one step of a definition by key, leaving the rest as they are.</summary>
    public static WorkflowDefinition WithStep(
        this WorkflowDefinition definition,
        string key,
        Func<WorkflowStepDefinition, WorkflowStepDefinition> change) =>
        definition with
        {
            Steps = [.. definition.Steps.Select(step => step.Key.Value == key ? change(step) : step)],
        };
}
