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
/// <c>POST .../workflow-runs/{runId}/abandonment</c> through the real Gateway (13A.10b): a run stops only when the
/// creator confirms it, keeps everything it linked, is audited, is never mistaken for a finish, a repeat writes
/// nothing, a stale token is refused, who may, and two workspaces.
/// </summary>
public sealed class WorkflowRunAbandonEndpointTests : IAsyncLifetime
{
    private const string MemberPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private readonly StoreSwitch _store = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real data layer, behind a switch that makes its next stop lose a race.
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

    // ---- Confirm ----

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Without_confirmation_nothing_happens(bool? confirmed)
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var before = await SnapshotAsync(runId);

        var response = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/abandonment",
            new { confirmed, reason = "Not this week", expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunAbandonInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("confirmed", out _));
        Assert.Equal(before, await SnapshotAsync(runId));
        Assert.Equal(0, await AuditRowsAsync(runId));
    }

    [Fact]
    public async Task A_blank_or_overlong_reason_and_a_missing_or_malformed_token_are_each_refused_and_write_nothing()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var token = await TokenAsync(client, runId);
        var before = await SnapshotAsync(runId);

        foreach (var (reason, sent, field) in new[]
        {
            ("   ", token, "reason"),
            (new string('a', WorkflowRunPolicy.AbandonReasonMaxLength + 1), token, "reason"),
            ((string?)null, (string?)null, "expectedConcurrencyToken"),
            (null, "not a token", "expectedConcurrencyToken"),
        })
        {
            var response = await AbandonAsync(client, runId, sent, reason);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await BodyOf(response);
            Assert.Equal(WorkflowErrorCodes.RunAbandonInvalid, Code(body));
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        Assert.Equal(before, await SnapshotAsync(runId));
        Assert.Equal(0, await AuditRowsAsync(runId));
    }

    // ---- Success ----

    [Fact]
    public async Task A_confirmed_stop_keeps_the_run_whole_touches_nothing_it_linked_and_is_audited()
    {
        using var client = await OwnerAsync();
        var (runId, assetId) = await StartFromPictureAsync(client);

        // An optional step left part-way, with a draft: stopping leaves it exactly so.
        var draftId = Guid.NewGuid();
        await ChangeRunAsync(runId, (run, steps) =>
        {
            var describe = steps.Single(step => step.StepKey == "describe");
            describe.Status = WorkflowStepRunStatus.InProgress;
            describe.StartedAt = DateTimeOffset.UtcNow;
            describe.DraftResourceKind = WorkflowResourceKind.MediaAsset;
            describe.DraftResourceId = draftId;
            describe.DraftSavedAt = DateTimeOffset.UtcNow;
            run.CurrentStepKey = "describe";
        });
        var stepsBefore = await StepsAndLinksAsync(runId);
        var pictureBefore = await PictureAsync(assetId);

        var response = await AbandonAsync(client, runId, await TokenAsync(client, runId), "  Not this week  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);
        Assert.Equal("Abandoned", run.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("abandonedAt").ValueKind);
        Assert.Equal("Not this week", run.GetProperty("abandonReason").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("replacedByRunId").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("nextAction").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resumeRoute").ValueKind);
        Assert.False(run.GetProperty("readyToComplete").GetBoolean());

        // How far it got stays with it; no step, link or linked record moved.
        var stored = await StoredAsync(runId);
        Assert.Equal("describe", stored.CurrentStepKey);
        Assert.Equal(stored.CreatedByMembershipId, stored.AbandonedByMembershipId);
        Assert.Equal(stepsBefore, await StepsAndLinksAsync(runId));
        Assert.Equal(pictureBefore, await PictureAsync(assetId));
        var describe = run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == "describe");
        Assert.Equal("InProgress", describe.GetProperty("status").GetString());
        Assert.Equal(draftId, describe.GetProperty("draftResourceId").GetGuid());
        Assert.Equal(assetId, Assert.Single(run.GetProperty("links").EnumerateArray()).GetProperty("resourceId").GetGuid());

        // One audit row, with ids and counts and none of the creator's words.
        var audit = Assert.Single(await AuditAsync(runId));
        Assert.Equal(WorkflowAuditActions.RunResourceType, audit.ResourceType);
        Assert.False(string.IsNullOrEmpty(audit.ActorUserId));
        Assert.Equal("InProgress", audit.BeforeReference);
        Assert.Equal("Abandoned", audit.AfterReference);
        Assert.DoesNotContain("Not this week", audit.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Title, audit.Summary, StringComparison.Ordinal);

        // And a read says the same.
        var read = await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct));
        Assert.Equal("Abandoned", read.GetProperty("status").GetString());
        Assert.Equal("Not this week", read.GetProperty("abandonReason").GetString());
    }

    [Fact]
    public async Task A_stop_without_a_reason_records_none()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);

        var response = await AbandonAsync(client, runId, await TokenAsync(client, runId), reason: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await BodyOf(response)).GetProperty("abandonReason").ValueKind);
        Assert.Null((await StoredAsync(runId)).AbandonReason);
        Assert.Equal(1, await AuditRowsAsync(runId));
    }

    // ---- Abandon is not complete ----

    [Fact]
    public async Task A_finished_run_cannot_be_stopped_and_a_stopped_run_cannot_be_finished()
    {
        using var client = await OwnerAsync();

        // Ready to finish, and finished: stopping it is refused, whatever the token.
        var (finished, _) = await StartFromPictureAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, finished, await TokenAsync(client, finished))).StatusCode);
        var finishedBefore = await SnapshotAsync(finished);

        var refused = await AbandonAsync(client, finished, await TokenAsync(client, finished), "Changed my mind");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(refused)));
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(await AbandonAsync(client, finished, NeverHeld, null))));
        Assert.Equal(finishedBefore, await SnapshotAsync(finished));
        Assert.Equal(0, await AuditRowsAsync(finished));

        // Ready to finish, and stopped instead: it is not finished by asking.
        var (stopped, _) = await StartFromPictureAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await AbandonAsync(client, stopped, await TokenAsync(client, stopped), null)).StatusCode);
        var stoppedBefore = await SnapshotAsync(stopped);

        var completion = await CompleteAsync(client, stopped, await TokenAsync(client, stopped));
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(completion)));
        Assert.Equal(stoppedBefore, await SnapshotAsync(stopped));
        Assert.Null((await StoredAsync(stopped)).CompletedAt);
    }

    // ---- Replay ----

    [Fact]
    public async Task Stopping_twice_stops_once_whatever_the_token_and_keeps_the_first_reason()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var first = await BodyOf(await AbandonAsync(client, runId, await TokenAsync(client, runId), "Not this week"));
        var after = await SnapshotAsync(runId);

        var again = await AbandonAsync(client, runId, NeverHeld, "Something else entirely");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await BodyOf(again);
        Assert.Equal("Abandoned", body.GetProperty("status").GetString());
        Assert.Equal("Not this week", body.GetProperty("abandonReason").GetString());
        Assert.Equal(first.GetProperty("abandonedAt").GetDateTimeOffset(), body.GetProperty("abandonedAt").GetDateTimeOffset());
        Assert.Equal(after, await SnapshotAsync(runId));
        Assert.Equal(1, await AuditRowsAsync(runId));
    }

    [Fact]
    public async Task A_run_already_started_over_answers_as_it_stands()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var replacement = Guid.NewGuid();
        await ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            run.ReplacedByRunId = replacement;
        });
        var before = await SnapshotAsync(runId);

        var response = await AbandonAsync(client, runId, NeverHeld, "Too late now");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(replacement, body.GetProperty("replacedByRunId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("abandonReason").ValueKind);
        Assert.Equal(before, await SnapshotAsync(runId));
        Assert.Equal(0, await AuditRowsAsync(runId));
    }

    [Fact]
    public async Task Losing_to_another_stop_of_the_run_is_the_success_it_would_have_been()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var stoppedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = stoppedAt;
            run.AbandonReason = "The other tab's reason";
        });

        var response = await AbandonAsync(client, runId, await TokenAsync(client, runId), "This tab's reason");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(stoppedAt, body.GetProperty("abandonedAt").GetDateTimeOffset());
        Assert.Equal("The other tab's reason", body.GetProperty("abandonReason").GetString());
    }

    // ---- Conflict ----

    [Fact]
    public async Task Losing_to_the_run_being_finished_says_it_is_finished()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.CurrentStepKey = null;
        });

        var response = await AbandonAsync(client, runId, await TokenAsync(client, runId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(response)));
        Assert.Equal(WorkflowRunStatus.Completed, (await StoredAsync(runId)).Status);
    }

    [Fact]
    public async Task Losing_to_any_other_change_is_a_conflict_with_the_token_to_look_again_from()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) => run.Title = "Renamed elsewhere");

        var response = await AbandonAsync(client, runId, await TokenAsync(client, runId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.Equal(await TokenAsync(client, runId), body.GetProperty("currentConcurrencyToken").GetString());
        Assert.Equal(WorkflowRunStatus.InProgress, (await StoredAsync(runId)).Status);
    }

    [Fact]
    public async Task A_stale_token_is_refused_with_the_token_to_look_again_from_and_writes_nothing()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(client);
        var before = await SnapshotAsync(runId);

        var response = await AbandonAsync(client, runId, NeverHeld, "Not this week");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.Equal(await TokenAsync(client, runId), body.GetProperty("currentConcurrencyToken").GetString());
        Assert.Equal(before, await SnapshotAsync(runId));
        Assert.Equal(0, await AuditRowsAsync(runId));
    }

    // ---- Who may, and isolation ----

    [Fact]
    public async Task A_contributor_cannot_stop_someone_elses_run_and_a_viewer_cannot_stop_any()
    {
        using var owner = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(owner);
        var token = await TokenAsync(owner, runId);
        var before = await SnapshotAsync(runId);

        using var contributor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Contributor), MemberPassword, Ct);
        var refused = await AbandonAsync(contributor, runId, token, null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(await BodyOf(refused)));

        using var viewer = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Viewer), MemberPassword, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await AbandonAsync(viewer, runId, token, null)).StatusCode);

        Assert.Equal(before, await SnapshotAsync(runId));
        Assert.Equal(0, await AuditRowsAsync(runId));
    }

    [Fact]
    public async Task An_editor_stopping_someone_elses_run_is_recorded_as_the_one_who_stopped_it()
    {
        using var editor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Editor), MemberPassword, Ct);
        using var owner = await OwnerAsync();
        var (runId, _) = await StartFromPictureAsync(owner);

        var response = await AbandonAsync(editor, runId, await TokenAsync(editor, runId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(runId);
        Assert.NotNull(stored.AbandonedByMembershipId);
        Assert.NotEqual(stored.CreatedByMembershipId, stored.AbandonedByMembershipId);
        var stopper = await InWorkspaceAsync(db => db.WorkspaceMemberships.AsNoTracking()
            .SingleAsync(membership => membership.Id == stored.AbandonedByMembershipId, Ct));
        Assert.Equal(WorkspaceRole.Editor, stopper.Role);
        Assert.Equal(stopper.UserId, Assert.Single(await AuditAsync(runId)).ActorUserId);
    }

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does_and_is_left_running()
    {
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var started = await BodyOf(await ownerB.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/workflow-runs", new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct));
        var theirId = started.GetProperty("id").GetGuid();
        var theirs = started.GetProperty("concurrencyToken").GetString()!;
        using var client = await OwnerAsync();

        // Even with the right token: workspace A cannot name it.
        var foreign = await AbandonAsync(client, theirId, theirs, "Not yours to stop");
        var unknown = await AbandonAsync(client, Guid.NewGuid(), theirs, "Not yours to stop");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)));

        var stillTheirs = await InWorkspaceAsync(_fixture.WorkspaceB.Id, _fixture.WorkspaceB.Slug, db =>
            db.WorkflowRuns.AsNoTracking().SingleAsync(run => run.Id == theirId, Ct));
        Assert.Equal(WorkflowRunStatus.InProgress, stillTheirs.Status);
        Assert.Null(stillTheirs.AbandonedAt);
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceB.Id, _fixture.WorkspaceB.Slug, db =>
            db.AuditLogs.CountAsync(row => row.Action == WorkflowAuditActions.RunAbandoned, Ct)));

        // Nor is a stop of it recorded on this side.
        Assert.Equal(0, await AuditRowsAsync(theirId));
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    private Task<HttpResponseMessage> AbandonAsync(GatewayClient client, Guid runId, string? token, string? reason) =>
        client.PostAsJsonAsync($"{RunsIn}/{runId}/abandonment", new { confirmed = true, reason, expectedConcurrencyToken = token }, Ct);

    private Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string? token) =>
        client.PostAsJsonAsync($"{RunsIn}/{runId}/completion", new { expectedConcurrencyToken = token }, Ct);

    /// <summary>
    /// An organize-a-picture run started from a picture: its one required step is done at the start, so it could be
    /// finished — which is what makes stopping it, rather than finishing it, a choice.
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
        var response = await client.PostAsJsonAsync(
            RunsIn, new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return ((await BodyOf(response)).GetProperty("id").GetGuid(), assetId);
    }

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct))).GetProperty("concurrencyToken").GetString()!;

    private Task<WorkflowRun> StoredAsync(Guid runId) =>
        InWorkspaceAsync(db => db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct));

    private Task<List<AuditLog>> AuditAsync(Guid runId) =>
        InWorkspaceAsync(db => db.AuditLogs.AsNoTracking()
            .Where(row => row.Action == WorkflowAuditActions.RunAbandoned && row.ResourceId == runId.ToString("D"))
            .ToListAsync(Ct));

    private async Task<int> AuditRowsAsync(Guid runId) => (await AuditAsync(runId)).Count;

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.Status}|{run.CompletedAt:O}|{run.AbandonedAt:O}|"
                + $"{run.AbandonedByMembershipId}|{run.AbandonReason}|{run.UpdatedAt:O}|{run.CurrentStepKey}|{run.LastRoute}|"
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
                    $"{step.StepKey}:{step.Status}:{step.StartedAt:O}:{step.CompletedAt:O}:{step.SkippedAt:O}:{step.SkipReason}:"
                    + $"{step.DraftResourceKind}:{step.DraftResourceId}:{step.DraftSavedAt:O}"))
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

    /// <summary>A switch a test sets to make the next stop lose a race to a rival change.</summary>
    private sealed class StoreSwitch
    {
        /// <summary>A change applied just before the next stop, which then reports that it lost to it. Used once.</summary>
        public Func<Task>? LoseRaceTo { get; set; }
    }

    /// <summary>
    /// The real data layer, except that a stop can find a rival change landed first. The gateway fixture's store does
    /// not move a row version on its own, so a lost race is staged here — and, as the real stale path does, nothing
    /// the losing stop staged (its audit row included) is written.
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

        public async Task<WorkflowRunWrite> AbandonAsync(
            WorkflowRun run, byte[] expectedRowVersion, AuditEntry audit, CancellationToken cancellationToken)
        {
            if (store.LoseRaceTo is { } rival)
            {
                store.LoseRaceTo = null;
                await rival();

                return WorkflowRunWrite.Stale;
            }

            return await inner.AbandonAsync(run, expectedRowVersion, audit, cancellationToken);
        }

        public Task<WorkflowRunWrite> SaveAsync(
            WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken, IReadOnlyList<WorkflowRunLink>? newLinks = null) =>
            inner.SaveAsync(run, expectedRowVersion, cancellationToken, newLinks);
    }
}
