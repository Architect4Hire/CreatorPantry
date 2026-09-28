using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-008's task handler: the deterministic templating that turns an existing proposal's own rows into
/// explanation items, with no model in the loop. Covers the no-change case, the unsupported-claim guarantee
/// (every id an item names traces exactly to the source), verification-need linking, and the missing-link
/// refusal when the source no longer resolves to a proposal.
/// </summary>
public sealed class AiProposalExplanationAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiProposalExplanationAiTaskHandlerTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_source_proposal_with_nothing_on_it_produces_a_single_no_change_item()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var (requestId, _, _) = Seed(scope);

        var outcome = await Handle(scope, requestId);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var item = Assert.Single(outcome.Proposal!.Changes);
        Assert.Equal(AiChangeKind.Add, item.ChangeKind);
        Assert.Equal(AiChangeTargetKind.ProposalExplanationItem, item.TargetKind);
        Assert.Equal("No changes were proposed.", item.AfterValue);
    }

    /// <summary>
    /// The unsupported-claim guarantee, made concrete: an explanation item's linked ids are an exact match to
    /// the source proposal's own rows, not merely a subset -- nothing invented, and nothing dropped.
    /// </summary>
    [Fact]
    public async Task Grouped_fields_on_one_target_become_one_item_whose_ids_trace_exactly_to_the_source()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var targetId = Guid.NewGuid();

        var (requestId, changes, _) = Seed(
            scope,
            changes:
            [
                new AiStructuredChange
                {
                    ChangeKind = AiChangeKind.Add,
                    TargetKind = AiChangeTargetKind.Ingredient,
                    TargetId = targetId,
                    AfterValue = "flour",
                    SortOrder = 0,
                },
                new AiStructuredChange
                {
                    ChangeKind = AiChangeKind.Set,
                    TargetKind = AiChangeTargetKind.Ingredient,
                    TargetId = targetId,
                    FieldName = "quantityText",
                    BeforeValue = "2 cups",
                    AfterValue = "3 cups",
                    SortOrder = 1,
                },
                new AiStructuredChange
                {
                    ChangeKind = AiChangeKind.Set,
                    TargetKind = AiChangeTargetKind.Ingredient,
                    TargetId = targetId,
                    FieldName = "unitText",
                    BeforeValue = "cups",
                    AfterValue = "ml",
                    SortOrder = 2,
                },
            ]);

        var outcome = await Handle(scope, requestId);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var item = Assert.Single(outcome.Proposal!.Changes, c => c.ChangeKind == AiChangeKind.Add);

        var linkedIds = Field(outcome.Proposal!, item.TargetId!.Value, AiProposalExplanationFields.SourceChangeIds)!
            .Split(',')
            .Select(Guid.Parse)
            .ToHashSet();

        Assert.Equal(changes.Select(c => c.Id).ToHashSet(), linkedIds);

        var detail = Field(outcome.Proposal!, item.TargetId!.Value, AiProposalExplanationFields.Detail);
        Assert.Contains("quantityText changed from '2 cups' to '3 cups'", detail);
        Assert.Contains("unitText changed from 'cups' to 'ml'", detail);
    }

    [Fact]
    public async Task A_warning_linked_to_a_change_is_attached_to_that_items_verification_needed()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);

        var (requestId, changes, warnings) = Seed(
            scope,
            changes:
            [
                new AiStructuredChange
                {
                    ChangeKind = AiChangeKind.Set,
                    TargetKind = AiChangeTargetKind.Recipe,
                    FieldName = "headnote",
                    BeforeValue = "Old headnote",
                    AfterValue = "New headnote",
                    SortOrder = 0,
                },
            ],
            linkWarning: (change => new AiWarning
            {
                Kind = AiWarningKind.SafetyCaution,
                Message = "Check this before publishing.",
                AiStructuredChangeId = change.Id,
                SortOrder = 0,
            }));

        var outcome = await Handle(scope, requestId);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var item = Assert.Single(outcome.Proposal!.Changes, c => c.ChangeKind == AiChangeKind.Add);

        var linkedWarningIds = Field(outcome.Proposal!, item.TargetId!.Value, AiProposalExplanationFields.SourceWarningIds)!
            .Split(',')
            .Select(Guid.Parse)
            .ToHashSet();

        Assert.Equal(warnings.Select(w => w.Id).ToHashSet(), linkedWarningIds);

        // The warning becomes this explanation's own AiWarning row -- carrying its own Kind, not merged into
        // an undifferentiated string field -- kept alongside the item via AiStructuredChangeId.
        var emitted = Assert.Single(outcome.Proposal!.Warnings, w => w.AiStructuredChangeId == item.Id);
        Assert.Equal(AiWarningKind.SafetyCaution, emitted.Kind);
        Assert.Equal("Check this before publishing.", emitted.Message);
    }

    [Fact]
    public async Task A_proposal_level_warning_becomes_its_own_general_item()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);

        var (requestId, _, warnings) = Seed(
            scope,
            generalWarnings: [new AiWarning { Kind = AiWarningKind.Assumption, Message = "Assumed a 9-inch pan.", SortOrder = 0 }]);

        var outcome = await Handle(scope, requestId);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);

        // No changes were seeded, so the general-warning item must stand alone -- the boilerplate "no changes"
        // item is skipped whenever there is a real warning to explain instead.
        var item = Assert.Single(outcome.Proposal!.Changes, c => c.ChangeKind == AiChangeKind.Add);
        Assert.Equal("General notes about this proposal.", item.AfterValue);

        var linkedWarningIds = Field(outcome.Proposal!, item.TargetId!.Value, AiProposalExplanationFields.SourceWarningIds)!
            .Split(',')
            .Select(Guid.Parse)
            .ToHashSet();

        Assert.Equal(warnings.Select(w => w.Id).ToHashSet(), linkedWarningIds);

        var emitted = Assert.Single(outcome.Proposal!.Warnings, w => w.AiStructuredChangeId == item.Id);
        Assert.Equal(AiWarningKind.Assumption, emitted.Kind);
        Assert.Equal("Assumed a 9-inch pan.", emitted.Message);
    }

    /// <summary>The missing-link refusal: nothing here can link an explanation item to a proposal that is not there.</summary>
    [Fact]
    public async Task A_source_that_no_longer_resolves_to_a_proposal_is_refused()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);

        var outcome = await Handle(scope, Guid.NewGuid());

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    /// <summary>
    /// The same missing-link refusal applies to a source that exists but belongs to another workspace: the
    /// handler's own re-read at execution time relies on the claimed operation's already-resolved workspace
    /// context exactly as the request-time check does, so a proposal in workspace B is invisible to a claim
    /// resolved to workspace A -- indistinguishable from a source that does not exist at all (tenancy.md).
    /// </summary>
    [Fact]
    public async Task A_source_in_another_workspace_is_refused_the_same_way_as_a_missing_one()
    {
        using var seedScope = _provider.CreateScope();
        Resolve(seedScope, WorkspaceB);
        var (requestId, _, _) = Seed(seedScope, workspaceId: WorkspaceB);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);

        var outcome = await Handle(scope, requestId);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    [Fact]
    public async Task An_operation_with_no_declared_source_is_refused_as_a_validation_failure()
    {
        using var scope = _provider.CreateScope();
        var handler = new AiProposalExplanationAiTaskHandler(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await handler.HandleAsync(
            Context(inputs: null), TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static async Task<AiTaskHandlerOutcome> Handle(IServiceScope scope, Guid sourceRequestId)
    {
        var handler = new AiProposalExplanationAiTaskHandler(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        return await handler.HandleAsync(
            Context(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiProposalExplanationInputs.SourceRequestId] = sourceRequestId.ToString(),
            }),
            TestContext.Current.CancellationToken);
    }

    private static AiTaskExecutionContext Context(IReadOnlyDictionary<string, string>? inputs) => new(
        OperationId: Operation,
        WorkspaceId: WorkspaceA,
        LeaseToken: Guid.NewGuid(),
        Scope: AiOperationScope.Advisory,
        RecipeId: null,
        RecipeVersionId: null,
        CorrelationId: Guid.NewGuid(),
        RenewLeaseAsync: _ => Task.CompletedTask,
        Inputs: inputs);

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner);

    private static string? Field(AiProposal proposal, Guid targetId, string field) =>
        proposal.Changes
            .FirstOrDefault(c => c.TargetId == targetId && c.ChangeKind == AiChangeKind.Set && c.FieldName == field)
            ?.AfterValue;

    /// <summary>
    /// Writes a completed source proposal directly. Two-phase when a warning links to a change: the change
    /// must be persisted first so its real, database-assigned id exists to link to.
    /// </summary>
    private static (Guid RequestId, IReadOnlyList<AiStructuredChange> Changes, IReadOnlyList<AiWarning> Warnings) Seed(
        IServiceScope scope,
        IReadOnlyList<AiStructuredChange>? changes = null,
        IReadOnlyList<AiWarning>? generalWarnings = null,
        Func<AiStructuredChange, AiWarning>? linkWarning = null,
        Guid? workspaceId = null)
    {
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = workspaceId ?? WorkspaceA;

        var operation = new AiOperation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace,
            TaskType = AiTaskType.RecipeRevision,
            Scope = AiOperationScope.WholeRecipe,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"source-{Guid.NewGuid()}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        };

        var proposal = new AiProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace,
            AiOperationId = operation.Id,
            OutputSchemaVersion = "test.v1",
            PromptTemplateId = "test",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "none",
            ProviderName = "test",
            ModelName = "test",
            CreatedAt = Now,
        };

        foreach (var change in changes ?? [])
        {
            proposal.Changes.Add(change);
        }

        foreach (var warning in generalWarnings ?? [])
        {
            proposal.Warnings.Add(warning);
        }

        db.AiOperations.Add(operation);
        db.AiProposals.Add(proposal);
        db.SaveChanges();

        if (linkWarning is not null)
        {
            var warning = linkWarning(proposal.Changes.Single());
            proposal.Warnings.Add(warning);
            db.SaveChanges();
        }

        return (operation.Id, proposal.Changes.ToList(), proposal.Warnings.ToList());
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
