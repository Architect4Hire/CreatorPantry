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
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs/{runId}/steps/{stepKey}/save-and-exit</c> through the real Gateway (13A.10): the run is
/// left in progress with its draft and where the creator was, and "Continue" leads back there; nothing committed is
/// lost, a repeat writes nothing, a refusal writes nothing, who may, and two workspaces.
/// </summary>
public sealed class WorkflowRunSaveAndExitEndpointTests : IAsyncLifetime
{
    private const string MemberPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private readonly StoreSwitch _store = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real data layer, behind a switch that makes its next save fail or lose a race.
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

    // ---- Save ----

    [Fact]
    public async Task Save_and_exit_keeps_the_run_in_progress_with_its_draft_and_where_the_creator_was()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();
        var before = await ReadAsync(client, runId);

        var response = await SaveAndExitAsync(client, runId, "about", new
        {
            draft = new { kind = "CreativeContext", id = contextId },
            capabilityKey = WorkflowCapabilities.ImagesStudio,
            expectedConcurrencyToken = before.GetProperty("concurrencyToken").GetString(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);

        // Leaving is not a status: in progress, on the same step, nothing finished or stopped.
        Assert.Equal("InProgress", run.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("abandonedAt").ValueKind);
        Assert.Equal(0, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal("about", run.GetProperty("nextAction").GetProperty("stepKey").GetString());

        // The draft is the step's, and the first draft started it.
        var about = run.GetProperty("steps")[0];
        Assert.Equal("InProgress", about.GetProperty("status").GetString());
        Assert.Equal(contextId, about.GetProperty("draftResourceId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, about.GetProperty("draftSavedAt").ValueKind);

        Assert.Equal("image-studio", run.GetProperty("lastRoute").GetString());
        Assert.Equal("image-studio", run.GetProperty("resumeRoute").GetString());
        Assert.True(run.GetProperty("updatedAt").GetDateTimeOffset() > before.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_route_is_the_servers_filled_from_the_runs_own_records()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnPlanAsync(client);

        var response = await SaveAndExitAsync(client, runId, "plan", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudioContext,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"image-studio/context/{contextId:D}", (await BodyOf(response)).GetProperty("lastRoute").GetString());
        Assert.Equal(
            $"image-studio/context/{contextId:D}",
            await InWorkspaceAsync(db => db.WorkflowRuns.Where(run => run.Id == runId).Select(run => run.LastRoute).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Leaving_without_a_draft_keeps_the_one_the_step_holds()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();
        var saved = await BodyOf(await client.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/draft",
            new { kind = "CreativeContext", id = contextId, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct));
        var savedAt = saved.GetProperty("steps")[0].GetProperty("draftSavedAt").GetDateTimeOffset();

        var response = await SaveAndExitAsync(client, runId, "about", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudio,
            expectedConcurrencyToken = saved.GetProperty("concurrencyToken").GetString(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var about = (await BodyOf(response)).GetProperty("steps")[0];
        Assert.Equal(contextId, about.GetProperty("draftResourceId").GetGuid());
        Assert.Equal("CreativeContext", about.GetProperty("draftKind").GetString());
        Assert.Equal(savedAt, about.GetProperty("draftSavedAt").GetDateTimeOffset());
    }

    // ---- Resume ----

    [Fact]
    public async Task Reading_the_run_later_carries_on_from_where_it_was_saved()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnPlanAsync(client);
        await SaveAndExitAsync(client, runId, "plan", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudioContext,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        // Another session: a fresh sign-in reads the run as it was left.
        using var later = await OwnerAsync();
        var run = await ReadAsync(later, runId);

        Assert.Equal("InProgress", run.GetProperty("status").GetString());
        Assert.Equal("plan", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Equal($"image-studio/context/{contextId:D}", run.GetProperty("resumeRoute").GetString());
        Assert.Equal(contextId, run.GetProperty("steps")[0].GetProperty("draftResourceId").GetGuid());
    }

    [Fact]
    public async Task Completing_the_step_left_moves_continue_on_to_the_next_step()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();
        await SaveAndExitAsync(client, runId, "about", new
        {
            draft = new { kind = "CreativeContext", id = contextId },
            capabilityKey = WorkflowCapabilities.ImagesStudio,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        var completed = await BodyOf(await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct));

        // Not the studio's front page the creator left from, but the next step's own page.
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("lastRoute").ValueKind);
        Assert.Equal($"image-studio/context/{contextId:D}", completed.GetProperty("resumeRoute").GetString());
    }

    [Fact]
    public async Task Skipping_the_step_left_moves_continue_on_to_the_next_step()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnPlanAsync(client);
        await SaveAndExitAsync(client, runId, "plan", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudioContext,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        var skipped = await BodyOf(await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/plan/skip", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct));

        Assert.Equal(JsonValueKind.Null, skipped.GetProperty("lastRoute").ValueKind);
        Assert.Equal("make-and-keep", skipped.GetProperty("nextAction").GetProperty("stepKey").GetString());
    }

    // ---- Replay ----

    [Fact]
    public async Task The_same_save_and_exit_twice_writes_once_whatever_the_token()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();
        var body = new Dictionary<string, object?>
        {
            ["draft"] = new { kind = "CreativeContext", id = contextId },
            ["capabilityKey"] = WorkflowCapabilities.ImagesStudio,
            ["expectedConcurrencyToken"] = await TokenAsync(client, runId),
        };

        var first = await BodyOf(await SaveAndExitAsync(client, runId, "about", body));
        var after = await SnapshotAsync(runId);
        body["expectedConcurrencyToken"] = NeverHeld;
        var again = await SaveAndExitAsync(client, runId, "about", body);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first.GetProperty("concurrencyToken").GetString(), (await BodyOf(again)).GetProperty("concurrencyToken").GetString());
        Assert.Equal(after, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Leaving_with_nothing_new_writes_nothing()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var before = await SnapshotAsync(runId);

        var response = await SaveAndExitAsync(client, runId, "about", new { expectedConcurrencyToken = NeverHeld });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    // ---- Failure ----

    [Fact]
    public async Task A_save_and_exit_against_a_run_that_has_changed_is_refused_with_what_it_holds_now()
    {
        using var client = await OwnerAsync();
        var (runId, _) = await RunOnPlanAsync(client);
        var before = await SnapshotAsync(runId);

        var response = await SaveAndExitAsync(client, runId, "plan", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudioContext,
            expectedConcurrencyToken = NeverHeld,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.Equal(await TokenAsync(client, runId), body.GetProperty("currentConcurrencyToken").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastRoute").ValueKind);
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task Each_refusal_writes_nothing()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, runId);
        var before = await SnapshotAsync(runId);

        // An action another step offers, not this one.
        var otherStep = await SaveAndExitAsync(client, runId, "about", new { capabilityKey = WorkflowCapabilities.ImagesStudioContext, expectedConcurrencyToken = token });
        Assert.Equal(HttpStatusCode.BadRequest, otherStep.StatusCode);
        var otherBody = await BodyOf(otherStep);
        Assert.Equal(WorkflowErrorCodes.SaveAndExitInvalid, Code(otherBody));
        Assert.True(otherBody.GetProperty("errors").TryGetProperty("capabilityKey", out _));

        // No such action at all.
        var unknown = await SaveAndExitAsync(client, runId, "about", new { capabilityKey = "nowhere.at-all", expectedConcurrencyToken = token });
        Assert.Equal(WorkflowErrorCodes.SaveAndExitInvalid, Code(await BodyOf(unknown)));

        // A draft of a kind the step does not keep, and one this workspace does not have.
        var wrongKind = await SaveAndExitAsync(client, runId, "about", new { draft = new { kind = "MediaAsset", id = Guid.NewGuid() }, expectedConcurrencyToken = token });
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        var wrongBody = await BodyOf(wrongKind);
        Assert.Equal(WorkflowErrorCodes.StepDraftInvalid, Code(wrongBody));
        Assert.True(wrongBody.GetProperty("errors").TryGetProperty("draft.kind", out _));

        var missing = await SaveAndExitAsync(client, runId, "about", new { draft = new { kind = "CreativeContext", id = Guid.NewGuid() }, expectedConcurrencyToken = token });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepDraftUnprocessable, Code(await BodyOf(missing)));

        // A step that is not the one the run is on, one the run does not have, and no token.
        var notCurrent = await BodyOf(await SaveAndExitAsync(client, runId, "plan", new { expectedConcurrencyToken = token }));
        Assert.Equal(WorkflowErrorCodes.StepNotCurrent, Code(notCurrent));
        Assert.Equal("about", notCurrent.GetProperty("currentStepKey").GetString());
        Assert.Equal(WorkflowErrorCodes.StepNotFound, Code(await BodyOf(await SaveAndExitAsync(client, runId, "nowhere", new { expectedConcurrencyToken = token }))));
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAndExitAsync(client, runId, "about", new { capabilityKey = WorkflowCapabilities.ImagesStudio })).StatusCode);

        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task An_action_whose_page_needs_a_record_the_run_does_not_hold_has_nowhere_to_come_back_to()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");

        // On the lead-picture step with no recipe linked: its page is the recipe's, and there is none.
        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            foreach (var step in await db.WorkflowStepRuns.Where(step => step.WorkflowRunId == runId && step.StepKey != "lead-picture").ToListAsync(Ct))
            {
                step.Status = WorkflowStepRunStatus.Completed;
                step.CompletedAt = DateTimeOffset.UtcNow;
                step.CompletedByMembershipId = run.CreatedByMembershipId;
            }

            run.CompletedStepCount = 3;
            run.CurrentStepKey = "lead-picture";
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var before = await SnapshotAsync(runId);

        var response = await SaveAndExitAsync(client, runId, "lead-picture", new
        {
            capabilityKey = WorkflowCapabilities.RecipesLeadPicture,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.LastRouteUnprocessable, Code(await BodyOf(response)));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_finished_run_and_a_finished_step_are_not_saved_for_later()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnPlanAsync(client);

        // Every step done, the run waiting on its last: there is no work on it to come back to.
        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            foreach (var step in await db.WorkflowStepRuns.Where(step => step.WorkflowRunId == runId).ToListAsync(Ct))
            {
                step.Status = WorkflowStepRunStatus.Completed;
                step.CompletedAt ??= DateTimeOffset.UtcNow;
                step.CompletedByMembershipId ??= run.CreatedByMembershipId;
            }

            run.CompletedStepCount = 4;
            run.CurrentStepKey = "lead-picture";
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var done = await SaveAndExitAsync(client, runId, "lead-picture", new
        {
            draft = new { kind = "CreativeContext", id = contextId },
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });
        Assert.Equal(HttpStatusCode.Conflict, done.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepAlreadyDone, Code(await BodyOf(done)));

        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var before = await SnapshotAsync(runId);

        var finished = await SaveAndExitAsync(client, runId, "lead-picture", new { expectedConcurrencyToken = await TokenAsync(client, runId) });

        Assert.Equal(HttpStatusCode.Conflict, finished.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(finished)));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_save_and_exit_that_fails_is_not_reported_as_saved_and_leaves_the_run_as_it_was()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();
        var body = new
        {
            draft = new { kind = "CreativeContext", id = contextId },
            capabilityKey = WorkflowCapabilities.ImagesStudio,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        };
        var before = await SnapshotAsync(runId);

        _store.Fails = true;
        HttpResponseMessage response;
        try
        {
            response = await SaveAndExitAsync(client, runId, "about", body);
        }
        finally
        {
            _store.Fails = false;
        }

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(runId));

        // And the same save and exit, once the store is back, lands.
        var again = await SaveAndExitAsync(client, runId, "about", body);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("image-studio", (await BodyOf(again)).GetProperty("resumeRoute").GetString());
    }

    // ---- Lost races: judged as if the request had come second ----

    [Fact]
    public async Task Losing_to_the_same_save_and_exit_is_the_success_it_would_have_been()
    {
        using var client = await OwnerAsync();
        var (runId, contextId) = await RunOnPlanAsync(client);
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) => run.LastRoute = $"image-studio/context/{contextId:D}");

        var response = await SaveAndExitAsync(client, runId, "plan", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudioContext,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"image-studio/context/{contextId:D}", (await BodyOf(response)).GetProperty("resumeRoute").GetString());
    }

    [Fact]
    public async Task Losing_to_a_completion_of_the_step_is_not_a_save_for_later_even_with_the_same_draft()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();

        // The rival completed the step on this very draft and moved the run on.
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, steps) =>
        {
            var about = steps.Single(step => step.StepKey == "about");
            about.Status = WorkflowStepRunStatus.Completed;
            about.CompletedAt = DateTimeOffset.UtcNow;
            about.CompletedByMembershipId = run.CreatedByMembershipId;
            about.DraftResourceKind = WorkflowResourceKind.CreativeContext;
            about.DraftResourceId = contextId;
            about.DraftSavedAt = DateTimeOffset.UtcNow;
            run.CompletedStepCount = 1;
            run.CurrentStepKey = "plan";
        });

        var response = await SaveAndExitAsync(client, runId, "about", new
        {
            draft = new { kind = "CreativeContext", id = contextId },
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepNotCurrent, Code(body));
        Assert.Equal("plan", body.GetProperty("currentStepKey").GetString());
    }

    [Fact]
    public async Task Losing_to_the_run_being_stopped_says_it_is_finished()
    {
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        _store.LoseRaceTo = () => ChangeRunAsync(runId, (run, _) =>
        {
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
        });

        var response = await SaveAndExitAsync(client, runId, "about", new
        {
            capabilityKey = WorkflowCapabilities.ImagesStudio,
            expectedConcurrencyToken = await TokenAsync(client, runId),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(response)));
    }

    // ---- Who may, and isolation ----

    [Fact]
    public async Task A_contributor_cannot_save_someone_elses_run_and_a_viewer_cannot_save_any()
    {
        using var owner = await OwnerAsync();
        var runId = await StartAsync(owner, "images.create");
        var body = new { capabilityKey = WorkflowCapabilities.ImagesStudio, expectedConcurrencyToken = await TokenAsync(owner, runId) };
        var before = await SnapshotAsync(runId);

        using var contributor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Contributor), MemberPassword, Ct);
        var refused = await SaveAndExitAsync(contributor, runId, "about", body);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(await BodyOf(refused)));

        using var viewer = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Viewer), MemberPassword, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAndExitAsync(viewer, runId, "about", body)).StatusCode);

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

        var foreign = await SaveAndExitAsync(client, theirs, "about", new { expectedConcurrencyToken = NeverHeld });
        var unknown = await SaveAndExitAsync(client, Guid.NewGuid(), "about", new { expectedConcurrencyToken = NeverHeld });

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)));
    }

    [Fact]
    public async Task Another_workspaces_record_is_no_draft_here_and_answers_as_an_unknown_one_does()
    {
        var theirs = await InWorkspaceAsync(_fixture.WorkspaceB.Id, _fixture.WorkspaceB.Slug, async db =>
        {
            var context = CreativeContextSeeds.NewContext(DateTimeOffset.UtcNow);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });
        using var client = await OwnerAsync();
        var runId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, runId);
        var before = await SnapshotAsync(runId);

        var foreign = await SaveAndExitAsync(client, runId, "about", new { draft = new { kind = "CreativeContext", id = theirs }, expectedConcurrencyToken = token });
        var unknown = await SaveAndExitAsync(client, runId, "about", new { draft = new { kind = "CreativeContext", id = Guid.NewGuid() }, expectedConcurrencyToken = token });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(await BodyOf(foreign))));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    private Task<HttpResponseMessage> SaveAndExitAsync(GatewayClient client, Guid runId, string stepKey, object body) =>
        client.PostAsJsonAsync($"{RunsIn}/{runId}/steps/{stepKey}/save-and-exit", body, Ct);

    private async Task<Guid> StartAsync(GatewayClient client, string definitionKey)
    {
        var response = await client.PostAsJsonAsync(RunsIn, new { definitionKey }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private Task<Guid> SeedContextAsync() =>
        InWorkspaceAsync(async db =>
        {
            var context = CreativeContextSeeds.NewContext(DateTimeOffset.UtcNow);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

    /// <summary>A pictures run on its plan step, as a creator gets there: the idea saved as the first step's draft and the step completed from it.</summary>
    private async Task<(Guid RunId, Guid ContextId)> RunOnPlanAsync(GatewayClient client)
    {
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/draft",
            new { kind = "CreativeContext", id = contextId, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/about/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct)).StatusCode);

        return (runId, contextId);
    }

    private async Task<JsonElement> ReadAsync(GatewayClient client, Guid runId)
    {
        var response = await client.GetAsync($"{RunsIn}/{runId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.Status}|{run.UpdatedAt:O}|{run.CurrentStepKey}|{run.LastRoute}|"
                + string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.DraftResourceKind}:{step.DraftResourceId}:{step.DraftSavedAt:O}"));
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

    /// <summary>Switches a test sets to make the next save fail, or lose a race to a rival change.</summary>
    private sealed class StoreSwitch
    {
        private volatile bool _fails;

        public bool Fails { get => _fails; set => _fails = value; }

        /// <summary>A change applied just before the next save, which then reports that it lost to it. Used once.</summary>
        public Func<Task>? LoseRaceTo { get; set; }
    }

    /// <summary>
    /// The real data layer, except that a save can throw before anything is written, or find a rival change landed
    /// first. The gateway fixture's store does not move a row version on its own, so a lost race is staged here.
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
            if (store.Fails)
            {
                throw new InvalidOperationException("The store is unavailable.");
            }

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
