using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// A workspace's AI feature switches over a real SQLite-backed seam: what a read reports before and after an
/// Owner chooses, that the workspace's answer beats the deployment default in both directions at the gate a
/// real request passes through, the role bar, the validator, the audit record, and two-workspace isolation.
/// </summary>
public sealed class AiFeatureTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiFeatureTests()
    {
        _connection.Open();

        // The deployment default: concepts on, everything else off — dish facets and revision included.
        var defaults = new AiTaskOptions();
        defaults.Enabled.Add(AiTaskCatalog.RecipeConcepts);
        defaults.Enabled.Add(AiTaskCatalog.Diagnostic);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(defaults)
            .AddAiTaskGate()
            .AddAiFeatureSeam()
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- reading -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_that_has_not_chosen_reports_the_deployment_default_for_every_switchable_task()
    {
        var features = await GetAsync(WorkspaceA);

        Assert.Equal(AiFeaturePolicy.SwitchableTasks, features.Select(feature => feature.Task));
        Assert.All(features, feature => Assert.False(feature.SetByWorkspace));
        Assert.Equal([AiTaskCatalog.RecipeConcepts], Enabled(features));
    }

    [Fact]
    public async Task The_diagnostic_task_is_never_offered_even_when_the_deployment_has_it_on()
    {
        Assert.DoesNotContain(AiTaskCatalog.Diagnostic, (await GetAsync(WorkspaceA)).Select(feature => feature.Task));
    }

    // ---- replacing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_replace_stores_an_answer_for_every_task_and_a_read_returns_it()
    {
        var result = await ReplaceAsync(WorkspaceA, AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview);

        Assert.True(result.Succeeded);
        Assert.Equal([AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview], Enabled(result.Value!.Features));
        Assert.All(result.Value.Features, feature => Assert.True(feature.SetByWorkspace));

        // Concepts was on by default and was not named, so it is now off: the saved set is the whole answer.
        var read = await GetAsync(WorkspaceA);
        Assert.Equal([AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview], Enabled(read));
    }

    [Fact]
    public async Task Replacing_again_updates_the_same_rows_rather_than_adding_more()
    {
        await ReplaceAsync(WorkspaceA, AiTaskCatalog.RecipeRevision);
        await ReplaceAsync(WorkspaceA, AiTaskCatalog.RecipeAdaptation);
        await ReplaceAsync(WorkspaceA);

        Assert.Empty(Enabled(await GetAsync(WorkspaceA)));

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var rows = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .WorkspaceAiTaskSwitches.CountAsync(Ct);
        Assert.Equal(AiFeaturePolicy.SwitchableTasks.Count, rows);
    }

    [Fact]
    public async Task A_task_is_stored_in_the_catalogues_spelling_however_the_request_wrote_it()
    {
        var result = await ReplaceAsync(WorkspaceA, "  Recipe.REVISION ");

        Assert.True(result.Succeeded);
        Assert.Equal([AiTaskCatalog.RecipeRevision], Enabled(result.Value!.Features));
        Assert.True(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeRevision));
    }

    [Fact]
    public async Task A_replace_is_audited_with_counts_and_no_task_names()
    {
        await ReplaceAsync(WorkspaceA, AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var audit = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AuditLogs.SingleAsync(log => log.Action == AiFeatureAuditActions.Replaced, Ct);

        Assert.Equal("owner-account", audit.ActorUserId);
        Assert.Equal("1", audit.BeforeReference);
        Assert.Equal("2", audit.AfterReference);
        Assert.DoesNotContain("recipe.", audit.Summary);
    }

    // ---- the role bar and the validator ------------------------------------------------------------------

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Contributor)]
    [InlineData(WorkspaceRole.Editor)]
    public async Task Anyone_below_Owner_may_read_but_not_replace(WorkspaceRole role)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, role);
        var facade = scope.ServiceProvider.GetRequiredService<IAiFeatureFacade>();

        Assert.True((await facade.GetAsync(Ct)).Succeeded);

        var refused = await facade.ReplaceAsync(
            new ReplaceAiFeaturesViewModel { Enabled = [AiTaskCatalog.RecipeRevision] }, Ct);

        Assert.Equal(AiFeatureErrors.Forbidden, refused.Error!.Code);
        Assert.False(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeRevision));
    }

    [Theory]
    [InlineData("diagnostic")]
    [InlineData("recipe.no-such-task")]
    [InlineData("")]
    public async Task A_name_that_is_not_a_switchable_feature_is_refused_and_nothing_is_written(string task)
    {
        var result = await ReplaceAsync(WorkspaceA, AiTaskCatalog.RecipeRevision, task);

        Assert.Equal(AiFeatureErrors.Invalid, result.Error!.Code);
        Assert.Contains("enabled[1]", result.Error.FieldErrors.Keys);
        Assert.All(await GetAsync(WorkspaceA), feature => Assert.False(feature.SetByWorkspace));
    }

    [Fact]
    public async Task A_missing_list_and_a_repeated_task_are_each_refused()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IAiFeatureFacade>();

        var missing = await facade.ReplaceAsync(new ReplaceAiFeaturesViewModel(), Ct);
        Assert.Equal(AiFeatureErrors.Invalid, missing.Error!.Code);

        var repeated = await facade.ReplaceAsync(
            new ReplaceAiFeaturesViewModel { Enabled = [AiTaskCatalog.RecipeRevision, "RECIPE.revision"] }, Ct);
        Assert.Equal(AiFeatureErrors.Invalid, repeated.Error!.Code);
        Assert.Contains("enabled[1]", repeated.Error.FieldErrors.Keys);
    }

    // ---- the gate a request passes through ---------------------------------------------------------------

    [Fact]
    public async Task The_workspaces_answer_beats_the_deployment_default_in_both_directions()
    {
        Assert.True(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeConcepts));
        Assert.False(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeRevision));

        await ReplaceAsync(WorkspaceA, AiTaskCatalog.RecipeRevision);

        // On here although the deployment leaves it off, and off here although the deployment has it on.
        Assert.True(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeRevision));
        Assert.False(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeConcepts));
    }

    [Fact]
    public async Task The_diagnostic_task_stays_the_deployments_decision()
    {
        await ReplaceAsync(WorkspaceA);

        Assert.True(await GateAsync(WorkspaceA, AiTaskCatalog.Diagnostic));
    }

    [Fact]
    public async Task A_request_refused_by_default_is_queued_once_the_Owner_switches_its_feature_on()
    {
        var before = await RequestDishFacetsAsync(WorkspaceA, "key-1");
        Assert.Equal(AiDishFacetsRequestErrors.TaskNotEnabled, before.Result.Error!.Code);

        await ReplaceAsync(WorkspaceA, AiTaskCatalog.DishFacetSuggestion);

        var after = await RequestDishFacetsAsync(WorkspaceA, "key-2");
        Assert.True(after.Result.Succeeded);
    }

    // ---- two workspaces ----------------------------------------------------------------------------------

    [Fact]
    public async Task One_workspaces_choice_changes_nothing_another_reads_or_may_request()
    {
        await ReplaceAsync(WorkspaceA, AiTaskCatalog.DishFacetSuggestion, AiTaskCatalog.RecipeRevision);

        // B still reads the default, with nothing marked as chosen.
        var inB = await GetAsync(WorkspaceB);
        Assert.Equal([AiTaskCatalog.RecipeConcepts], Enabled(inB));
        Assert.All(inB, feature => Assert.False(feature.SetByWorkspace));

        // And B's requests are gated by B's answer, not A's.
        Assert.False(await GateAsync(WorkspaceB, AiTaskCatalog.RecipeRevision));
        var refused = await RequestDishFacetsAsync(WorkspaceB, "key-b");
        Assert.Equal(AiDishFacetsRequestErrors.TaskNotEnabled, refused.Result.Error!.Code);

        // B choosing the opposite leaves A exactly where A put it.
        await ReplaceAsync(WorkspaceB, AiTaskCatalog.RecipeReview);
        Assert.Equal(
            [AiTaskCatalog.DishFacetSuggestion, AiTaskCatalog.RecipeRevision], Enabled(await GetAsync(WorkspaceA)));
        Assert.False(await GateAsync(WorkspaceA, AiTaskCatalog.RecipeReview));

        // The same task holds one row in each workspace, and neither read sees the other's.
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceB);
        var visible = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .WorkspaceAiTaskSwitches.Select(row => row.WorkspaceId).Distinct().ToListAsync(Ct);
        Assert.Equal([WorkspaceB], visible);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static IEnumerable<string> Enabled(IEnumerable<AiFeatureServiceModel> features) =>
        features.Where(feature => feature.Enabled).Select(feature => feature.Task);

    private async Task<IReadOnlyList<AiFeatureServiceModel>> GetAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return (await scope.ServiceProvider.GetRequiredService<IAiFeatureFacade>().GetAsync(Ct)).Value!.Features;
    }

    private async Task<Domain.Managers.Results.OperationResult<AiFeaturesServiceModel>> ReplaceAsync(
        Guid workspaceId, params string[] enabled)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiFeatureFacade>()
            .ReplaceAsync(new ReplaceAiFeaturesViewModel { Enabled = enabled }, Ct);
    }

    private async Task<bool> GateAsync(Guid workspaceId, string task)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiTaskGate>().IsEnabledAsync(task, Ct);
    }

    private async Task<Domain.Managers.Idempotency.IdempotentOutcome<AiProposalStatusServiceModel>> RequestDishFacetsAsync(
        Guid workspaceId, string key)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiDishFacetsRequestBusiness>()
            .RequestAsync(new RequestDishFacetsViewModel { DishName = "Fattoush with grilled chicken" }, key, Ct);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId, WorkspaceRole role = WorkspaceRole.Owner) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            role,
            "owner-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
