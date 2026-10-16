using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// The promises a work task makes about its own shape (13A.23): one person's work and stable pointers to what it
/// is about, never an editorial or publishing state, never a copy of a linked record, and a delete path that
/// cannot reach — or be held up by — another module's records.
/// </summary>
/// <remarks>
/// Most of these are rules about what must <em>not</em> be added — a recipe title, a board column, a foreign key
/// into the recipe module — each of which would work perfectly and quietly defeat the design.
/// </remarks>
public sealed class WorkTaskModelShapeTests : IDisposable
{
    private static readonly Type[] Entities = [typeof(WorkTask), typeof(WorkTaskLabel), typeof(WorkTaskTemplate), typeof(WorkTaskFocus)];

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void The_module_has_exactly_these_entities()
    {
        var discovered = typeof(WorkTask).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(WorkTask).Namespace && type is { IsClass: true, IsAbstract: false })
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
    public void Only_the_task_points_at_the_workspace_and_its_labels_cascade_from_the_task_alone()
    {
        var model = Model();

        // Two edges: the workspace, which cascades, and the routine it came from (13A.30), this module's own record,
        // which is workspace-paired and restricted — a second cascade path is what SQL Server refuses.
        var taskKeys = model.FindEntityType(typeof(WorkTask))!.GetForeignKeys().ToList();
        Assert.Equal(2, taskKeys.Count);
        var onlyEdge = Assert.Single(taskKeys, key => key.PrincipalEntityType.ClrType == typeof(Workspace));
        Assert.Equal(DeleteBehavior.Cascade, onlyEdge.DeleteBehavior);
        var template = Assert.Single(taskKeys, key => key.PrincipalEntityType.ClrType == typeof(WorkTaskTemplate));
        Assert.Equal(DeleteBehavior.Restrict, template.DeleteBehavior);
        Assert.Equal(
            [nameof(IWorkspaceOwned.WorkspaceId), nameof(WorkTask.WorkTaskTemplateId)],
            template.Properties.Select(property => property.Name));

        var templateEdge = Assert.Single(model.FindEntityType(typeof(WorkTaskTemplate))!.GetForeignKeys());
        Assert.Equal(typeof(Workspace), templateEdge.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, templateEdge.DeleteBehavior);

        var labelKeys = model.FindEntityType(typeof(WorkTaskLabel))!.GetForeignKeys().ToList();
        Assert.Equal(2, labelKeys.Count);

        // Into the task, composite with the workspace, cascading. A direct edge to Workspaces would be a second
        // cascade path, which SQL Server refuses and SQLite does not.
        var task = Assert.Single(labelKeys, key => key.PrincipalEntityType.ClrType == typeof(WorkTask));
        Assert.Equal(DeleteBehavior.Cascade, task.DeleteBehavior);
        Assert.Equal([nameof(IWorkspaceOwned.WorkspaceId), nameof(WorkTaskLabel.WorkTaskId)], task.Properties.Select(property => property.Name));

        // Into the shared vocabulary, workspace-paired and restricted: the tag cascades from the workspace too.
        var tag = Assert.Single(labelKeys, key => key.PrincipalEntityType.ClrType == typeof(WorkspaceTag));
        Assert.Equal(DeleteBehavior.Restrict, tag.DeleteBehavior);
        Assert.Equal([nameof(IWorkspaceOwned.WorkspaceId), nameof(WorkTaskLabel.WorkspaceTagId)], tag.Properties.Select(property => property.Name));
    }

    [Theory]
    [InlineData(nameof(WorkTask.RecipeId))]
    [InlineData(nameof(WorkTask.ContentCardId))]
    [InlineData(nameof(WorkTask.MediaAssetId))]
    [InlineData(nameof(WorkTask.WorkflowRunId))]
    public void A_link_is_a_nullable_pointer_and_never_a_foreign_key(string property)
    {
        // A key here would let a to-do cascade into a recipe or stand in the way of archiving one. The linked
        // module stays authoritative, and the write seam checks the id through its facade instead.
        var entityType = Model().FindEntityType(typeof(WorkTask))!;

        Assert.True(entityType.FindProperty(property)!.IsNullable);
        Assert.DoesNotContain(entityType.GetForeignKeys(), key => key.Properties.Any(column => column.Name == property));
    }

    [Fact]
    public void No_entity_holds_free_text_beyond_the_persons_own_words_and_a_step_key()
    {
        // Adding a string here should mean arguing with this list first: it is either the person's words about
        // their work or a key — or it is a copy of something another module owns, such as a recipe's title.
        var strings = Entities
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => $"{entity.Name}.{property.Name}"))
            .Order()
            .ToList();

        Assert.Equal(
            [
                "WorkTask.Description", "WorkTask.Title", "WorkTask.WorkflowStepKey",
                "WorkTaskTemplate.Description", "WorkTaskTemplate.TimeZoneId", "WorkTaskTemplate.Title",
            ],
            strings);
    }

    [Fact]
    public void Only_the_task_carries_bytes_and_those_are_its_concurrency_token()
    {
        var bytes = Entities
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.PropertyType == typeof(byte[]) || property.PropertyType == typeof(Stream))
                .Select(property => $"{entity.Name}.{property.Name}"))
            .ToList();

        Assert.Equal(["WorkTask.RowVersion", "WorkTaskTemplate.RowVersion"], bytes);
        Assert.True(Model().FindEntityType(typeof(WorkTask))!.FindProperty(nameof(WorkTask.RowVersion))!.IsConcurrencyToken);
    }

    [Fact]
    public void No_property_names_an_editorial_or_publishing_state_a_provider_or_a_secret()
    {
        // The person's work, not the content's: approval, review, board column and delivery belong to the board
        // and to publications, and a task that carried any of them would be a second source of truth for them.
        var offenders = Entities
            .SelectMany(entity => entity.GetProperties().Select(property => $"{entity.Name}.{property.Name}"))
            .Where(name => new[]
                {
                    "Approv", "Review", "Publish", "Column", "Channel", "Delivery",
                    "Provider", "Model", "Prompt", "Secret", "Token", "Body", "Content", "Url",
                }
                .Any(banned => name.Replace("ContentCardId", string.Empty, StringComparison.Ordinal)
                    .Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_task_status_is_not_a_board_column_or_a_publishing_state()
    {
        var editorial = Enum.GetNames<ContentCardColumn>()
            .Concat(Enum.GetNames<PublishingRecordStatus>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(Enum.GetNames<WorkTaskStatus>().Where(editorial.Contains));
        Assert.Equal(["Backlog", "InProgress", "Waiting", "Done"], Enum.GetNames<WorkTaskStatus>());
    }

    [Fact]
    public void A_new_task_starts_in_the_backlog_at_normal_priority_with_nothing_planned()
    {
        var task = new WorkTask();

        Assert.Equal(WorkTaskStatus.Backlog, task.Status);
        Assert.Equal(WorkTaskPriority.Normal, task.Priority);
        Assert.Null(task.ScheduledDate);
        Assert.Null(task.PlanningWeekStart);
        Assert.Null(task.EstimateMinutes);
        Assert.Null(task.CompletedAt);
    }

    public static TheoryData<string> CheckConstraints() =>
    [
        "CK_WorkTasks_Title_NotBlank",
        "CK_WorkTasks_Description_NotBlank",
        "CK_WorkTasks_Status_Range",
        "CK_WorkTasks_Priority_Range",
        "CK_WorkTasks_Completed_Recorded",
        "CK_WorkTasks_CompletedFrom_Range",
        "CK_WorkTasks_ScheduledDate_InWeek",
        "CK_WorkTasks_Estimate_Range",
        "CK_WorkTasks_Recurrence_Whole",
        "CK_WorkTasks_WorkflowStep_WithRun",
        "CK_WorkTasks_Origin_Range",
        "CK_WorkTasks_WorkflowOrigin_WithStep",
    ];

    [Theory]
    [MemberData(nameof(CheckConstraints))]
    public void Declares_its_check_constraints(string name)
    {
        Assert.Contains(DesignTime(typeof(WorkTask)).GetCheckConstraints(), constraint => constraint.Name == name);
    }

    [Theory]
    [InlineData(typeof(WorkTask), "IX_WorkTasks_Workspace_Assignee_Status_Day_Rank", false, null)]
    [InlineData(typeof(WorkTask), "IX_WorkTasks_Workspace_Assignee_Week_Status", false, null)]
    [InlineData(typeof(WorkTask), "IX_WorkTasks_Workspace_Assignee_Due", false, "DueDate IS NOT NULL")]
    [InlineData(typeof(WorkTask), "IX_WorkTasks_Workspace_Assignee_Completed", false, "CompletedAt IS NOT NULL")]
    [InlineData(typeof(WorkTask), "IX_WorkTasks_Workspace_Owner_Status", false, null)]
    [InlineData(typeof(WorkTask), "IX_WorkTasks_Workspace_Run_Step", false, "WorkflowRunId IS NOT NULL")]
    [InlineData(typeof(WorkTask), "UX_WorkTasks_Workspace_Template_Occurrence", true, "WorkTaskTemplateId IS NOT NULL")]
    [InlineData(typeof(WorkTask), "UX_WorkTasks_Workspace_Owner_WorkflowStep", true, "Origin = 2")]
    [InlineData(typeof(WorkTaskLabel), "IX_WorkTaskLabels_Workspace_Tag_Task", false, null)]
    public void Declares_its_indexes_workspace_first(Type entity, string name, bool unique, string? filter)
    {
        var index = Assert.Single(Model().FindEntityType(entity)!.GetIndexes(), index => index.GetDatabaseName() == name);

        Assert.Equal(unique, index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
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
