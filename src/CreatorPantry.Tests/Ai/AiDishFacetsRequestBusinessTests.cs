using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
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
/// The dish-facet request seam over a real SQLite-backed <see cref="IAiOperationDataLayer"/>: the
/// task-enabled gate, the queued operation's shape, the stored input the worker reads back, idempotent
/// replay, and two-workspace isolation.
/// </summary>
public sealed class AiDishFacetsRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private const string Fattoush = "Fatoosh Salad with Radishes and Grilled Chicken Schwarma";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiDishFacetsRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.DishFacetSuggestion);

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
            .AddScoped<IAiDishFacetsRequestBusiness, AiDishFacetsRequestBusiness>()
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

    // ---- task-enabled gate -------------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_queued()
    {
        var tasks = new AiTaskOptions(); // Empty: nothing enabled.

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = new AiDishFacetsRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IAiRequestQuotaGate>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            tasks,
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiDishFacetsRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- request shape -----------------------------------------------------------------------------------

    /// <summary>
    /// The queued row names no recipe and no part of one, which is what the capability is.
    /// </summary>
    /// <remarks>
    /// Asserted on the stored row rather than taken on trust from the controller: the scope is the server's to
    /// fix, and a row claiming to address <c>WholeRecipe</c> would say something untrue about an operation that
    /// reads a name and changes nothing.
    /// </remarks>
    [Fact]
    public async Task A_request_queues_an_operation_with_no_recipe_and_a_not_applicable_scope()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();

        var outcome = await business.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.DishFacetSuggestion, status.TaskType);
        Assert.Equal(AiOperationScope.NotApplicable, status.Scope);
        Assert.Null(status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
        Assert.Null(status.Proposal);
        Assert.False(outcome.Replayed);
    }

    /// <summary>
    /// The name reaches the stored inputs the worker reads back, under the key the handler looks for.
    /// </summary>
    /// <remarks>
    /// The one link in this chain with no type across it: the request writes <c>TaskInputsJson</c> and the
    /// handler deserializes it in another process, some time later. A key written on one side and not read on
    /// the other fails silently — the worker refuses for want of a name that was supplied — so the two halves
    /// are pinned from both ends. <c>DishFacetSuggestionAiTaskHandlerTests</c> covers the reading half.
    /// </remarks>
    [Fact]
    public async Task The_dish_name_is_stored_trimmed_for_the_worker_to_read()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestDishFacetsViewModel { DishName = $"  {Fattoush}  " },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        using var reading = _provider.CreateScope();
        Resolve(reading, WorkspaceA);
        var db = reading.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operation = await db.AiOperations.SingleAsync(TestContext.Current.CancellationToken);

        var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;

        Assert.Equal(Fattoush, inputs[AiDishFacetInputs.DishName]);

        // One key, and only one: the candidate list and the creator's other answers are deliberately not sent.
        Assert.Single(inputs);
    }

    // ---- idempotency and isolation -----------------------------------------------------------------------

    /// <summary>
    /// The same key and the same name replays rather than queueing a second reading.
    /// </summary>
    /// <remarks>
    /// Matters more here than on the capabilities a creator presses a button for: the surface asks as a name is
    /// filled in, so the same request arriving twice is ordinary rather than exceptional.
    /// </remarks>
    [Fact]
    public async Task The_same_key_and_name_replays_the_first_request()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();

        var first = await business.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);

        Assert.True(second.Replayed);
        Assert.Equal(
            first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    /// <summary>
    /// The same key with a different name is a different request, and is refused rather than replayed.
    /// </summary>
    /// <remarks>
    /// The name is the entire input, so this is the only way the reconciliation that compares
    /// <c>TaskInputsJson</c> can be exercised for this capability — without it, a creator who corrected their
    /// dish name while a retry was in flight would be handed a reading of the name they replaced.
    /// </remarks>
    [Fact]
    public async Task The_same_key_with_a_different_name_is_refused()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();

        await business.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(
            Named("Tabbouleh"), "key-1", TestContext.Current.CancellationToken);

        Assert.False(second.Result.Succeeded);
    }

    [Fact]
    public async Task A_request_made_in_one_workspace_cannot_be_read_from_the_other()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var businessA = scopeA.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();
        var queued = await businessA.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var businessB = scopeB.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();

        var result = await businessB.GetAsync(
            queued.Result.Value!.AiProposalRequestId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDishFacetsRequestErrors.RequestNotFound, result.Error!.Code);
    }

    /// <summary>
    /// An operation of another task type is not this route's resource, and reads as absent.
    /// </summary>
    /// <remarks>
    /// Indistinguishable from a request that never existed, deliberately: the status route must not become a
    /// way to enumerate which capabilities a workspace has been using.
    /// </remarks>
    [Fact]
    public async Task An_operation_of_another_task_type_reads_as_absent()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);

        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
        var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceContext>();

        var other = await operations.RequestAsync(
            new Domain.Modules.Ai.Data.Entities.AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.RecipeConcepts,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = "other-key",
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = Now,
                StatusChangedAt = Now,
                AvailableAt = Now,
            },
            TestContext.Current.CancellationToken);

        var business = scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();
        var result = await business.GetAsync(
            other.Operation!.Id, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDishFacetsRequestErrors.RequestNotFound, result.Error!.Code);
    }

    /// <summary>
    /// A dish-facet reading cannot be dispositioned against a recipe through the generic proposal route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things stop a stored reading reaching a recipe field, and this pins the one that actually
    /// carries the weight. <c>AiChangeTargetKind.DishFacetSuggestion</c> is absent from
    /// <c>AiChangeApplicability</c>, and the task needs its own route — but the generic disposition guard
    /// tests for <c>AiOperationScope.Advisory</c>, and this task's scope is <c>NotApplicable</c>, so that
    /// guard does not cover it. What does is the null <c>RecipeId</c>, which no recipe id can match.
    /// </para>
    /// <para>
    /// Incidental protection is exactly the kind that disappears in a refactor, so it is asserted rather than
    /// reasoned about: a reading must not become an edit to a recipe, and the null id is why it cannot.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_reading_cannot_be_dispositioned_against_any_recipe()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();

        var queued = await business.RequestAsync(Named(), "key-1", TestContext.Current.CancellationToken);
        var requestId = queued.Result.Value!.AiProposalRequestId;

        using var reading = _provider.CreateScope();
        Resolve(reading, WorkspaceA);
        var operation = await reading.ServiceProvider.GetRequiredService<IAiOperationDataLayer>()
            .GetWithProposalAsync(requestId, TestContext.Current.CancellationToken);

        // The barrier itself: no recipe is named, so no recipe id a disposition could carry will ever match.
        Assert.NotNull(operation);
        Assert.Null(operation.Operation.RecipeId);
        Assert.Null(operation.Operation.RecipeVersionId);

        // And the rows it will produce address a target no change kind can reach.
        Assert.Empty(AiChangeApplicability.For(AiChangeTargetKind.DishFacetSuggestion));
        Assert.DoesNotContain(AiChangeTargetKind.DishFacetSuggestion, AiChangeApplicability.Targets);
    }

    [Fact]
    public async Task Two_workspaces_reading_a_name_do_not_collide_on_the_same_idempotency_key()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var businessA = scopeA.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();
        var outcomeA = await businessA.RequestAsync(
            Named(), "shared-key", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var businessB = scopeB.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>();
        var outcomeB = await businessB.RequestAsync(
            Named(), "shared-key", TestContext.Current.CancellationToken);

        Assert.True(outcomeA.Result.Succeeded);
        Assert.True(outcomeB.Result.Succeeded);
        Assert.False(outcomeA.Replayed);
        Assert.False(outcomeB.Replayed);
        Assert.NotEqual(outcomeA.Result.Value!.AiProposalRequestId, outcomeB.Result.Value!.AiProposalRequestId);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static RequestDishFacetsViewModel Named(string dishName = Fattoush) => new() { DishName = dishName };

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
