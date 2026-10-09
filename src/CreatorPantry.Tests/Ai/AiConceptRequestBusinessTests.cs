using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage;
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
/// AIREC-001's request seam over a real SQLite-backed <see cref="IAiOperationDataLayer"/>: the task-enabled
/// gate, idempotent replay (including the reconciliation fix that compares <c>TaskInputsJson</c>, not just
/// task/scope/recipe), the status read, and two-workspace isolation.
/// </summary>
public sealed class AiConceptRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiConceptRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.RecipeConcepts);

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
            .AddScoped<IAiConceptRequestBusiness, AiConceptRequestBusiness>()
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
        var business = new AiConceptRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IAiRequestQuotaGate>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            tasks,
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiConceptRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- request shape -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_queues_an_operation_with_no_recipe_and_a_not_applicable_scope()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();

        var outcome = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.RecipeConcepts, status.TaskType);
        Assert.Equal(AiOperationScope.NotApplicable, status.Scope);
        Assert.Null(status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
        Assert.Null(status.Proposal);
        Assert.False(outcome.Replayed);
    }

    /// <summary>
    /// The dish name reaches the stored inputs the worker reads back.
    /// </summary>
    /// <remarks>
    /// The one link in this chain with no type across it: the request writes <c>TaskInputsJson</c> and the
    /// handler deserializes it in another process, some time later, so a key written on one side and not read
    /// on the other fails silently — the creator gets concepts for a dish they never named and nothing says
    /// why. <c>RecipeConceptsAiTaskHandlerTests</c> covers the reading half; this is the writing half.
    /// </remarks>
    [Fact]
    public async Task A_dish_name_is_stored_on_the_operation_for_the_worker_to_read()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeConceptsViewModel { DishName = "Fattoush salad with radishes and grilled chicken shawarma" },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operation = await db.AiOperations.AsNoTracking().SingleAsync(
            row => row.Id == outcome.Result.Value!.AiProposalRequestId,
            TestContext.Current.CancellationToken);

        var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;

        Assert.Equal(
            "Fattoush salad with radishes and grilled chicken shawarma",
            inputs[AiBriefInputs.DishName]);
    }

    // ---- idempotency --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_brief_replays_the_original()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();

        var first = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    /// <summary>
    /// The reconciliation fix this feature needed: TaskType/Scope/RecipeId are identical for every concept
    /// request (there is no recipe to vary), so without comparing the brief itself, two different briefs
    /// reusing one key would be misread as the same request.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_a_different_brief_is_refused_rather_than_replayed_or_reissued()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();

        await business.RequestAsync(Brief(cuisine: "Sichuan"), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(
            Brief(cuisine: "Tuscan"), "key-1", TestContext.Current.CancellationToken);

        Assert.False(second.Replayed);
        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    // ---- status read --------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_request_id_is_not_found()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();

        var result = await business.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiConceptRequestErrors.RequestNotFound, result.Error!.Code);
    }

    /// <summary>
    /// A request id belonging to some other task type is not this route's resource, and is refused the same
    /// way an unknown id is -- neither discloses that the other exists.
    /// </summary>
    [Fact]
    public async Task A_request_id_belonging_to_a_different_task_type_is_not_found()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
        var business = scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();

        var diagnostic = await operations.RequestAsync(
            new Domain.Modules.Ai.Data.Entities.AiOperation
            {
                WorkspaceId = WorkspaceA,
                TaskType = AiTaskType.Diagnostic,
                Scope = AiOperationScope.WholeRecipe,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = "diagnostic-key",
                RequestedByMembershipId = Guid.NewGuid(),
                RequestedAt = Now,
                StatusChangedAt = Now,
                AvailableAt = Now,
            },
            TestContext.Current.CancellationToken);

        var result = await business.GetAsync(diagnostic.Operation!.Id, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiConceptRequestErrors.RequestNotFound, result.Error!.Code);
    }

    // ---- workspace isolation -------------------------------------------------------------------------------

    /// <summary>
    /// A request queued in workspace A is invisible to a caller resolved to workspace B -- the global query
    /// filter, exercised through this seam rather than asserted about it directly (tenancy.md).
    /// </summary>
    [Fact]
    public async Task A_request_from_one_workspace_is_not_found_from_another()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var businessA = scopeA.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();
        var queued = await businessA.RequestAsync(Brief(), "key-a", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var businessB = scopeB.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();
        var result = await businessB.GetAsync(
            queued.Result.Value!.AiProposalRequestId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiConceptRequestErrors.RequestNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task Two_workspaces_requesting_concepts_do_not_collide_on_the_same_idempotency_key()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var businessA = scopeA.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();
        var outcomeA = await businessA.RequestAsync(Brief(), "shared-key", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var businessB = scopeB.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>();
        var outcomeB = await businessB.RequestAsync(Brief(), "shared-key", TestContext.Current.CancellationToken);

        Assert.True(outcomeA.Result.Succeeded);
        Assert.True(outcomeB.Result.Succeeded);
        Assert.False(outcomeA.Replayed);
        Assert.False(outcomeB.Replayed);
        Assert.NotEqual(outcomeA.Result.Value!.AiProposalRequestId, outcomeB.Result.Value!.AiProposalRequestId);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static RequestRecipeConceptsViewModel Brief(string cuisine = "Sichuan") => new() { Cuisine = cuisine };

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
