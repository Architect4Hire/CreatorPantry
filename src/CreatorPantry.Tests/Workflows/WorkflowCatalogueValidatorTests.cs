using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Codes = CreatorPantry.Domain.Modules.Workflows.Managers.WorkflowDefinitionErrorCodes;

namespace CreatorPantry.Tests.Workflows;

public sealed class WorkflowCatalogueValidatorTests
{
    private static readonly WorkflowDefinition Example = WorkflowDefinitionFixtures.CreateRecipe;

    [Fact]
    public void The_example_and_the_workflows_it_suggests_form_a_valid_catalogue()
    {
        Assert.Empty(WorkflowCatalogueValidator.Validate([Example, .. WorkflowDefinitionFixtures.SuggestedStandIns]));
    }

    [Fact]
    public void A_suggested_workflow_must_be_in_the_catalogue()
    {
        var errors = WorkflowCatalogueValidator.Validate([Example]);

        Assert.Equal(3, errors.Count);
        Assert.All(errors, error => Assert.Equal(Codes.SuggestionUnknownWorkflow, error.Code));
    }

    [Fact]
    public void Each_definition_is_validated_on_its_own_too()
    {
        var errors = WorkflowCatalogueValidator.Validate(
            [Example with { Goal = "" }, .. WorkflowDefinitionFixtures.SuggestedStandIns]);

        Assert.Equal([Codes.CopyMissing], errors.Select(error => error.Code));
    }

    [Fact]
    public void Every_version_of_a_workflow_stays_in_the_catalogue()
    {
        var catalogue = new[]
        {
            WorkflowDefinitionFixtures.StandIn("recipe.improve", 1),
            WorkflowDefinitionFixtures.StandIn("recipe.improve", 2),
            WorkflowDefinitionFixtures.StandIn("recipe.improve", 3),
        };

        Assert.Empty(WorkflowCatalogueValidator.Validate(catalogue));
    }

    [Theory]
    [InlineData(new[] { 1, 3 })]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 2, 3 })]
    public void A_version_a_run_could_be_pinned_to_is_never_missing(int[] versions)
    {
        var errors = WorkflowCatalogueValidator.Validate(
            [.. versions.Select(version => WorkflowDefinitionFixtures.StandIn("recipe.improve", version))]);

        Assert.Equal([Codes.CatalogueVersionGap], errors.Select(error => error.Code));
    }

    [Fact]
    public void A_key_and_version_identify_one_definition()
    {
        var errors = WorkflowCatalogueValidator.Validate(
        [
            WorkflowDefinitionFixtures.StandIn("recipe.improve", 1),
            WorkflowDefinitionFixtures.StandIn("recipe.improve", 1) with { Goal = "Improve a recipe" },
        ]);

        Assert.Equal([Codes.CatalogueDuplicateVersion], errors.Select(error => error.Code));
    }

    [Fact]
    public void A_defect_in_the_capability_registry_fails_the_catalogue()
    {
        var registry = WorkflowCapabilities.All
            .Append(new WorkflowCapability("recipes.export", "Download it", "recipes/{orderId}/export", WorkspaceRole.Viewer))
            .ToList();

        var errors = WorkflowCatalogueValidator.Validate([Example, .. WorkflowDefinitionFixtures.SuggestedStandIns], registry);

        Assert.Equal([Codes.CapabilityRouteInvalid], errors.Select(error => error.Code));
    }
}
