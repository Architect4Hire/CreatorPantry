using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The shape of the proposal aggregate: its boundaries, its retention, and the two properties that make the
/// privacy rules structural rather than remembered.
/// </summary>
/// <remarks>
/// Discovers the module's entities from the model rather than naming them one by one where it can, in the
/// same spirit as <c>RecipeModelShapeTests</c>: a sixth AI entity added without a query filter would pass
/// every test that only knows about the five here.
/// </remarks>
public sealed class AiProposalModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// The module's aggregate roots, and the only AI entities permitted a direct foreign key to a workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the operation. <see cref="AiProposal"/> is a root for reading and routing — its own id, its own
    /// alternate key — but for deletion it is interior to its operation, so it carries no foreign key to
    /// <c>Workspaces</c>.
    /// </para>
    /// <para>
    /// That is not tidiness. A second edge to <c>Workspaces</c> gives SQL Server two cascade paths to the same
    /// table and it refuses the DDL outright. SQLite does not enforce the rule, so the whole of this file
    /// passed against a schema SQL Server would not create, until the migration ran against a real database —
    /// which is what <see cref="Every_cascade_path_from_the_workspace_is_a_tree"/> now catches at this level.
    /// </para>
    /// </remarks>
    private static readonly Type[] AggregateRoots = [typeof(AiOperation)];

    /// <summary>
    /// No AI table is reachable from <c>Workspaces</c> by two cascading paths.
    /// </summary>
    /// <remarks>
    /// SQL Server rejects such a schema, and the SQLite-backed model here will not, so this walks the cascade
    /// edges itself rather than waiting for a real database to say no. It is deliberately about the whole
    /// module rather than about which types are roots: a future entity that cascades from two parents is the
    /// same defect wearing different names.
    /// </remarks>
    [Fact]
    public void Every_cascade_path_from_the_workspace_is_a_tree()
    {
        var model = Model();

        var offenders = AiEntityTypes()
            .Select(type => (type, cascading: model.FindEntityType(type)!.GetForeignKeys()
                .Where(key => key.DeleteBehavior == DeleteBehavior.Cascade)
                .ToList()))
            .Where(entry => entry.cascading.Count > 1)
            .Select(entry => $"{entry.type.Name} cascades from "
                + string.Join(" and ", entry.cascading.Select(key => key.PrincipalEntityType.ClrType.Name)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "SQL Server refuses multiple cascade paths to one table:\n" + string.Join("\n", offenders));
    }

    public static TheoryData<string> AiEntityNames() =>
        [.. AiEntityTypes().Select(type => type.FullName!)];

    [Theory]
    [MemberData(nameof(AiEntityNames))]
    public void Every_ai_entity_is_workspace_owned_and_filtered(string entityName)
    {
        var entityType = Model().FindEntityType(entityName)!;

        Assert.True(
            typeof(IWorkspaceOwned).IsAssignableFrom(entityType.ClrType),
            $"{entityName} is not IWorkspaceOwned");

        var workspaceId = entityType.FindProperty(nameof(IWorkspaceOwned.WorkspaceId));
        Assert.True(workspaceId is not null, $"{entityName} has no WorkspaceId");
        Assert.False(workspaceId!.IsNullable, $"{entityName}.WorkspaceId is nullable");

        Assert.True(
            entityType.GetDeclaredQueryFilters().Count > 0,
            $"{entityName} has no global query filter, so a query against it reads every workspace");
    }

    [Theory]
    [MemberData(nameof(AiEntityNames))]
    public void Every_ai_entity_can_be_reached_workspace_first(string entityName)
    {
        var entityType = Model().FindEntityType(entityName)!;

        var workspaceFirst = entityType.GetIndexes().Select(index => index.Properties)
            .Concat(entityType.GetKeys().Select(key => key.Properties))
            .Any(properties => properties[0].Name == nameof(IWorkspaceOwned.WorkspaceId));

        Assert.True(workspaceFirst, $"{entityName} has no workspace-leading index or key");
    }

    [Fact]
    public void Only_aggregate_roots_point_at_the_workspace()
    {
        var model = Model();

        var pointingAtWorkspaces = AiEntityTypes()
            .Where(type => model.FindEntityType(type)!.GetForeignKeys()
                .Any(key => key.PrincipalEntityType.ClrType == typeof(Workspace)))
            .ToList();

        Assert.Equal(
            [.. AggregateRoots.OrderBy(type => type.Name)],
            [.. pointingAtWorkspaces.OrderBy(type => type.Name)]);
    }

    [Fact]
    public void Every_interior_entity_cascades_from_exactly_one_parent_inside_the_module()
    {
        var model = Model();

        foreach (var type in AiEntityTypes().Except(AggregateRoots))
        {
            var cascading = model.FindEntityType(type)!.GetForeignKeys()
                .Where(key => key.DeleteBehavior == DeleteBehavior.Cascade)
                .ToList();

            Assert.True(
                cascading.Count == 1,
                $"{type.Name} has {cascading.Count} cascading foreign keys; exactly one keeps the delete path a tree");

            Assert.Contains(cascading[0].PrincipalEntityType.ClrType, AiEntityTypes());
        }
    }

    /// <summary>
    /// Every cross-module reference carries <c>WorkspaceId</c> into the key, so an AI row pointing at another
    /// workspace's recipe content is unreferenceable at the database rather than only refused above it.
    /// </summary>
    [Fact]
    public void Every_reference_into_the_recipe_module_is_composite_with_the_workspace()
    {
        var model = Model();

        var crossModule = AiEntityTypes()
            .SelectMany(type => model.FindEntityType(type)!.GetForeignKeys())
            .Where(key => key.PrincipalEntityType.ClrType == typeof(Recipe)
                || key.PrincipalEntityType.ClrType == typeof(RecipeVersion));

        Assert.NotEmpty(crossModule);
        Assert.All(crossModule, key =>
            Assert.Equal(nameof(IWorkspaceOwned.WorkspaceId), key.Properties[0].Name));
    }

    /// <summary>Deleting a recipe that has AI history is refused, matching <c>RecipeVersion</c>.</summary>
    [Fact]
    public void A_recipe_delete_cannot_reach_a_proposal()
    {
        var toVersion = Model().FindEntityType(typeof(AiProposal))!.GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(RecipeVersion));

        Assert.Equal(DeleteBehavior.Restrict, toVersion.DeleteBehavior);
    }

    [Fact]
    public void One_operation_has_at_most_one_proposal()
    {
        Assert.Contains(Model().FindEntityType(typeof(AiProposal))!.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(AiProposal.WorkspaceId), nameof(AiProposal.AiOperationId)]));
    }

    [Fact]
    public void One_operation_has_at_most_one_row_per_attempt()
    {
        Assert.Contains(Model().FindEntityType(typeof(AiExecutionMetadata))!.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
            [
                nameof(AiExecutionMetadata.WorkspaceId),
                nameof(AiExecutionMetadata.AiOperationId),
                nameof(AiExecutionMetadata.AttemptNumber),
            ]));
    }

    /// <summary>
    /// Execution metadata hangs off the operation, not the proposal, because a failed attempt produces a row
    /// here and no proposal at all.
    /// </summary>
    [Fact]
    public void Execution_metadata_belongs_to_the_operation()
    {
        var parents = Model().FindEntityType(typeof(AiExecutionMetadata))!.GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType)
            .ToArray();

        Assert.Equal([typeof(AiOperation)], parents);
    }

    /// <summary>
    /// Only the change row is editable, and only so the creator's decision can be written to it. Everything
    /// else in the aggregate is write-once.
    /// </summary>
    [Fact]
    public void Only_the_structured_change_is_not_write_once()
    {
        var mutable = AiEntityTypes()
            .Where(type => !typeof(IImmutableRecord).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal([typeof(AiOperation), typeof(AiStructuredChange)], mutable.OrderBy(type => type.Name).ToArray());
    }

    /// <summary>
    /// ai.md forbids storing prompt bodies, model responses, or provider payloads as diagnostics. The
    /// execution record has exactly one text column that is not an identifier, and it is the sanitized failure
    /// summary — a property worth asserting, because the cheapest way to break it is adding a "RawResponse"
    /// string the next time something needs debugging.
    /// </summary>
    [Fact]
    public void The_diagnostic_record_has_one_free_text_column()
    {
        var text = Model().FindEntityType(typeof(AiExecutionMetadata))!.GetProperties()
            .Where(property => property.ClrType == typeof(string))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                nameof(AiExecutionMetadata.FailureSummary),
                nameof(AiExecutionMetadata.ModelDeployment),
                nameof(AiExecutionMetadata.ModelName),
                nameof(AiExecutionMetadata.PromptTemplateId),
                nameof(AiExecutionMetadata.PromptTemplateVersion),
                nameof(AiExecutionMetadata.ProviderName),
            ],
            text);

        Assert.Equal(
            AiPolicy.DiagnosticMaxLength,
            Model().FindEntityType(typeof(AiExecutionMetadata))!
                .FindProperty(nameof(AiExecutionMetadata.FailureSummary))!.GetMaxLength());
    }

    /// <summary>
    /// A proposal records the body that produced it, not merely the version that was claimed. A template's
    /// manifest cannot change its body without changing this value, so a stored proposal stays explainable
    /// after the template moves on.
    /// </summary>
    [Fact]
    public void A_proposal_records_its_template_body_checksum()
    {
        var checksum = Model().FindEntityType(typeof(AiProposal))!
            .FindProperty(nameof(AiProposal.PromptTemplateBodyChecksum))!;

        Assert.False(checksum.IsNullable);
        Assert.Equal(AiPolicy.ChecksumMaxLength, checksum.GetMaxLength());
    }

    [Theory]
    [InlineData("CK_AiStructuredChanges_ChangeKind_Declared")]
    [InlineData("CK_AiStructuredChanges_TargetKind_Declared")]
    [InlineData("CK_AiStructuredChanges_FieldName_Set")]
    [InlineData("CK_AiStructuredChanges_Position_Kind")]
    [InlineData("CK_AiStructuredChanges_Position_NotNegative")]
    [InlineData("CK_AiStructuredChanges_Decision_Complete")]
    [InlineData("CK_AiStructuredChanges_HasAValue")]
    public void The_change_table_declares_its_check_constraints(string name)
    {
        Assert.Contains(
            DesignTime(typeof(AiStructuredChange)).GetCheckConstraints(),
            constraint => constraint.Name == name);
    }

    [Theory]
    [InlineData("CK_AiExecutionMetadata_Attempt_Positive")]
    [InlineData("CK_AiExecutionMetadata_Latency_NotNegative")]
    [InlineData("CK_AiExecutionMetadata_Tokens_NotNegative")]
    [InlineData("CK_AiExecutionMetadata_Cost_NotNegative")]
    [InlineData("CK_AiExecutionMetadata_FailureCategory_Declared")]
    [InlineData("CK_AiExecutionMetadata_Summary_Requires_Failure")]
    [InlineData("CK_AiExecutionMetadata_Completed_After_Started")]
    public void The_execution_table_declares_its_check_constraints(string name)
    {
        Assert.Contains(
            DesignTime(typeof(AiExecutionMetadata)).GetCheckConstraints(),
            constraint => constraint.Name == name);
    }

    [Theory]
    [InlineData(typeof(AiWarning), "CK_AiWarnings_Kind_Declared")]
    [InlineData(typeof(AiProposalFeedback), "CK_AiProposalFeedback_SaysSomething")]
    public void The_remaining_tables_declare_their_check_constraints(Type entity, string name)
    {
        Assert.Contains(DesignTime(entity).GetCheckConstraints(), constraint => constraint.Name == name);
    }

    /// <summary>Every concrete entity class in the AI module's entity namespace.</summary>
    private static Type[] AiEntityTypes() => [.. typeof(AiOperation).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(AiOperation).Namespace && type is { IsClass: true, IsAbstract: false })
        .OrderBy(type => type.Name)];

    private IModel Model()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model;
    }

    /// <inheritdoc cref="AiOperationModelShapeTests.Declares_its_check_constraints"/>
    private IEntityType DesignTime(Type entity)
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);

        return RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model.FindEntityType(entity)!;
    }
}
