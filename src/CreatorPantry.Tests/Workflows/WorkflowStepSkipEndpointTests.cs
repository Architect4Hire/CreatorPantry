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
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs/{runId}/steps/{stepKey}/skip</c> through the real Gateway (13A.9a): an optional step
/// passed over with its reason and nothing made of it, a required one refused, the last step, a repeat, a stale
/// token, Back and skip again, who may, and two workspaces.
/// </summary>
public sealed class WorkflowStepSkipEndpointTests : IAsyncLifetime
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
    public async Task An_optional_step_is_passed_over_with_its_reason_and_nothing_is_made_of_it()
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);

        // A draft saved on the step before it was skipped: it stays a draft, never a result.
        var drafted = Guid.NewGuid();
        await InWorkspaceAsync(async db =>
        {
            var plan = await db.WorkflowStepRuns.SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == "plan", Ct);
            plan.Status = WorkflowStepRunStatus.InProgress;
            plan.StartedAt = DateTimeOffset.UtcNow;
            plan.DraftResourceKind = WorkflowResourceKind.CreativeContext;
            plan.DraftResourceId = drafted;
            plan.DraftSavedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var response = await SkipAsync(client, runId, "plan", await TokenAsync(client, runId), reason: "  Not today.  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);
        var plan = Step(run, "plan");
        Assert.Equal("Skipped", plan.GetProperty("status").GetString());
        Assert.Equal("Not today.", plan.GetProperty("skipReason").GetString());
        Assert.Equal(JsonValueKind.String, plan.GetProperty("skippedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("completedAt").ValueKind);
        Assert.Equal(drafted, plan.GetProperty("draftResourceId").GetGuid());
        Assert.Equal(1, run.GetProperty("skippedStepCount").GetInt32());
        Assert.Equal(1, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal("make-and-keep", run.GetProperty("nextAction").GetProperty("stepKey").GetString());

        // Only the record the first step made: nothing was linked for the step passed over.
        Assert.Single(run.GetProperty("links").EnumerateArray());

        // Who passed it over is the caller, from the workspace — not anything sent.
        await InWorkspaceAsync(async db =>
        {
            var stored = await db.WorkflowStepRuns.AsNoTracking().SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == "plan", Ct);
            var created = await db.WorkflowRuns.Where(item => item.Id == runId).Select(item => item.CreatedByMembershipId).SingleAsync(Ct);
            Assert.Equal(created, stored.SkippedByMembershipId);

            return 0;
        });
    }

    [Fact]
    public async Task A_required_step_cannot_be_passed_over()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var before = await SnapshotAsync(runId);

        var response = await SkipAsync(client, runId, "about", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepRequired, Code(await BodyOf(response)));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Passing_over_the_last_open_step_leaves_the_run_ready_to_be_finished()
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);
        await SkipAsync(client, runId, "plan", await TokenAsync(client, runId));
        var assetId = await InWorkspaceAsync(async db =>
        {
            var asset = SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/make-and-keep/completion",
            new { results = new[] { new { kind = "MediaAsset", id = assetId } }, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);

        var run = await BodyOf(await SkipAsync(client, runId, "lead-picture", await TokenAsync(client, runId)));

        Assert.True(run.GetProperty("readyToComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("nextAction").ValueKind);
        Assert.Equal(2, run.GetProperty("skippedStepCount").GetInt32());
        Assert.Equal(
            "lead-picture",
            await InWorkspaceAsync(db => db.WorkflowRuns.Where(item => item.Id == runId).Select(item => item.CurrentStepKey).SingleAsync(Ct)));
    }

    [Fact]
    public async Task The_same_skip_twice_writes_once()
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);
        await SkipAsync(client, runId, "plan", await TokenAsync(client, runId), reason: "First.");
        var after = await SnapshotAsync(runId);

        var again = await SkipAsync(client, runId, "plan", NeverHeld, reason: "Second.");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("First.", Step(await BodyOf(again), "plan").GetProperty("skipReason").GetString());
        Assert.Equal(after, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_skip_against_a_run_that_has_changed_is_refused()
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);
        var before = await SnapshotAsync(runId);

        var response = await SkipAsync(client, runId, "plan", NeverHeld);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("currentConcurrencyToken").GetString()));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Back_undoes_a_skip_and_the_step_can_be_passed_over_again()
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);
        await SkipAsync(client, runId, "plan", await TokenAsync(client, runId), reason: "Not today.");

        var back = await BodyOf(await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/make-and-keep/back", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct));
        Assert.Equal("InProgress", Step(back, "plan").GetProperty("status").GetString());
        Assert.Equal(0, back.GetProperty("skippedStepCount").GetInt32());

        var again = await SkipAsync(client, runId, "plan", await TokenAsync(client, runId), reason: "Still not today.");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Still not today.", Step(await BodyOf(again), "plan").GetProperty("skipReason").GetString());
    }

    [Fact]
    public async Task Only_the_current_open_step_of_a_run_in_progress_is_passed_over()
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);
        var token = await TokenAsync(client, runId);

        var later = await SkipAsync(client, runId, "lead-picture", token);
        Assert.Equal(HttpStatusCode.Conflict, later.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepNotCurrent, Code(await BodyOf(later)));
        Assert.Equal(WorkflowErrorCodes.StepNotFound, Code(await BodyOf(await SkipAsync(client, runId, "nowhere", token))));

        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(await SkipAsync(client, runId, "plan", token))));
    }

    [Fact]
    public async Task The_last_step_once_done_is_not_passed_over()
    {
        using var client = await OwnerAsync();
        var assetId = await InWorkspaceAsync(async db =>
        {
            var asset = SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var start = await client.PostAsJsonAsync(RunsIn, new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(start)).GetProperty("id").GetGuid();
        foreach (var step in new[] { "describe", "tag", "note-use" })
        {
            await client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/{step}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        }

        var response = await SkipAsync(client, runId, "note-use", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepAlreadyDone, Code(await BodyOf(response)));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("too long")]
    public async Task A_blank_reason_or_one_too_long_is_a_bad_request(string what)
    {
        using var client = await OwnerAsync();
        var runId = await RunOnPlanAsync(client);

        var response = await SkipAsync(client, runId, "plan", await TokenAsync(client, runId),
            reason: what == "blank" ? "   " : new string('a', WorkflowRunPolicy.SkipReasonMaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepSkipInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task A_reason_is_optional_and_one_at_the_limit_is_kept()
    {
        using var client = await OwnerAsync();
        var omitted = await RunOnPlanAsync(client);
        var atLimit = await RunOnPlanAsync(client);
        var longest = new string('a', WorkflowRunPolicy.SkipReasonMaxLength);

        var none = await BodyOf(await SkipAsync(client, omitted, "plan", await TokenAsync(client, omitted)));
        var kept = await BodyOf(await SkipAsync(client, atLimit, "plan", await TokenAsync(client, atLimit), reason: longest));

        Assert.Equal(JsonValueKind.Null, Step(none, "plan").GetProperty("skipReason").ValueKind);
        Assert.Equal(longest, Step(kept, "plan").GetProperty("skipReason").GetString());
    }

    /// <summary>
    /// A step passed over is not completed where it stands — that would make it both, which the record cannot hold.
    /// Back reopens it to be done. The run reaches this by passing over its last open step and waiting on it.
    /// </summary>
    [Fact]
    public async Task A_step_passed_over_is_not_completed_in_place_nor_given_a_draft()
    {
        using var client = await OwnerAsync();
        var assetId = await InWorkspaceAsync(async db =>
        {
            var asset = SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var start = await client.PostAsJsonAsync(RunsIn, new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(start)).GetProperty("id").GetGuid();
        foreach (var step in new[] { "describe", "tag" })
        {
            await client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/{step}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        }

        await SkipAsync(client, runId, "note-use", await TokenAsync(client, runId));
        var before = await SnapshotAsync(runId);

        var completed = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/note-use/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
        var body = await BodyOf(completed);
        Assert.Equal(WorkflowErrorCodes.StepAlreadyDone, Code(body));
        Assert.Contains("passed over", body.GetProperty("title").GetString(), StringComparison.Ordinal);

        var drafted = await client.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/note-use/draft",
            new { kind = "MediaAsset", id = assetId, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, drafted.StatusCode);
        Assert.Contains("passed over", (await BodyOf(drafted)).GetProperty("title").GetString(), StringComparison.Ordinal);

        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_required_step_is_refused_as_required_before_any_token_is_looked_at()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");

        var response = await SkipAsync(client, runId, "about", NeverHeld);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepRequired, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task An_editor_may_pass_over_a_step_on_someone_elses_run()
    {
        using var owner = await OwnerAsync();
        var runId = await RunOnPlanAsync(owner);
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, (await SkipAsync(editor, runId, "plan", await TokenAsync(owner, runId))).StatusCode);
    }

    [Fact]
    public async Task A_contributor_cannot_pass_over_a_step_on_someone_elses_run()
    {
        using var owner = await OwnerAsync();
        var runId = await RunOnPlanAsync(owner);
        var email = await AddContributorAsync();
        using var contributor = await _fixture.SignInAsync(email, ContributorPassword, Ct);

        var response = await SkipAsync(contributor, runId, "plan", await TokenAsync(owner, runId));

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

        var foreign = await SkipAsync(client, theirs, "plan", NeverHeld);
        var unknown = await SkipAsync(client, Guid.NewGuid(), "plan", NeverHeld);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)));
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    private Task<HttpResponseMessage> SkipAsync(GatewayClient client, Guid runId, string stepKey, string? token, string? reason = null) =>
        client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/{stepKey}/skip", new { reason, expectedConcurrencyToken = token }, Ct);

    private static JsonElement Step(JsonElement run, string key) =>
        run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == key);

    private async Task<Guid> StartAsync(GatewayClient client, string definitionKey)
    {
        var response = await client.PostAsJsonAsync(RunsIn, new { definitionKey }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    /// <summary>A pictures run with its first step done on an idea, now on the optional plan step.</summary>
    private async Task<Guid> RunOnPlanAsync(GatewayClient client)
    {
        var runId = await StartAsync(client, "images.create");
        var contextId = await InWorkspaceAsync(async db =>
        {
            var context = CreativeContextSeeds.NewContext(DateTimeOffset.UtcNow);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

        var completed = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/completion",
            new { results = new[] { new { kind = "CreativeContext", id = contextId } }, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        return runId;
    }

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct))).GetProperty("concurrencyToken").GetString()!;

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);
            var links = await db.WorkflowRunLinks.AsNoTracking().CountAsync(link => link.WorkflowRunId == runId, Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.UpdatedAt:O}|{run.CurrentStepKey}|{run.CompletedStepCount}|{run.SkippedStepCount}|{links}|"
                + string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.SkippedAt:O}:{step.SkipReason}"));
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
