using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// A run pinned to an older version of its workflow (13A.6): it reads as it began and is told a newer one exists,
/// while a new run starts on the newest. The shipped catalogue has only ever had one version, so this host serves
/// one with a second version of <c>recipe.create</c> beside the first.
/// </summary>
public sealed class WorkflowRunPinnedVersionEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
            services.AddSingleton<IWorkflowCatalogue>(new CatalogueWithASecondVersion()));

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-runs";

    [Fact]
    public void The_catalogue_it_serves_is_itself_valid()
    {
        Assert.Empty(WorkflowCatalogueValidator.Validate(new CatalogueWithASecondVersion().All));
    }

    [Fact]
    public async Task A_run_on_the_first_version_reads_as_it_began_and_is_told_a_newer_one_exists()
    {
        var runId = await SeedVersionOneRunAsync(WorkflowRunStatus.InProgress);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var run = await (await client.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{runId}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1, run.GetProperty("definitionVersion").GetInt32());
        Assert.Equal("Create a recipe", run.GetProperty("goal").GetString());
        Assert.Equal(5, run.GetProperty("steps").GetArrayLength());
        Assert.Equal("Start your recipe", run.GetProperty("steps")[0].GetProperty("title").GetString());
        Assert.Equal("Start your recipe", run.GetProperty("nextAction").GetProperty("title").GetString());

        var warning = Assert.Single(run.GetProperty("warnings").EnumerateArray());
        Assert.Equal(WorkflowRunWarningCodes.NewerVersion, warning.GetProperty("code").GetString());
        Assert.Equal(2, warning.GetProperty("latestVersion").GetInt32());

        // And it is still the first version afterwards: reading never moves a run.
        Assert.Equal(1, await InWorkspaceAsync(db => db.WorkflowRuns.Where(row => row.Id == runId).Select(row => row.DefinitionVersion).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_finished_run_on_the_first_version_is_not_told_about_the_newer_one()
    {
        var runId = await SeedVersionOneRunAsync(WorkflowRunStatus.Completed);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var run = await (await client.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{runId}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1, run.GetProperty("definitionVersion").GetInt32());
        Assert.Empty(run.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_new_run_starts_on_the_newest_version()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await client.PostAsJsonAsync(RunsIn(_fixture.WorkspaceA), new { definitionKey = "recipe.create" }, Guid.NewGuid().ToString(), Ct);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, run.GetProperty("definitionVersion").GetInt32());
        Assert.Equal("Make a recipe", run.GetProperty("goal").GetString());
        Assert.Equal("Begin your recipe", run.GetProperty("nextAction").GetProperty("title").GetString());
    }

    /// <summary>A run as one started on version 1 before version 2 shipped would have been stored.</summary>
    private Task<Guid> SeedVersionOneRunAsync(WorkflowRunStatus status) =>
        InWorkspaceAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var completed = status == WorkflowRunStatus.Completed;
            var run = new WorkflowRun
            {
                Id = Guid.NewGuid(),
                DefinitionKey = WorkflowCatalogue.RecipeCreateKey,
                DefinitionVersion = 1,
                Title = "Create a recipe",
                Status = status,
                CurrentStepKey = completed ? null : "start",
                StepCount = WorkflowCatalogue.RecipeCreateV1.Steps.Count,
                CreatedByMembershipId = Guid.NewGuid(),
                StartedAt = now,
                UpdatedAt = now,
                CompletedAt = completed ? now : null,
            };
            db.WorkflowRuns.Add(run);
            db.WorkflowStepRuns.AddRange(WorkflowCatalogue.RecipeCreateV1.Steps.Select(step => new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = run.Id,
                StepKey = step.Key.Value,
                Status = WorkflowStepRunStatus.NotStarted,
            }));
            await db.SaveChangesAsync(Ct);

            return run.Id;
        });

    private async Task<T> InWorkspaceAsync<T>(Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>The shipped catalogue plus a second version of <c>recipe.create</c> whose copy differs.</summary>
    private sealed class CatalogueWithASecondVersion : IWorkflowCatalogue
    {
        private static readonly WorkflowDefinition RecipeCreateV2 = WorkflowCatalogue.RecipeCreateV1 with
        {
            Version = 2,
            Goal = "Make a recipe",
            Steps =
            [
                WorkflowCatalogue.RecipeCreateV1.Steps[0] with { Title = "Begin your recipe" },
                .. WorkflowCatalogue.RecipeCreateV1.Steps.Skip(1),
            ],
        };

        public IReadOnlyList<WorkflowDefinition> All { get; } = [.. WorkflowCatalogue.All, RecipeCreateV2];

        public WorkflowDefinition? Find(WorkflowKey key, int version) =>
            All.FirstOrDefault(definition => definition.Key == key && definition.Version == version);

        public IReadOnlyList<WorkflowDefinition> Latest() =>
            [.. All.GroupBy(definition => definition.Key).Select(family => family.MaxBy(definition => definition.Version)!)];
    }
}
