using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-003's request seam over a real SQLite-backed data layer and the real recipe module: the enabled
/// gate, the recipe and pinned-version checks, what is stored on the operation, and two-workspace isolation.
/// </summary>
public sealed class AiRevisionRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiRevisionRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.RecipeRevision);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiRevisionRequestBusiness, AiRevisionRequestBusiness>()
            .AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
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

    // ---- the enabled gate --------------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_queued()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = new AiRevisionRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new AiTaskOptions(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(
            recipe.RecipeId, Request(recipe.VersionId), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiRevisionRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- the recipe and its pinned version ---------------------------------------------------------------

    [Fact]
    public async Task A_request_queues_an_operation_against_the_pinned_version()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.RecipeRevision, status.TaskType);
        Assert.Equal(AiOperationScope.Metadata, status.Scope);
        Assert.Equal(recipe.VersionId, status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA,
            new SeededRecipe(Guid.NewGuid(), Guid.NewGuid()),
            Request(Guid.NewGuid()));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiRevisionRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// The stale-version case. A revision computed against a version the recipe has moved past would describe
    /// content that is no longer there, so it is refused at request time rather than discovered at acceptance
    /// — which is where a creator would have already reviewed a diff of a recipe that no longer exists.
    /// </summary>
    [Fact]
    public async Task A_version_that_is_not_the_current_one_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var stale = recipe.VersionId;

        await EditAsync(WorkspaceA, recipe.RecipeId);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(stale));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiRevisionRequestErrors.SourceVersionInvalid, outcome.Result.Error!.Code);
    }

    /// <summary>And the version the edit produced is the one a fresh request may name.</summary>
    [Fact]
    public async Task The_current_version_after_an_edit_is_accepted()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        await EditAsync(WorkspaceA, recipe.RecipeId);

        var current = await CurrentVersionAsync(WorkspaceA, recipe.RecipeId);
        var outcome = await RequestAsync(WorkspaceA, recipe, Request(current));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
    }

    // ---- what is stored ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_creators_goal_is_stored_in_the_vocabulary_the_handler_reads()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: "Make the headnote warmer"));

        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Equal("Make the headnote warmer", inputs[AiRevisionInputs.Goal]);
    }

    /// <summary>
    /// The scope lives on the operation's own column, which is what the validator reads when the answer comes
    /// back. Storing it in the inputs as well would give a later reader two places to disagree.
    /// </summary>
    [Fact]
    public async Task The_scope_is_not_duplicated_into_the_stored_inputs()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));
        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.DoesNotContain("scope", inputs.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_request_with_no_goal_stores_no_goal()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId, goal: null));
        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Empty(inputs);
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_request_replays()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var first = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId), "key-1");
        var second = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId), "key-1");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    /// <summary>
    /// The same key naming a different section is a different request, which is what the scope column being
    /// part of the reconciliation comparison buys.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_a_different_section_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId), "key-1");
        var second = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, scope: AiOperationScope.Instructions), "key-1");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task The_same_key_with_a_different_goal_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId, goal: "Shorter"), "key-1");
        var second = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: "Longer"), "key-1");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    // ---- the status read ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_request_can_be_read_back()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        var read = await GetAsync(WorkspaceA, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.True(read.Succeeded, read.Error?.Message);
        Assert.Equal(AiOperationStatus.Requested, read.Value!.Status);
    }

    /// <summary>A request read through a different recipe's route is not that route's resource.</summary>
    [Fact]
    public async Task A_request_read_under_the_wrong_recipe_is_not_found()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var other = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        var read = await GetAsync(WorkspaceA, other.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiRevisionRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// Workspace B naming workspace A's recipe. The query filter makes it invisible, and the seam reports it
    /// exactly as it reports a recipe that never existed.
    /// </summary>
    [Fact]
    public async Task A_recipe_in_another_workspace_cannot_be_revised()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceB, recipe, Request(recipe.VersionId));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiRevisionRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task A_revision_request_from_one_workspace_is_not_found_from_another()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        var read = await GetAsync(WorkspaceB, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiRevisionRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId);

    private static RequestRecipeRevisionViewModel Request(
        Guid versionId,
        AiOperationScope scope = AiOperationScope.Metadata,
        string? goal = "Make it punchier") =>
        new() { Scope = scope, SourceVersionId = versionId, Goal = goal };

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId, SeededRecipe recipe, RequestRecipeRevisionViewModel model, string key = "key-1")
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiRevisionRequestBusiness>()
            .RequestAsync(recipe.RecipeId, model, key, TestContext.Current.CancellationToken);
    }

    private async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid workspaceId, Guid recipeId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiRevisionRequestBusiness>()
            .GetAsync(recipeId, requestId, TestContext.Current.CancellationToken);
    }

    private async Task<SeededRecipe> SeedRecipeAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var created = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>().CreateAsync(
            UserId,
            new CreateRecipeViewModel { Title = "Weeknight Mapo Tofu", Headnote = "As written." },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        return new SeededRecipe(created.Result.Value!.RecipeId, created.Result.Value.VersionId);
    }

    /// <summary>An ordinary creator edit, which is what makes the version a request pinned stale.</summary>
    private async Task EditAsync(Guid workspaceId, Guid recipeId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();
        var detail = await recipes.GetDetailAsync(recipeId, TestContext.Current.CancellationToken);

        var updated = await recipes.UpdateAsync(
            UserId,
            recipeId,
            new UpdateRecipeViewModel
            {
                ExpectedConcurrencyToken = detail.Value!.ConcurrencyToken,
                Headnote = Domain.Managers.Patching.PatchField<string?>.Submitted("Rewritten by the creator."),
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(updated.Result.Succeeded, updated.Result.Error?.Message);
    }

    private async Task<Guid> CurrentVersionAsync(Guid workspaceId, Guid recipeId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var detail = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>()
            .GetDetailAsync(recipeId, TestContext.Current.CancellationToken);

        return detail.Value!.CurrentVersion!.Id;
    }

    private async Task<Dictionary<string, string>> LoadInputsAsync(Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = await db.AiOperations.AsNoTracking()
            .SingleAsync(row => row.Id == operationId, TestContext.Current.CancellationToken);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;
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
