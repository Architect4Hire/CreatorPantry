using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Tests.Workflows;

public sealed class WorkflowDefinitionVersionTests
{
    private static readonly WorkflowDefinition Example = WorkflowDefinitionFixtures.CreateRecipe;

    public static TheoryData<string, WorkflowDefinition> Changes() => new()
    {
        { "goal", Example with { Goal = "Make a recipe" } },
        { "description", Example with { Description = Example.Description + " Today." } },
        { "outcome", Example with { ExpectedOutcome = "A recipe." } },
        { "workflow prerequisite", Example with { Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe)] } },
        { "step order", Example with { Steps = [Example.Steps[0], Example.Steps[2], Example.Steps[1], .. Example.Steps.Skip(3)] } },
        { "step key", Example.WithStep("method", step => step with { Key = "steps" }) },
        { "step title", Example.WithStep("method", step => step with { Title = "Check the method" }) },
        { "step help", Example.WithStep("method", step => step with { Help = step.Help + "!" }) },
        { "step requirement", Example.WithStep("method", step => step with { Requirement = WorkflowStepRequirement.Optional }) },
        { "primary capability", Example.WithStep("start", step => step with { Capability = new(WorkflowCapabilities.RecipesDraftWithHelp, [WorkflowCapabilities.RecipesNew]) }) },
        { "alternatives", Example.WithStep("start", step => step with { Capability = new(WorkflowCapabilities.RecipesNew) }) },
        { "step prerequisite", Example.WithStep("method", step => step with { Prerequisites = [new WorkflowPrerequisite.StepCompleted("ingredients")] }) },
        { "readiness rules", Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([RecipeReadinessCatalogue.InstructionsPresent, RecipeReadinessCatalogue.DescriptionPresent]) }) },
        { "completion kind", Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.RecipeStatusIn([RecipeStatus.InDevelopment]) }) },
        { "suggestion reason", Example with { NextSuggestions = [new WorkflowSuggestion.Workflow("images.create", "Show it off.")] } },
        { "suggestion order", Example with { NextSuggestions = [.. Example.NextSuggestions.Reverse()] } },
        { "suggestion target kind", Example with { NextSuggestions = [new WorkflowSuggestion.Capability(WorkflowCapabilities.RecipesEdit, Example.NextSuggestions[0].Why)] } },
    };

    [Fact]
    public void The_canonical_form_is_deterministic()
    {
        var rebuilt = Example with { Steps = [.. Example.Steps.Select(step => step with { })] };

        Assert.Equal(WorkflowDefinitionLedger.Canonical(Example), WorkflowDefinitionLedger.Canonical(rebuilt));
    }

    /// <summary>
    /// Every field reaches the record, so any edit to a shipped definition is caught — copy included, because
    /// a run part-way through one is reading that copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_change_to_a_definition_changes_its_record(string what, WorkflowDefinition changed)
    {
        Assert.False(
            WorkflowDefinitionLedger.Canonical(Example) == WorkflowDefinitionLedger.Canonical(changed),
            $"changing the {what} left the record unchanged");
    }

    [Fact]
    public void An_unchanged_catalogue_honours_what_was_shipped()
    {
        var shipped = WorkflowDefinitionLedger.Entries([Example]);

        Assert.Empty(WorkflowDefinitionLedger.Violations(shipped, WorkflowDefinitionLedger.Entries([Example])));
    }

    [Fact]
    public void Editing_a_shipped_version_in_place_is_refused()
    {
        var shipped = WorkflowDefinitionLedger.Entries([Example]);
        var current = WorkflowDefinitionLedger.Entries([Example with { Goal = "Make a recipe" }]);

        var violation = Assert.Single(WorkflowDefinitionLedger.Violations(shipped, current));
        Assert.StartsWith("recipe.create@1 was shipped and has changed", violation);
        Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionLedger.Append(shipped, current));
    }

    [Fact]
    public void Removing_a_shipped_version_is_refused_because_a_run_may_be_pinned_to_it()
    {
        var shipped = WorkflowDefinitionLedger.Entries([Example]);
        var current = WorkflowDefinitionLedger.Entries([Example with { Version = 2 }]);

        var violation = Assert.Single(WorkflowDefinitionLedger.Violations(shipped, current));
        Assert.StartsWith("recipe.create@1 was shipped and has been removed", violation);
        Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionLedger.Append(shipped, current));
    }

    [Fact]
    public void A_change_ships_as_the_next_version_beside_the_old_one()
    {
        var shipped = WorkflowDefinitionLedger.Entries([Example]);
        var next = Example with { Version = 2, Goal = "Make a recipe" };
        var current = WorkflowDefinitionLedger.Entries([Example, next]);

        var appended = WorkflowDefinitionLedger.Append(shipped, current);

        Assert.Equal(["recipe.create@1", "recipe.create@2"], appended.Keys);
        Assert.Equal(shipped["recipe.create@1"], appended["recipe.create@1"]);
        Assert.Empty(WorkflowCatalogueValidator.Validate([Example, next, .. WorkflowDefinitionFixtures.SuggestedStandIns]));
    }

    [Fact]
    public void A_new_workflow_ships_at_version_one()
    {
        var shipped = WorkflowDefinitionLedger.Entries([Example]);
        var current = WorkflowDefinitionLedger.Entries([Example, .. WorkflowDefinitionFixtures.SuggestedStandIns]);

        Assert.Equal(4, WorkflowDefinitionLedger.Append(shipped, current).Count);
    }
}
