using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>GET .../workflow-runs/{runId}</c> through the real Gateway (13A.6): a run part-way, finished and stopped, the
/// records it holds that have gone, a reader who cannot take the next step, that a read writes nothing, and the
/// boundary between two workspaces. A run pinned to an older version is <see cref="WorkflowRunPinnedVersionEndpointTests"/>.
/// </summary>
public sealed class WorkflowRunReadEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_run_part_way_says_what_is_done_what_was_passed_over_and_where_to_carry_on()
    {
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        var runId = await StartAsync(_fixture.WorkspaceA, new { definitionKey = "recipe.improve", startingResource = new { kind = "Recipe", id = recipeId } });

        // The run as later steps will leave it: one more step done, an optional one passed over, on to the next.
        var at = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);
        await ChangeRunAsync(_fixture.WorkspaceA, runId, (run, steps) =>
        {
            Step(steps, "change").Status = WorkflowStepRunStatus.Completed;
            Step(steps, "change").CompletedAt = at;
            Step(steps, "change").CompletedByMembershipId = run.CreatedByMembershipId;
            Step(steps, "compare").Status = WorkflowStepRunStatus.Skipped;
            Step(steps, "compare").SkippedAt = at;
            Step(steps, "compare").SkippedByMembershipId = run.CreatedByMembershipId;
            Step(steps, "compare").SkipReason = "I know what changed.";
            run.CompletedStepCount = 2;
            run.SkippedStepCount = 1;
            run.CurrentStepKey = "decide";
        });

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{runId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var run = await BodyOf(response);
        Assert.Equal(1, run.GetProperty("definitionVersion").GetInt32());

        // The pinned version's goal and what the creator has at the end: the guided shell's heading and lede (13A.12).
        var improve = WorkflowCatalogue.Find("recipe.improve", 1)!;
        Assert.Equal(improve.Goal, run.GetProperty("goal").GetString());
        Assert.Equal(improve.ExpectedOutcome, run.GetProperty("expectedOutcome").GetString());
        Assert.Equal(2, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(1, run.GetProperty("skippedStepCount").GetInt32());

        var steps = run.GetProperty("steps").EnumerateArray().ToDictionary(step => step.GetProperty("key").GetString()!);
        Assert.Equal("Completed", steps["choose"].GetProperty("status").GetString());
        Assert.Equal("Completed", steps["change"].GetProperty("status").GetString());
        Assert.Equal(at, steps["change"].GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal("Skipped", steps["compare"].GetProperty("status").GetString());
        Assert.Equal("I know what changed.", steps["compare"].GetProperty("skipReason").GetString());
        Assert.Equal("NotStarted", steps["decide"].GetProperty("status").GetString());

        var next = run.GetProperty("nextAction");
        Assert.Equal("decide", next.GetProperty("stepKey").GetString());
        Assert.Equal($"recipes/{recipeId:D}", next.GetProperty("actions")[0].GetProperty("route").GetString());
        Assert.Equal($"recipes/{recipeId:D}", run.GetProperty("resumeRoute").GetString());
        Assert.Empty(run.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Carrying_on_goes_back_to_where_the_creator_last_was()
    {
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        var runId = await StartAsync(_fixture.WorkspaceA, new { definitionKey = "recipe.improve", startingResource = new { kind = "Recipe", id = recipeId } });
        await ChangeRunAsync(_fixture.WorkspaceA, runId, (run, _) => run.LastRoute = $"recipes/{recipeId:D}?area=history");

        var run = await ReadAsync(_fixture.WorkspaceA, runId);

        Assert.Equal($"recipes/{recipeId:D}?area=history", run.GetProperty("lastRoute").GetString());
        Assert.Equal($"recipes/{recipeId:D}?area=history", run.GetProperty("resumeRoute").GetString());
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Abandoned")]
    public async Task A_finished_or_stopped_run_has_nothing_next_and_nowhere_to_resume(string status)
    {
        var runId = await StartAsync(_fixture.WorkspaceA, new { definitionKey = "recipe.create" });
        var at = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
        await ChangeRunAsync(_fixture.WorkspaceA, runId, (run, _) =>
        {
            run.Status = Enum.Parse<WorkflowRunStatus>(status);
            if (run.Status == WorkflowRunStatus.Completed)
            {
                run.CompletedAt = at;
                run.CurrentStepKey = null;
            }
            else
            {
                run.AbandonedAt = at;
            }
        });

        var run = await ReadAsync(_fixture.WorkspaceA, runId);

        Assert.Equal(status, run.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("nextAction").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resumeRoute").ValueKind);
        Assert.Equal(at, run.GetProperty(status == "Completed" ? "completedAt" : "abandonedAt").GetDateTimeOffset());
        Assert.Empty(run.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_record_the_run_holds_that_is_gone_is_a_warning_with_no_title_and_the_run_still_reads()
    {
        var live = await CreateRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var deletedAsset = await SeedAssetAsync(_fixture.WorkspaceA, deleted: true);
        var goneCard = Guid.NewGuid();
        var goneRecipe = Guid.NewGuid();
        var context = Guid.NewGuid();

        // Another workspace's live records, of every kind that can be checked, each with words of its own.
        var theirRecipe = await CreateRecipeAsync(_fixture.WorkspaceB, NeighbourWords);
        var theirAsset = await SeedAssetAsync(_fixture.WorkspaceB, deleted: false);
        var theirCard = await CreateCardAsync(_fixture.WorkspaceB, NeighbourWords);

        var runId = await StartAsync(_fixture.WorkspaceA, new { definitionKey = "recipe.create" });
        await ChangeRunAsync(_fixture.WorkspaceA, runId, (run, _, links) =>
        {
            links.Add(Link(run, WorkflowResourceKind.Recipe, live, "start"));
            links.Add(Link(run, WorkflowResourceKind.Recipe, goneRecipe, null));
            links.Add(Link(run, WorkflowResourceKind.MediaAsset, deletedAsset, null));
            links.Add(Link(run, WorkflowResourceKind.ContentCard, goneCard, null));
            links.Add(Link(run, WorkflowResourceKind.Recipe, theirRecipe, "ingredients"));
            links.Add(Link(run, WorkflowResourceKind.MediaAsset, theirAsset, "method"));
            links.Add(Link(run, WorkflowResourceKind.ContentCard, theirCard, "method"));

            // A kind nothing can check yet is shown, unchecked, rather than failing the read.
            links.Add(Link(run, WorkflowResourceKind.WorkTask, context, null));
        });

        var run = await ReadAsync(_fixture.WorkspaceA, runId);

        Assert.Equal(8, run.GetProperty("links").GetArrayLength());
        var warnings = run.GetProperty("warnings").EnumerateArray()
            .Where(warning => warning.GetProperty("code").GetString() == WorkflowRunWarningCodes.LinkMissing)
            .ToDictionary(warning => warning.GetProperty("resourceId").GetGuid());

        Assert.Equal(
            new HashSet<Guid> { goneRecipe, deletedAsset, goneCard, theirRecipe, theirAsset, theirCard },
            warnings.Keys.ToHashSet());

        // Another workspace's live record reads exactly as one that is gone: same words, same fields, its own id.
        AssertSameWarning(warnings[goneRecipe], warnings[theirRecipe]);
        AssertSameWarning(warnings[deletedAsset], warnings[theirAsset]);
        AssertSameWarning(warnings[goneCard], warnings[theirCard]);

        // And nothing of theirs is in the answer but the ids the run already held.
        Assert.DoesNotContain(NeighbourWords, run.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_that_is_gone_is_not_used_to_fill_an_address()
    {
        var gone = Guid.NewGuid();
        var runId = await StartAsync(_fixture.WorkspaceA, new { definitionKey = "recipe.create" });
        await ChangeRunAsync(_fixture.WorkspaceA, runId, (run, steps, links) =>
        {
            // Even where the creator last was: an address naming the gone record is not one to send them back to.
            run.LastRoute = $"recipes/{gone:D}";
            Step(steps, "start").Status = WorkflowStepRunStatus.Completed;
            Step(steps, "start").CompletedAt = DateTimeOffset.UtcNow;
            Step(steps, "start").CompletedByMembershipId = run.CreatedByMembershipId;
            run.CompletedStepCount = 1;
            run.CurrentStepKey = "ingredients";
            links.Add(Link(run, WorkflowResourceKind.Recipe, gone, "start"));
        });

        var run = await ReadAsync(_fixture.WorkspaceA, runId);

        // No address that leads to "not found"; the warning says why, and the creator can choose again.
        Assert.Equal(JsonValueKind.Null, run.GetProperty("nextAction").GetProperty("actions")[0].GetProperty("route").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resumeRoute").ValueKind);
        Assert.Equal(WorkflowRunWarningCodes.LinkMissing, Assert.Single(run.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_viewer_reads_a_run_and_is_told_someone_else_takes_the_next_step()
    {
        var runId = await StartAsync(_fixture.WorkspaceB, new { definitionKey = "recipe.create" });

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);
        var response = await viewer.GetAsync($"{RunsIn(_fixture.WorkspaceB)}/{runId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);
        Assert.All(
            run.GetProperty("nextAction").GetProperty("actions").EnumerateArray(),
            action => Assert.False(action.GetProperty("youCanDoIt").GetBoolean()));

        var warning = Assert.Single(run.GetProperty("warnings").EnumerateArray());
        Assert.Equal(WorkflowRunWarningCodes.NextStepNeedsRole, warning.GetProperty("code").GetString());
        Assert.Contains("Contributor", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_run_changes_nothing_about_it()
    {
        var runId = await StartAsync(_fixture.WorkspaceA, new { definitionKey = "recipe.create" });
        var before = await SnapshotAsync(_fixture.WorkspaceA, runId);

        var first = await ReadAsync(_fixture.WorkspaceA, runId);
        var second = await ReadAsync(_fixture.WorkspaceA, runId);

        Assert.Equal(before, await SnapshotAsync(_fixture.WorkspaceA, runId));
        Assert.Equal(first.GetProperty("concurrencyToken").GetString(), second.GetProperty("concurrencyToken").GetString());
        Assert.Equal(first.GetProperty("updatedAt").GetDateTimeOffset(), second.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does()
    {
        var theirs = await StartAsync(_fixture.WorkspaceB, new { definitionKey = "recipe.create" });
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var foreign = await ownerA.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{theirs}", Ct);
        var unknown = await ownerA.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, foreignBody.GetProperty("code").GetString());
        Assert.True(
            JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)),
            "an unknown run and another workspace's answered differently");
    }

    // ---- Helpers ----

    private async Task<JsonElement> ReadAsync(SeededWorkspace workspace, Guid runId)
    {
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync($"{RunsIn(workspace)}/{runId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private async Task<Guid> StartAsync(SeededWorkspace workspace, object body)
    {
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.PostAsJsonAsync(RunsIn(workspace), body, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private const string NeighbourWords = "Neighbour's secret loaf";

    private async Task<Guid> CreateRecipeAsync(SeededWorkspace workspace, string title = "Soda bread")
    {
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private async Task<Guid> CreateCardAsync(SeededWorkspace workspace, string title)
    {
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/content-cards", new { title }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("card").GetProperty("id").GetGuid();
    }

    private Task<Guid> SeedAssetAsync(SeededWorkspace workspace, bool deleted) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var asset = SeededMediaAsset.For(workspace.Id);
            if (deleted)
            {
                asset.DeletedAt = DateTimeOffset.UtcNow;
                asset.DeletedByMembershipId = Guid.NewGuid();
            }

            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });

    /// <summary>Two link_missing warnings say the same thing when all but the id they name is equal.</summary>
    private static void AssertSameWarning(JsonElement expected, JsonElement actual)
    {
        static JsonNode WithoutId(JsonElement warning)
        {
            var node = JsonNode.Parse(warning.GetRawText())!.AsObject();
            node.Remove("resourceId");
            node.Remove("stepKey");

            return node;
        }

        Assert.True(JsonNode.DeepEquals(WithoutId(expected), WithoutId(actual)), $"{expected} differs from {actual}");
    }

    private Task ChangeRunAsync(SeededWorkspace workspace, Guid runId, Action<WorkflowRun, List<WorkflowStepRun>> change) =>
        ChangeRunAsync(workspace, runId, (run, steps, _) => change(run, steps));

    /// <summary>Moves a stored run along as later prompts' commands will, so a read can be tested against it.</summary>
    private Task ChangeRunAsync(
        SeededWorkspace workspace, Guid runId, Action<WorkflowRun, List<WorkflowStepRun>, List<WorkflowRunLink>> change) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(row => row.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.Where(step => step.WorkflowRunId == runId).ToListAsync(Ct);
            var links = new List<WorkflowRunLink>();

            change(run, steps, links);
            db.WorkflowRunLinks.AddRange(links);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private Task<string> SnapshotAsync(SeededWorkspace workspace, Guid runId) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.UpdatedAt:O}|{run.CurrentStepKey}|{run.LastRoute}|"
                + string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.StartedAt:O}"));
        });

    private static WorkflowStepRun Step(List<WorkflowStepRun> steps, string key) => steps.Single(step => step.StepKey == key);

    private static WorkflowRunLink Link(WorkflowRun run, WorkflowResourceKind kind, Guid id, string? stepKey) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowRunId = run.Id,
        ResourceKind = kind,
        ResourceId = id,
        StepKey = stepKey,
        LinkedAt = DateTimeOffset.UtcNow,
        LinkedByMembershipId = run.CreatedByMembershipId,
    };

    private static JsonNode Comparable(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");
        node.Remove("correlationId");

        return node;
    }

    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
