using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs/{runId}/completion</c> through the real Gateway (13A.10a): a run finishes only when every
/// required step is done and its rule still holds, nothing the client says counts, nothing it linked is touched, a
/// repeat writes nothing, who may, and two workspaces.
/// </summary>
public sealed class WorkflowRunCompletionEndpointTests : IAsyncLifetime
{
    private const string MemberPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private readonly StoreSwitch _store = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real data layer, behind a switch that makes its next save lose a race.
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IWorkflowRunDataLayer));
            services.Remove(real);
            services.Add(new ServiceDescriptor(
                typeof(IWorkflowRunDataLayer),
                provider => new SwitchedDataLayer(
                    (IWorkflowRunDataLayer)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), _store),
                real.Lifetime));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RunsIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    // ---- Success ----

    [Fact]
    public async Task A_run_with_every_required_step_done_finishes_and_leaves_everything_it_touched_as_it_was()
    {
        using var client = await OwnerAsync();
        var (runId, assetId) = await StartFromPictureAsync(client);

        // An optional step left part-way, with a draft: finishing leaves it exactly so.
        var draftId = Guid.NewGuid();
        await ChangeRunAsync(runId, (_, steps) =>
        {
            var describe = steps.Single(step => step.StepKey == "describe");
            describe.Status = WorkflowStepRunStatus.InProgress;
            describe.StartedAt = DateTimeOffset.UtcNow;
            describe.DraftResourceKind = WorkflowResourceKind.MediaAsset;
            describe.DraftResourceId = draftId;
            describe.DraftSavedAt = DateTimeOffset.UtcNow;
        });
        var stepsBefore = await StepsAndLinksAsync(runId);
        var pictureBefore = await PictureAsync(assetId);

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("abandonedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("nextAction").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resumeRoute").ValueKind);
        Assert.False(run.GetProperty("readyToComplete").GetBoolean());
        Assert.Null(await InWorkspaceAsync(db => db.WorkflowRuns.Where(item => item.Id == runId).Select(item => item.CurrentStepKey).SingleAsync(Ct)));

        // No step, link or linked record moved.
        Assert.Equal(stepsBefore, await StepsAndLinksAsync(runId));
        Assert.Equal(pictureBefore, await PictureAsync(assetId));
        var describe = run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == "describe");
        Assert.Equal("InProgress", describe.GetProperty("status").GetString());
        Assert.Equal(draftId, describe.GetProperty("draftResourceId").GetGuid());
    }

    [Fact]
    public async Task A_run_whose_rules_are_read_from_the_recipe_finishes_when_the_recipe_says_so()
    {
        // A saved change during the run, and no suggestion or safety note left waiting — both the recipe module's
        // answer, not the run's record of it.
        using var client = await OwnerAsync();
        var runId = await StartImprovingDoneAsync(client);
        await ChangeRunAsync(runId, (run, _) => run.StartedAt = DateTimeOffset.UtcNow.AddHours(-1));

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Completed", (await BodyOf(response)).GetProperty("status").GetString());
    }

    // ---- Incomplete ----

    [Fact]
    public async Task A_step_recorded_done_whose_rule_no_longer_holds_is_not_done()
    {
        // The step says completed; the recipe says no change was saved since the run began. The recipe wins.
        using var client = await OwnerAsync();
        var runId = await StartImprovingDoneAsync(client);
        var before = await SnapshotAsync(runId);

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var change = Assert.Single((await BodyOf(response)).GetProperty("incompleteSteps").EnumerateArray());
        Assert.Equal("change", change.GetProperty("stepKey").GetString());
        Assert.Equal("Save a change to the recipe first.", Assert.Single(change.GetProperty("missing").EnumerateArray()).GetString());
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_run_with_required_steps_not_done_is_not_finished_and_says_which()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, new { definitionKey = "images.create" });
        var before = await SnapshotAsync(runId);

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunIncomplete, Code(body));

        // The required steps, in order, and not the optional one between them.
        var incomplete = body.GetProperty("incompleteSteps").EnumerateArray().ToList();
        Assert.Equal(["about", "make-and-keep"], incomplete.Select(step => step.GetProperty("stepKey").GetString()));
        Assert.All(incomplete, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.GetProperty("title").GetString()));
            Assert.Equal("This step isn't done yet.", Assert.Single(step.GetProperty("missing").EnumerateArray()).GetString());
        });
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task What_the_client_says_about_progress_counts_for_nothing()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, new { definitionKey = "images.create" });
        var before = await SnapshotAsync(runId);

        var response = await client.PostAsJsonAsync($"{RunsIn}/{runId}/completion", new
        {
            expectedConcurrencyToken = await TokenAsync(client, runId),
            status = "Completed",
            completedStepCount = 4,
            readyToComplete = true,
            steps = new[] { new { key = "about", status = "Completed" }, new { key = "make-and-keep", status = "Completed" } },
        }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunIncomplete, Code(await BodyOf(response)));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_step_done_on_a_picture_since_deleted_is_not_done_any_more()
    {
        using var client = await OwnerAsync();
        var (runId, assetId) = await StartFromPictureAsync(client);
        await InWorkspaceAsync(async db =>
        {
            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = DateTimeOffset.UtcNow;
            asset.DeletedByMembershipId = asset.CreatedByMembershipId;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var before = await SnapshotAsync(runId);

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var add = Assert.Single((await BodyOf(response)).GetProperty("incompleteSteps").EnumerateArray());
        Assert.Equal("add", add.GetProperty("stepKey").GetString());
        Assert.Contains("can't be found any more", Assert.Single(add.GetProperty("missing").EnumerateArray()).GetString());
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    // ---- Replay ----

    [Fact]
    public async Task Finishing_twice_finishes_once_whatever_the_token()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var first = await BodyOf(await CompleteAsync(client, runId, await TokenAsync(client, runId)));
        var after = await SnapshotAsync(runId);

        var again = await CompleteAsync(client, runId, NeverHeld);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await BodyOf(again);
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("completedAt").GetDateTimeOffset(), body.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(after, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Losing_to_another_finish_of_the_run_is_the_success_it_would_have_been()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var finishedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Completed;
            run.CompletedAt = finishedAt;
            run.CurrentStepKey = null;
        });

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(finishedAt, (await BodyOf(response)).GetProperty("completedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Losing_to_the_run_being_stopped_says_it_was_stopped()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
        });

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task Losing_to_any_other_change_is_a_conflict_with_the_token_to_look_again_from()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) => run.Title = "Renamed elsewhere");

        var response = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.Equal(await TokenAsync(client, runId), body.GetProperty("currentConcurrencyToken").GetString());
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_stopped_run_a_stale_token_and_a_malformed_one_are_each_refused_and_write_nothing()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var before = await SnapshotAsync(runId);

        var stale = await CompleteAsync(client, runId, NeverHeld);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = await BodyOf(stale);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(staleBody));
        Assert.Equal(await TokenAsync(client, runId), staleBody.GetProperty("currentConcurrencyToken").GetString());

        var malformed = await CompleteAsync(client, runId, "not a token");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunCompletionInvalid, Code(await BodyOf(malformed)));
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(client, runId, token: null)).StatusCode);
        Assert.Equal(before, await SnapshotAsync(runId));

        await ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
        });
        var stoppedBefore = await SnapshotAsync(runId);

        var stopped = await CompleteAsync(client, runId, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(stopped)));

        // Stopped wins over a stale token: there is nothing to look again at.
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(await CompleteAsync(client, runId, NeverHeld))));
        Assert.Equal(stoppedBefore, await SnapshotAsync(runId));
    }

    // ---- Who may, and isolation ----

    [Fact]
    public async Task A_contributor_cannot_finish_someone_elses_run_and_a_viewer_cannot_finish_any()
    {
        using var owner = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(owner);
        var token = await TokenAsync(owner, runId);
        var before = await SnapshotAsync(runId);

        using var contributor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Contributor), MemberPassword, Ct);
        var refused = await CompleteAsync(contributor, runId, token);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(await BodyOf(refused)));

        using var viewer = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Viewer), MemberPassword, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await CompleteAsync(viewer, runId, token)).StatusCode);

        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does()
    {
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = (await BodyOf(await ownerB.PostAsJsonAsync(
                $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/workflow-runs", new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct)))
            .GetProperty("id").GetGuid();
        using var client = await OwnerAsync();

        var foreign = await CompleteAsync(client, theirs, NeverHeld);
        var unknown = await CompleteAsync(client, Guid.NewGuid(), NeverHeld);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)));
    }

    [Fact]
    public async Task Another_workspaces_picture_proves_no_step_here_and_reads_as_an_unknown_one()
    {
        var theirs = await InWorkspaceAsync(_fixture.WorkspaceB.Id, _fixture.WorkspaceB.Slug, async db =>
        {
            var asset = Media.SeededMediaAsset.For(_fixture.WorkspaceB.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(Media.SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        using var client = await OwnerAsync();
        var (planted, _) = await StartFromPictureAsync(client);
        var (unknown, _) = await StartFromPictureAsync(client);
        await PointLinkAtAsync(planted, theirs);
        await PointLinkAtAsync(unknown, Guid.NewGuid());
        var before = await SnapshotAsync(planted);

        var foreign = await CompleteAsync(client, planted, await TokenAsync(client, planted));
        var missing = await CompleteAsync(client, unknown, await TokenAsync(client, unknown));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(missing)), Comparable(await BodyOf(foreign))));
        Assert.Equal(before, await SnapshotAsync(planted));
    }

    [Fact]
    public async Task Another_workspaces_recipe_meets_no_rule_here_and_reads_as_an_unknown_one()
    {
        // Every required step marked done, so each is judged on its rule alone: the recipe the first step linked, a
        // recipe saved during the run, and the recipe's readiness — each read through the recipe module.
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = await CreateRecipeAsync(ownerB, _fixture.WorkspaceB.Slug);
        using var client = await OwnerAsync();
        var planted = await StartImprovingDoneAsync(client);
        var unknown = await StartImprovingDoneAsync(client);
        await PointLinkAtAsync(planted, theirs);
        await PointLinkAtAsync(unknown, Guid.NewGuid());
        var before = await SnapshotAsync(planted);

        var foreign = await CompleteAsync(client, planted, await TokenAsync(client, planted));
        var missing = await CompleteAsync(client, unknown, await TokenAsync(client, unknown));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(
            ["choose", "change", "decide"],
            foreignBody.GetProperty("incompleteSteps").EnumerateArray().Select(step => step.GetProperty("stepKey").GetString()));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(missing)), Comparable(foreignBody)));
        Assert.Equal(before, await SnapshotAsync(planted));
    }

    // ---- Helpers ----

    private async Task<Guid> CreateRecipeAsync(GatewayClient client, string slug)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{slug}/recipes", new { title = "Soda bread" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>An improve-a-recipe run started from this workspace's recipe, with every required step marked done.</summary>
    private async Task<Guid> StartImprovingDoneAsync(GatewayClient client)
    {
        var recipeId = await CreateRecipeAsync(client, _fixture.WorkspaceA.Slug);
        var runId = await StartAsync(client, new { definitionKey = "recipe.improve", startingResource = new { kind = "Recipe", id = recipeId } });
        await ChangeRunAsync(runId, (run, steps) =>
        {
            foreach (var step in steps.Where(step => step.StepKey is "change" or "decide"))
            {
                step.Status = WorkflowStepRunStatus.Completed;
                step.CompletedAt = DateTimeOffset.UtcNow;
                step.CompletedByMembershipId = run.CreatedByMembershipId;
            }

            run.CompletedStepCount = 3;
            run.CurrentStepKey = "compare";
        });

        return runId;
    }

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    private Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string? token) =>
        client.PostAsJsonAsync($"{RunsIn}/{runId}/completion", new { expectedConcurrencyToken = token }, Ct);

    private async Task<Guid> StartAsync(GatewayClient client, object body)
    {
        var response = await client.PostAsJsonAsync(RunsIn, body, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An organize-a-picture run started from a picture: its one required step is done at the start, so it is ready
    /// to finish with its three optional steps still open.
    /// </summary>
    private async Task<(Guid RunId, Guid AssetId)> StartFromPictureAsync(GatewayClient client)
    {
        var assetId = await InWorkspaceAsync(async db =>
        {
            var asset = Media.SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(Media.SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var runId = await StartAsync(client, new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } });

        return (runId, assetId);
    }

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct))).GetProperty("concurrencyToken").GetString()!;

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.Status}|{run.CompletedAt:O}|{run.UpdatedAt:O}|{run.CurrentStepKey}|"
                + await StepsAndLinksAsync(runId);
        });

    private Task<string> StepsAndLinksAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);
            var links = await db.WorkflowRunLinks.AsNoTracking().Where(link => link.WorkflowRunId == runId)
                .OrderBy(link => link.Id).ToListAsync(Ct);

            return string.Join(",", steps.Select(step =>
                    $"{step.StepKey}:{step.Status}:{step.StartedAt:O}:{step.CompletedAt:O}:{step.SkippedAt:O}:{step.DraftResourceKind}:{step.DraftResourceId}:{step.DraftSavedAt:O}"))
                + "|" + string.Join(",", links.Select(link => $"{link.ResourceKind}:{link.ResourceId}:{link.StepKey}:{link.LinkedAt:O}"));
        });

    private Task<string> PictureAsync(Guid assetId) =>
        InWorkspaceAsync(async db =>
        {
            var asset = await db.MediaAssets.AsNoTracking().SingleAsync(item => item.Id == assetId, Ct);

            return $"{Convert.ToBase64String(asset.RowVersion)}|{asset.UpdatedAt:O}|{asset.Title}|{asset.Description}|{asset.AltText}|"
                + $"{asset.CurrentVersionNumber}|{asset.DeletedAt:O}";
        });

    /// <summary>Changes a run and its steps directly, as another request would have.</summary>
    private Task ChangeRunAsync(Guid runId, Action<WorkflowRun, List<WorkflowStepRun>> change) =>
        InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.Where(step => step.WorkflowRunId == runId).ToListAsync(Ct);
            change(run, steps);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    /// <summary>Points the run's one link at another id, as no request can: the only way to plant one.</summary>
    private Task PointLinkAtAsync(Guid runId, Guid resourceId) =>
        InWorkspaceAsync(async db =>
        {
            (await db.WorkflowRunLinks.SingleAsync(link => link.WorkflowRunId == runId, Ct)).ResourceId = resourceId;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private async Task<string> AddMemberAsync(WorkspaceRole role)
    {
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, MemberPassword);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);

        return email;
    }

    private static JsonNode Comparable(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");
        node.Remove("correlationId");

        return node;
    }

    private Task<T> InWorkspaceAsync<T>(Func<CreatorPantryDbContext, Task<T>> work) =>
        InWorkspaceAsync(_fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, work);

    private async Task<T> InWorkspaceAsync<T>(Guid workspaceId, string slug, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>A switch a test sets to make the next save lose a race to a rival change.</summary>
    private sealed class StoreSwitch
    {
        /// <summary>A change applied just before the next save, which then reports that it lost to it. Used once.</summary>
        public Func<Task>? LoseRaceTo { get; set; }
    }

    /// <summary>
    /// The real data layer, except that a save can find a rival change landed first. The gateway fixture's store does
    /// not move a row version on its own, so a lost race is staged here.
    /// </summary>
    private sealed class SwitchedDataLayer(IWorkflowRunDataLayer inner, StoreSwitch store) : IWorkflowRunDataLayer
    {
        public Task CreateAsync(
            WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps, IReadOnlyList<WorkflowRunLink> links, CancellationToken cancellationToken) =>
            inner.CreateAsync(run, steps, links, cancellationToken);

        public Task<WorkflowRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetAsync(runId, cancellationToken);

        public Task<WorkflowRunRecord?> GetForUpdateAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetForUpdateAsync(runId, cancellationToken);

        public Task<WorkflowRunWrite> RestartAsync(
            WorkflowRun oldRun,
            byte[] expectedRowVersion,
            WorkflowRun newRun,
            IReadOnlyList<WorkflowStepRun> newSteps,
            IReadOnlyList<WorkflowRunLink> newLinks,
            CancellationToken cancellationToken) =>
            inner.RestartAsync(oldRun, expectedRowVersion, newRun, newSteps, newLinks, cancellationToken);

        public Task<WorkflowRunWrite> AbandonAsync(
            WorkflowRun run, byte[] expectedRowVersion, AuditEntry audit, CancellationToken cancellationToken) =>
            inner.AbandonAsync(run, expectedRowVersion, audit, cancellationToken);

        public async Task<WorkflowRunWrite> SaveAsync(
            WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken, IReadOnlyList<WorkflowRunLink>? newLinks = null)
        {
            if (store.LoseRaceTo is { } rival)
            {
                store.LoseRaceTo = null;
                await rival();

                return WorkflowRunWrite.Stale;
            }

            return await inner.SaveAsync(run, expectedRowVersion, cancellationToken, newLinks);
        }
    }
}
