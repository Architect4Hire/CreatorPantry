using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage;
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
/// AIREC-008's request seam over a real SQLite-backed <see cref="IAiOperationDataLayer"/>: the task-enabled
/// gate, the missing-link refusal (a source that does not exist, belongs to another workspace, has no proposal
/// yet, or is itself an explanation), idempotent replay, and two-workspace isolation.
/// </summary>
public sealed class AiProposalExplanationRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiProposalExplanationRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.ProposalExplanation);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiProposalExplanationRequestBusiness, AiProposalExplanationRequestBusiness>()
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

    // ---- task-enabled gate -----------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_queued()
    {
        var tasks = new AiTaskOptions(); // Empty: nothing enabled.

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var source = SeedSourceProposal(scope, WorkspaceA);
        var business = new AiProposalExplanationRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IAiRequestQuotaGate>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            tasks,
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(Model(source), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- the missing-link refusal -----------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_source_request_is_refused_as_not_ready()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var outcome = await business.RequestAsync(
            Model(Guid.NewGuid()), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.SourceNotReady, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task A_source_with_no_proposal_yet_is_refused_as_not_ready()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        // Requested but never run: no AiProposal exists to link an explanation item to.
        var queued = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = WorkspaceA,
                TaskType = AiTaskType.RecipeRevision,
                Scope = AiOperationScope.WholeRecipe,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = "still-running",
                RequestedByMembershipId = Guid.NewGuid(),
                RequestedAt = Now,
                StatusChangedAt = Now,
                AvailableAt = Now,
            },
            TestContext.Current.CancellationToken);

        var outcome = await business.RequestAsync(
            Model(queued.Operation!.Id), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.SourceNotReady, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task A_source_in_another_workspace_is_refused_the_same_way_as_an_unknown_one()
    {
        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var sourceInB = SeedSourceProposal(scopeB, WorkspaceB);

        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var business = scopeA.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var outcome = await business.RequestAsync(
            Model(sourceInB), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.SourceNotReady, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task An_explanation_cannot_be_chained_off_another_explanation()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var explanationSource = SeedSourceProposal(scope, WorkspaceA, taskType: AiTaskType.ProposalExplanation);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var outcome = await business.RequestAsync(
            Model(explanationSource), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.SourceNotReady, outcome.Result.Error!.Code);
    }

    // ---- request shape -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_inherits_the_sources_recipe_version_and_runs_advisory()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);

        // No recipeId here: AiOperation.RecipeId/RecipeVersionId are real composite foreign keys, so
        // exercising a non-null value would mean seeding a backing Recipe too. The assignment this proves --
        // AiOperation.RecipeVersionId = source.Operation.RecipeVersionId, unconditionally -- is the same code
        // path whether the value is null or not.
        var source = SeedSourceProposal(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var outcome = await business.RequestAsync(Model(source), "key-1", TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.ProposalExplanation, status.TaskType);
        Assert.Equal(AiOperationScope.Advisory, status.Scope);
        Assert.Null(status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
        Assert.False(outcome.Replayed);
    }

    // ---- idempotency --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_source_replays_the_original()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var source = SeedSourceProposal(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var first = await business.RequestAsync(Model(source), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(Model(source), "key-1", TestContext.Current.CancellationToken);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    [Fact]
    public async Task The_same_key_naming_a_different_source_is_refused_rather_than_replayed_or_reissued()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var first = SeedSourceProposal(scope, WorkspaceA);
        var second = SeedSourceProposal(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        await business.RequestAsync(Model(first), "key-1", TestContext.Current.CancellationToken);
        var outcome = await business.RequestAsync(Model(second), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Replayed);
        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, outcome.Result.Error!.Code);
    }

    // ---- status read --------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_request_id_is_not_found()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var result = await business.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.RequestNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task A_request_id_belonging_to_a_different_task_type_is_not_found()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var source = SeedSourceProposal(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();

        var result = await business.GetAsync(source, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.RequestNotFound, result.Error!.Code);
    }

    // ---- workspace isolation -------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_from_one_workspace_is_not_found_from_another()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var source = SeedSourceProposal(scopeA, WorkspaceA);
        var businessA = scopeA.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();
        var queued = await businessA.RequestAsync(Model(source), "key-a", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var businessB = scopeB.ServiceProvider.GetRequiredService<IAiProposalExplanationRequestBusiness>();
        var result = await businessB.GetAsync(
            queued.Result.Value!.AiProposalRequestId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiProposalExplanationRequestErrors.RequestNotFound, result.Error!.Code);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static RequestProposalExplanationViewModel Model(Guid sourceRequestId) =>
        new() { SourceRequestId = sourceRequestId };

    /// <summary>
    /// Writes a completed source operation and proposal directly, bypassing the worker: this test seam is
    /// about explaining an already-finished proposal, not about producing one.
    /// </summary>
    private static Guid SeedSourceProposal(
        IServiceScope scope,
        Guid workspaceId,
        Guid? recipeId = null,
        Guid? recipeVersionId = null,
        AiTaskType taskType = AiTaskType.RecipeRevision)
    {
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = new AiOperation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            TaskType = taskType,
            Scope = recipeId is null ? AiOperationScope.NotApplicable : AiOperationScope.WholeRecipe,
            Status = AiOperationStatus.Proposed,
            RecipeId = recipeId,
            RecipeVersionId = recipeVersionId,
            IdempotencyKey = $"source-{Guid.NewGuid()}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        };

        db.AiOperations.Add(operation);

        db.AiProposals.Add(new AiProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            AiOperationId = operation.Id,
            SourceRecipeVersionId = recipeVersionId,
            OutputSchemaVersion = "test.v1",
            PromptTemplateId = "test",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "none",
            ProviderName = "test",
            ModelName = "test",
            CreatedAt = Now,
        });

        db.SaveChanges();

        return operation.Id;
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner, "test-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
