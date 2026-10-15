using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The promises a workflow run makes about its own shape (13A.2): state and stable pointers, never a copy of
/// what the journey touched, and a delete path that cannot reach another module's records.
/// </summary>
/// <remarks>
/// Most of these are rules about what must <em>not</em> be added — a recipe title, a draft body, a foreign key
/// into the recipe module — each of which would work perfectly and quietly defeat the design.
/// </remarks>
public sealed class WorkflowRunModelShapeTests : IDisposable
{
    private static readonly Type[] Entities = [typeof(WorkflowRun), typeof(WorkflowStepRun), typeof(WorkflowRunLink)];

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void The_module_has_exactly_these_entities()
    {
        var discovered = typeof(WorkflowRun).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(WorkflowRun).Namespace && type is { IsClass: true, IsAbstract: false })
            .OrderBy(type => type.Name);

        Assert.Equal(Entities.OrderBy(type => type.Name), discovered);
    }

    public static TheoryData<Type> EntityTypes() => [.. Entities];

    [Theory]
    [MemberData(nameof(EntityTypes))]
    public void Every_entity_is_workspace_owned_filtered_and_reachable_workspace_first(Type entity)
    {
        var entityType = Model().FindEntityType(entity)!;

        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(entity));
        Assert.False(entityType.FindProperty(nameof(IWorkspaceOwned.WorkspaceId))!.IsNullable);
        Assert.NotEmpty(entityType.GetDeclaredQueryFilters());

        Assert.Contains(
            entityType.GetIndexes().Select(index => index.Properties).Concat(entityType.GetKeys().Select(key => key.Properties)),
            properties => properties[0].Name == nameof(IWorkspaceOwned.WorkspaceId));
    }

    [Fact]
    public void Only_the_run_points_at_the_workspace_and_its_parts_cascade_from_the_run_alone()
    {
        var model = Model();

        var run = model.FindEntityType(typeof(WorkflowRun))!.GetForeignKeys().ToList();
        var onlyEdge = Assert.Single(run);
        Assert.Equal(typeof(Workspace), onlyEdge.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, onlyEdge.DeleteBehavior);

        foreach (var part in new[] { typeof(WorkflowStepRun), typeof(WorkflowRunLink) })
        {
            // One foreign key and one only: into the run, composite with the workspace, cascading. A second edge
            // to Workspaces would give SQL Server two cascade paths, which it refuses and SQLite does not.
            var key = Assert.Single(model.FindEntityType(part)!.GetForeignKeys());
            Assert.Equal(typeof(WorkflowRun), key.PrincipalEntityType.ClrType);
            Assert.Equal(DeleteBehavior.Cascade, key.DeleteBehavior);
            Assert.Equal(
                [nameof(IWorkspaceOwned.WorkspaceId), "WorkflowRunId"],
                key.Properties.Select(property => property.Name));
        }
    }

    [Fact]
    public void No_entity_holds_free_text_beyond_keys_a_route_and_the_creators_own_words()
    {
        // Adding a string here should mean arguing with this list first: it is either a key, a route, or the
        // creator's words about the run — or it is a copy of something another module owns.
        var strings = Entities
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => $"{entity.Name}.{property.Name}"))
            .Order()
            .ToList();

        Assert.Equal(
            [
                "WorkflowRun.AbandonReason",
                "WorkflowRun.CurrentStepKey",
                "WorkflowRun.DefinitionKey",
                "WorkflowRun.LastRoute",
                "WorkflowRun.Title",
                "WorkflowRunLink.StepKey",
                "WorkflowStepRun.SkipReason",
                "WorkflowStepRun.StepKey",
            ],
            strings);
    }

    [Fact]
    public void Only_the_run_carries_bytes_and_those_are_its_concurrency_token()
    {
        var bytes = Entities
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.PropertyType == typeof(byte[]) || property.PropertyType == typeof(Stream))
                .Select(property => $"{entity.Name}.{property.Name}"))
            .ToList();

        Assert.Equal(["WorkflowRun.RowVersion"], bytes);
        Assert.True(Model().FindEntityType(typeof(WorkflowRun))!.FindProperty(nameof(WorkflowRun.RowVersion))!.IsConcurrencyToken);
    }

    [Fact]
    public void No_property_names_a_provider_a_prompt_or_a_secret()
    {
        var offenders = Entities
            .SelectMany(entity => entity.GetProperties().Select(property => $"{entity.Name}.{property.Name}"))
            .Where(name => new[] { "Provider", "Model", "Prompt", "Deployment", "Secret", "Token", "Body", "Content", "Url" }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    public static TheoryData<Type, string> CheckConstraints() => new()
    {
        { typeof(WorkflowRun), "CK_WorkflowRuns_Status_Range" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_DefinitionKey_NotBlank" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_DefinitionVersion_Positive" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_Title_NotBlank" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_StepCount_Positive" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_Progress_Bounded" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_CurrentStep_Status" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_CurrentStep_NotBlank" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_CompletedAt_Status" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_AbandonedAt_Status" },
        { typeof(WorkflowRun), "CK_WorkflowRuns_LastRoute_Relative" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_Status_Range" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_StepKey_NotBlank" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_NotStarted_NoStart" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_Completed_Recorded" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_Skipped_Recorded" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_SkipReason_NotBlank" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_Draft_Whole" },
        { typeof(WorkflowStepRun), "CK_WorkflowStepRuns_DraftKind_Range" },
        { typeof(WorkflowRunLink), "CK_WorkflowRunLinks_ResourceKind_Range" },
        { typeof(WorkflowRunLink), "CK_WorkflowRunLinks_StepKey_NotBlank" },
    };

    [Theory]
    [MemberData(nameof(CheckConstraints))]
    public void Declares_its_check_constraints(Type entity, string name)
    {
        Assert.Contains(DesignTime(entity).GetCheckConstraints(), constraint => constraint.Name == name);
    }

    [Theory]
    [InlineData(typeof(WorkflowRun), "IX_WorkflowRuns_Workspace_Status_Updated", false)]
    [InlineData(typeof(WorkflowRun), "IX_WorkflowRuns_Workspace_Creator_Status_Updated", false)]
    [InlineData(typeof(WorkflowRun), "IX_WorkflowRuns_Workspace_Assignee_Status_Updated", false)]
    [InlineData(typeof(WorkflowRun), "IX_WorkflowRuns_Workspace_Definition_Version", false)]
    [InlineData(typeof(WorkflowStepRun), "UX_WorkflowStepRuns_Workspace_Run_StepKey", true)]
    [InlineData(typeof(WorkflowRunLink), "UX_WorkflowRunLinks_Workspace_Run_Resource", true)]
    [InlineData(typeof(WorkflowRunLink), "IX_WorkflowRunLinks_Workspace_Resource", false)]
    public void Declares_its_indexes_workspace_first(Type entity, string name, bool unique)
    {
        var index = Assert.Single(Model().FindEntityType(entity)!.GetIndexes(), index => index.GetDatabaseName() == name);

        Assert.Equal(unique, index.IsUnique);
        Assert.Equal(nameof(IWorkspaceOwned.WorkspaceId), index.Properties[0].Name);
    }

    private IModel Model()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);

        return RecipeAggregateFixture.Db(scope).Model;
    }

    private IEntityType DesignTime(Type entity)
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);

        return RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model.FindEntityType(entity)!;
    }
}
