using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs/{runId}/steps/{stepKey}/back</c> through the real Gateway (13A.9): the step before is
/// reopened — from done or passed over — with nothing lost and nothing later touched; the first step, a repeat, a
/// stale token, who may, and two workspaces.
/// </summary>
public sealed class WorkflowRunBackEndpointTests : IAsyncLifetime
{
    private const string ContributorPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RunsIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    [Fact]
    public async Task Back_reopens_a_completed_step_and_leaves_everything_else_where_it_was()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnMakeAndKeepAsync(client);

        var response = await BackAsync(client, runId, "make-and-keep", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);
        var steps = run.GetProperty("steps").EnumerateArray().ToDictionary(step => step.GetProperty("key").GetString()!);

        // The step before is open again, its completion cleared and taken off the count.
        Assert.Equal("InProgress", steps["plan"].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, steps["plan"].GetProperty("completedAt").ValueKind);
        Assert.Equal(1, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal("plan", run.GetProperty("nextAction").GetProperty("stepKey").GetString());

        // The step left, the steps before, their drafts and the run's links are as they were.
        Assert.Equal("NotStarted", steps["make-and-keep"].GetProperty("status").GetString());
        Assert.Equal("Completed", steps["about"].GetProperty("status").GetString());
        Assert.Equal(contextId, steps["about"].GetProperty("draftResourceId").GetGuid());
        Assert.Equal(contextId, Assert.Single(run.GetProperty("links").EnumerateArray()).GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task Back_onto_the_first_step_reopens_it_with_its_draft_and_its_record_still_there()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnMakeAndKeepAsync(client);
        await BackAsync(client, runId, "make-and-keep", await TokenAsync(client, runId));

        var response = await BackAsync(client, runId, "plan", await TokenAsync(client, runId));

        var run = await BodyOf(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("about", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
        var about = run.GetProperty("steps")[0];
        Assert.Equal("InProgress", about.GetProperty("status").GetString());
        Assert.Equal(contextId, about.GetProperty("draftResourceId").GetGuid());
        Assert.Single(run.GetProperty("links").EnumerateArray());
        Assert.Equal(0, run.GetProperty("completedStepCount").GetInt32());
    }

    [Fact]
    public async Task Back_onto_a_step_that_was_passed_over_opens_it_to_be_done_after_all()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(client);
        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            var plan = await db.WorkflowStepRuns.SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == "plan", Ct);
            plan.Status = WorkflowStepRunStatus.Skipped;
            plan.CompletedAt = null;
            plan.CompletedByMembershipId = null;
            plan.SkippedAt = DateTimeOffset.UtcNow;
            plan.SkippedByMembershipId = run.CreatedByMembershipId;
            plan.SkipReason = "Not today.";
            run.CompletedStepCount = 1;
            run.SkippedStepCount = 1;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var run = await BodyOf(await BackAsync(client, runId, "make-and-keep", await TokenAsync(client, runId)));

        var plan = run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == "plan");
        Assert.Equal("InProgress", plan.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("skipReason").ValueKind);
        Assert.Equal(0, run.GetProperty("skippedStepCount").GetInt32());
        Assert.Equal(1, run.GetProperty("completedStepCount").GetInt32());
    }

    [Fact]
    public async Task A_reopened_step_goes_forward_again_only_by_being_completed()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(client);
        await BackAsync(client, runId, "make-and-keep", await TokenAsync(client, runId));

        var completed = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/plan/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var run = await BodyOf(completed);
        Assert.Equal("make-and-keep", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Equal(2, run.GetProperty("completedStepCount").GetInt32());
    }

    [Fact]
    public async Task The_step_left_keeps_its_draft_and_continue_goes_to_the_reopened_step()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnMakeAndKeepAsync(client);
        var leftDraft = Guid.NewGuid();
        await InWorkspaceAsync(async db =>
        {
            var row = await db.WorkflowStepRuns.SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == "make-and-keep", Ct);
            row.Status = WorkflowStepRunStatus.InProgress;
            row.StartedAt = DateTimeOffset.UtcNow;
            row.DraftResourceKind = WorkflowResourceKind.MediaAsset;
            row.DraftResourceId = leftDraft;
            row.DraftSavedAt = DateTimeOffset.UtcNow;
            (await db.WorkflowRuns.SingleAsync(run => run.Id == runId, Ct)).LastRoute = $"image-studio/context/{contextId:D}";
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var run = await BodyOf(await BackAsync(client, runId, "make-and-keep", await TokenAsync(client, runId)));

        var left = run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == "make-and-keep");
        Assert.Equal("InProgress", left.GetProperty("status").GetString());
        Assert.Equal(leftDraft, left.GetProperty("draftResourceId").GetGuid());

        // Where the creator last was is the step just left; Continue goes to the reopened one.
        Assert.Equal(JsonValueKind.Null, run.GetProperty("lastRoute").ValueKind);
        Assert.Equal($"image-studio/context/{contextId:D}", run.GetProperty("resumeRoute").GetString());
        Assert.Equal("plan", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
    }

    [Fact]
    public async Task A_step_done_at_the_start_reopens_and_completes_again_on_the_record_the_run_already_holds()
    {
        using var client = await OwnerAsync();
        var assetId = await InWorkspaceAsync(async db =>
        {
            var asset = Media.SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(Media.SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var start = await client.PostAsJsonAsync(RunsIn, new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(start)).GetProperty("id").GetGuid();

        var back = await BodyOf(await BackAsync(client, runId, "describe", await TokenAsync(client, runId)));
        var add = back.GetProperty("steps")[0];
        Assert.Equal("InProgress", add.GetProperty("status").GetString());

        // Nothing named and no draft: the picture the run started from is the one this step made.
        var again = await client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/add/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var run = await BodyOf(again);
        Assert.Equal("describe", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Single(run.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task Back_from_the_finished_last_step_and_done_again_waits_on_the_last_step_not_the_middle()
    {
        using var client = await OwnerAsync();
        var assetId = await InWorkspaceAsync(async db =>
        {
            var asset = Media.SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(Media.SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var start = await client.PostAsJsonAsync(RunsIn, new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(start)).GetProperty("id").GetGuid();
        foreach (var step in new[] { "describe", "tag", "note-use" })
        {
            await client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/{step}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        }

        // Every step done, the run waiting on the last: a draft there is refused, and Back reopens the one before.
        var draft = await client.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/note-use/draft",
            new { kind = "MediaAsset", id = assetId, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepAlreadyDone, Code(await BodyOf(draft)));

        await BackAsync(client, runId, "note-use", await TokenAsync(client, runId));
        var redone = await BodyOf(await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/tag/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct));

        Assert.True(redone.GetProperty("readyToComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, redone.GetProperty("nextAction").ValueKind);
        Assert.Equal(
            "note-use",
            await InWorkspaceAsync(db => db.WorkflowRuns.Where(run => run.Id == runId).Select(run => run.CurrentStepKey).SingleAsync(Ct)));
    }

    [Fact]
    public async Task An_editor_may_take_someone_elses_run_back_and_a_malformed_token_is_a_bad_request()
    {
        using var owner = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(owner);
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);

        var malformed = await BackAsync(editor, runId, "make-and-keep", "not a token");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(WorkflowErrorCodes.BackInvalid, Code(await BodyOf(malformed)));

        Assert.Equal(HttpStatusCode.OK, (await BackAsync(editor, runId, "make-and-keep", await TokenAsync(owner, runId))).StatusCode);
    }

    [Fact]
    public async Task There_is_no_going_back_from_the_first_step()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var before = await SnapshotAsync(runId);

        var response = await BackAsync(client, runId, "about", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.AtFirstStep, Code(await BodyOf(response)));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task The_same_back_twice_moves_once()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(client);
        var token = await TokenAsync(client, runId);

        await BackAsync(client, runId, "make-and-keep", token);
        var after = await SnapshotAsync(runId);
        var again = await BackAsync(client, runId, "make-and-keep", NeverHeld);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("plan", (await BodyOf(again)).GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Equal(after, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_back_against_a_run_that_has_changed_is_refused()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(client);
        var before = await SnapshotAsync(runId);

        var response = await BackAsync(client, runId, "make-and-keep", NeverHeld);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(await BodyOf(response)));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Back_is_only_from_the_step_the_run_is_on_of_a_run_in_progress()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(client);
        var token = await TokenAsync(client, runId);

        // From the first step, which is two behind where the run is.
        var notCurrent = await BackAsync(client, runId, "about", token);
        Assert.Equal(HttpStatusCode.Conflict, notCurrent.StatusCode);
        var body = await BodyOf(notCurrent);
        Assert.Equal(WorkflowErrorCodes.StepNotCurrent, Code(body));
        Assert.Equal("make-and-keep", body.GetProperty("currentStepKey").GetString());

        Assert.Equal(WorkflowErrorCodes.StepNotFound, Code(await BodyOf(await BackAsync(client, runId, "nowhere", token))));
        Assert.Equal(HttpStatusCode.BadRequest, (await BackAsync(client, runId, "make-and-keep", token: null)).StatusCode);

        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(await BackAsync(client, runId, "make-and-keep", token))));
    }

    [Fact]
    public async Task A_contributor_cannot_take_someone_elses_run_back()
    {
        using var owner = await OwnerAsync();
        var (runId, _) = await RunOnMakeAndKeepAsync(owner);
        var email = await AddContributorAsync();
        using var contributor = await _fixture.SignInAsync(email, ContributorPassword, Ct);

        var response = await BackAsync(contributor, runId, "make-and-keep", await TokenAsync(owner, runId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does()
    {
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = (await BodyOf(await ownerB.PostAsJsonAsync(
                $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/workflow-runs", new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct)))
            .GetProperty("id").GetGuid();
        using var client = await OwnerAsync();

        var foreign = await BackAsync(client, theirs, "plan", NeverHeld);
        var unknown = await BackAsync(client, Guid.NewGuid(), "plan", NeverHeld);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)));
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    private Task<HttpResponseMessage> BackAsync(GatewayClient client, Guid runId, string stepKey, string? token) =>
        client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/{stepKey}/back", new { expectedConcurrencyToken = token }, Ct);

    private async Task<Guid> StartAsync(GatewayClient client, string definitionKey)
    {
        var response = await client.PostAsJsonAsync(RunsIn, new { definitionKey }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// A pictures run two steps in, as a creator gets there: the idea saved as the first step's draft and the step
    /// completed from it, the plan acknowledged, and on to making and keeping pictures.
    /// </summary>
    private async Task<(Guid RunId, Guid ContextId)> RunOnMakeAndKeepAsync(GatewayClient client)
    {
        var runId = await StartAsync(client, "images.create");
        var contextId = await InWorkspaceAsync(async db =>
        {
            var context = CreativeContextSeeds.NewContext(DateTimeOffset.UtcNow);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/draft",
            new { kind = "CreativeContext", id = contextId, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/plan/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct)).StatusCode);

        return (runId, contextId);
    }

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct))).GetProperty("concurrencyToken").GetString()!;

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.UpdatedAt:O}|{run.CurrentStepKey}|{run.CompletedStepCount}|{run.SkippedStepCount}|"
                + string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.CompletedAt:O}:{step.DraftResourceId}"));
        });

    private async Task<string> AddContributorAsync()
    {
        var email = $"contributor-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, ContributorPassword);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
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

    private async Task<T> InWorkspaceAsync<T>(Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
