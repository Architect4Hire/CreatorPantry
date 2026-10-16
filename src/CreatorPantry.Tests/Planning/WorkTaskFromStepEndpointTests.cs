using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using CreatorPantry.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// "Add to my plan" from a workflow step through the real Gateway (13A.29): a suggestion that writes nothing, a
/// confirm that makes one task per person per step and answers with it after, a replay, a task whose origin
/// follows its link, a run and step left exactly as they were, and the steps with nothing left to plan.
/// </summary>
/// <remarks>
/// That two confirms racing make one task is <see cref="WorkTaskSqlServerTests"/>' to prove, against the index.
/// </remarks>
public sealed class WorkTaskFromStepEndpointTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";

    /// <summary>
    /// Thursday lunchtime in <see cref="Zone"/>, years ahead of the real date on purpose: the gateway fixture's
    /// cookies are stamped from this clock and judged by the client's real one. 2038 has 2027's calendar.
    /// </summary>
    private static readonly DateTimeOffset Thursday = new(2038, 10, 14, 17, 0, 0, TimeSpan.Zero);

    private const string ThisMonday = "2038-10-11";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(time: new FakeTimeProvider(Thursday));

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.BrandProfiles.Add(new BrandProfile
            {
                Id = Guid.NewGuid(),
                BrandName = "Sam's Kitchen",
                TimeZoneId = Zone,
                CreatedByMembershipId = Guid.NewGuid(),
                UpdatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Thursday,
                UpdatedAt = Thursday,
            });

            return await db.SaveChangesAsync(Ct);
        });
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private string Path(SeededWorkspace? workspace = null) =>
        $"/api/v1/workspaces/{(workspace ?? _fixture.WorkspaceA).Slug}/work-tasks/from-workflow-step";

    private Task<HttpResponseMessage> SuggestAsync(GatewayClient client, Guid runId, string stepKey, SeededWorkspace? workspace = null) =>
        client.GetAsync($"{Path(workspace)}?workflowRunId={runId}&workflowStepKey={Uri.EscapeDataString(stepKey)}", Ct);

    private Task<HttpResponseMessage> AddAsync(GatewayClient client, object body, string? key = null, SeededWorkspace? workspace = null) =>
        key is null ? client.PostAsJsonAsync(Path(workspace), body, Ct) : client.PostAsJsonAsync(Path(workspace), body, key, Ct);

    private Task<int> CountAsync() =>
        InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.CountAsync(Ct));

    // ---- Suggest ----

    [Fact]
    public async Task A_suggestion_offers_the_steps_own_title_and_no_estimate_and_writes_nothing()
    {
        var (runId, stepKey, stepTitle, runTitle) = await StartRunAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await SuggestAsync(client, runId, stepKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var suggestion = await BodyOf(response);

        Assert.Equal(stepTitle, suggestion.GetProperty("title").GetString());
        Assert.Equal(stepTitle, suggestion.GetProperty("stepTitle").GetString());
        Assert.Equal(runTitle, suggestion.GetProperty("runTitle").GetString());
        Assert.Equal($"From your “{runTitle}” workflow.", suggestion.GetProperty("description").GetString());
        Assert.True(IsNull(suggestion, "estimateMinutes"));
        Assert.True(IsNull(suggestion, "scheduledDate"));
        Assert.Equal(ThisMonday, suggestion.GetProperty("planningWeekStart").GetString());
        Assert.Equal($"workflows/runs/{runId:D}", suggestion.GetProperty("workflowRunRoute").GetString());
        Assert.True(IsNull(suggestion, "existingTask"));

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task A_workspace_with_no_time_zone_is_offered_no_week_rather_than_a_guessed_one()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();
        await InWorkspaceAsync(_fixture.WorkspaceA, db => db.BrandProfiles
            .ExecuteUpdateAsync(set => set.SetProperty(profile => profile.TimeZoneId, (string?)null), Ct));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var suggestion = await BodyOf(await SuggestAsync(client, runId, stepKey));

        Assert.True(IsNull(suggestion, "planningWeekStart"));
    }

    // ---- Confirm ----

    [Fact]
    public async Task Confirming_unchanged_makes_the_suggested_task_once_and_after_that_answers_with_it()
    {
        var (runId, stepKey, stepTitle, runTitle) = await StartRunAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await BodyOf(first);
        Assert.True(created.GetProperty("created").GetBoolean());

        var task = created.GetProperty("task");
        Assert.Equal(stepTitle, task.GetProperty("title").GetString());
        Assert.Equal($"From your “{runTitle}” workflow.", task.GetProperty("description").GetString());
        Assert.Equal("Backlog", task.GetProperty("status").GetString());
        Assert.Equal(ThisMonday, task.GetProperty("planningWeekStart").GetString());
        Assert.True(IsNull(task, "estimateMinutes"));
        Assert.Equal(runId, task.GetProperty("links").GetProperty("workflowRunId").GetGuid());
        Assert.Equal(stepKey, task.GetProperty("links").GetProperty("workflowStepKey").GetString());
        Assert.Equal($"workflows/runs/{runId:D}", task.GetProperty("links").GetProperty("workflowRunRoute").GetString());

        var taskId = task.GetProperty("id").GetGuid();
        Assert.Equal(WorkTaskOrigin.WorkflowStep, (await InWorkspaceAsync(_fixture.WorkspaceA, db =>
            db.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == taskId, Ct))).Origin);

        var again = await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var existing = await BodyOf(again);
        Assert.False(existing.GetProperty("created").GetBoolean());
        Assert.Equal(taskId, existing.GetProperty("task").GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAsync());

        var suggestion = await BodyOf(await SuggestAsync(client, runId, stepKey));
        Assert.Equal(taskId, suggestion.GetProperty("existingTask").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task What_the_creator_changed_before_confirming_is_kept()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await AddAsync(client, new
        {
            workflowRunId = runId,
            workflowStepKey = stepKey,
            title = "  Pick the blue plate shot  ",
            description = "",
            priority = "High",
            estimateMinutes = 45,
            scheduledDate = "2038-10-15",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = (await BodyOf(response)).GetProperty("task");
        Assert.Equal("Pick the blue plate shot", task.GetProperty("title").GetString());
        Assert.True(IsNull(task, "description"));
        Assert.Equal("High", task.GetProperty("priority").GetString());
        Assert.Equal(45, task.GetProperty("estimateMinutes").GetInt32());
        Assert.Equal("2038-10-15", task.GetProperty("scheduledDate").GetString());
        Assert.Equal(ThisMonday, task.GetProperty("planningWeekStart").GetString());
    }

    [Fact]
    public async Task A_replay_returns_the_first_answer_and_the_same_key_with_another_request_is_refused()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();
        var key = Guid.NewGuid().ToString();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var body = new { workflowRunId = runId, workflowStepKey = stepKey };

        Assert.Equal(HttpStatusCode.Created, (await AddAsync(client, body, key)).StatusCode);

        var again = await AddAsync(client, body, key);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotent-Replayed")));
        Assert.True((await BodyOf(again)).GetProperty("created").GetBoolean());

        var reused = await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey, title = "Another" }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency.key_reused", Code(await BodyOf(reused)));
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task A_task_edited_off_its_step_is_no_longer_that_steps_task_and_the_step_can_be_planned_again()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = (await BodyOf(await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey }))).GetProperty("task");
        var taskId = task.GetProperty("id").GetGuid();

        var edit = await client.PatchAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/{taskId}",
            new { expectedConcurrencyToken = task.GetProperty("concurrencyToken").GetString(), workflowStepKey = (string?)null },
            Ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(WorkTaskOrigin.Capture, (await InWorkspaceAsync(_fixture.WorkspaceA, db =>
            db.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == taskId, Ct))).Origin);

        var replanned = await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey });
        Assert.Equal(HttpStatusCode.Created, replanned.StatusCode);
        Assert.NotEqual(taskId, (await BodyOf(replanned)).GetProperty("task").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Adding_a_step_to_the_plan_and_finishing_the_task_leave_the_run_exactly_as_it_was()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var before = await RunAsync(client, runId);

        var task = (await BodyOf(await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey }))).GetProperty("task");
        var complete = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/{task.GetProperty("id").GetGuid()}/complete",
            new { expectedConcurrencyToken = task.GetProperty("concurrencyToken").GetString() },
            Ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var after = await RunAsync(client, runId);
        Assert.Equal(before.GetProperty("steps").ToString(), after.GetProperty("steps").ToString());
        Assert.Equal(before.GetProperty("links").ToString(), after.GetProperty("links").ToString());
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
    }

    // ---- Nothing left to plan ----

    [Fact]
    public async Task A_run_no_longer_in_progress_or_a_step_already_done_has_nothing_left_to_plan()
    {
        var (abandonedRun, abandonedStep, _, _) = await StartRunAsync();
        var (openRun, doneStep, _, _) = await StartRunAsync();
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(row => row.Id == abandonedRun, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = Thursday;

            var step = await db.WorkflowStepRuns.SingleAsync(row => row.WorkflowRunId == openRun && row.StepKey == doneStep, Ct);
            step.Status = WorkflowStepRunStatus.Completed;
            step.CompletedAt = Thursday;
            step.CompletedByMembershipId = owner;

            return await db.SaveChangesAsync(Ct);
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        foreach (var (runId, stepKey, field) in new[] { (abandonedRun, abandonedStep, "workflowRunId"), (openRun, doneStep, "workflowStepKey") })
        {
            var suggested = await SuggestAsync(client, runId, stepKey);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, suggested.StatusCode);
            Assert.Equal(PlanningErrorCodes.WorkTaskStepUnprocessable, Code(await BodyOf(suggested)));

            var added = await AddAsync(client, new { workflowRunId = runId, workflowStepKey = stepKey });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, added.StatusCode);
            var problem = await BodyOf(added);
            Assert.Equal(PlanningErrorCodes.WorkTaskStepUnprocessable, Code(problem));
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        }

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task An_unknown_step_or_a_run_this_workspace_does_not_have_is_refused_as_capture_refuses_one()
    {
        var (runId, _, _, _) = await StartRunAsync();
        var theirs = await SeedRunInAsync(_fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var noSuchStep = await AddAsync(client, new { workflowRunId = runId, workflowStepKey = "no-such-step" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSuchStep.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskLinkUnprocessable, Code(await BodyOf(noSuchStep)));

        var foreign = await AddAsync(client, new { workflowRunId = theirs, workflowStepKey = "start" });
        var invented = await AddAsync(client, new { workflowRunId = Guid.NewGuid(), workflowStepKey = "start" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invented.StatusCode);

        var (foreignBody, inventedBody) = (await BodyOf(foreign), await BodyOf(invented));
        Assert.Equal(PlanningErrorCodes.WorkTaskLinkUnprocessable, Code(foreignBody));
        Assert.Equal(
            inventedBody.GetProperty("errors").GetProperty("workflowRunId").ToString(),
            foreignBody.GetProperty("errors").GetProperty("workflowRunId").ToString());

        var suggestForeign = await SuggestAsync(client, theirs, "start");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, suggestForeign.StatusCode);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task The_run_and_step_are_required()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await AddAsync(client, new { title = "No step" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await BodyOf(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("workflowRunId", out _));
        Assert.True(errors.TryGetProperty("workflowStepKey", out _));
    }

    // ---- Who ----

    [Fact]
    public async Task Each_member_plans_the_same_step_for_themselves_and_a_viewer_may_not()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        using var viewerOfB = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var ownersTask = (await BodyOf(await AddAsync(asOwner, new { workflowRunId = runId, workflowStepKey = stepKey }))).GetProperty("task");
        var editors = await AddAsync(asEditor, new { workflowRunId = runId, workflowStepKey = stepKey });
        Assert.Equal(HttpStatusCode.Created, editors.StatusCode);
        Assert.NotEqual(ownersTask.GetProperty("id").GetGuid(), (await BodyOf(editors)).GetProperty("task").GetProperty("id").GetGuid());
        Assert.Equal(2, await CountAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(viewerOfB, new { workflowRunId = runId, workflowStepKey = stepKey }, workspace: _fixture.WorkspaceB)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SuggestAsync(viewerOfB, runId, stepKey, _fixture.WorkspaceB)).StatusCode);
    }

    [Fact]
    public async Task A_colleagues_suggestion_never_shows_someone_elses_task_for_the_step()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var ownersTask = (await BodyOf(await AddAsync(asOwner, new { workflowRunId = runId, workflowStepKey = stepKey }))).GetProperty("task");

        var editorsSuggestion = await BodyOf(await SuggestAsync(asEditor, runId, stepKey));
        Assert.True(IsNull(editorsSuggestion, "existingTask"));

        var editorsTask = (await BodyOf(await AddAsync(asEditor, new { workflowRunId = runId, workflowStepKey = stepKey }))).GetProperty("task");
        var afterwards = await BodyOf(await SuggestAsync(asEditor, runId, stepKey));

        Assert.Equal(editorsTask.GetProperty("id").GetGuid(), afterwards.GetProperty("existingTask").GetProperty("id").GetGuid());
        Assert.NotEqual(ownersTask.GetProperty("id").GetGuid(), afterwards.GetProperty("existingTask").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task One_key_used_by_two_people_is_two_requests_and_never_replays_the_others_task()
    {
        var (runId, stepKey, _, _) = await StartRunAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { workflowRunId = runId, workflowStepKey = stepKey };

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var owners = await AddAsync(asOwner, body, key);
        var editors = await AddAsync(asEditor, body, key);

        Assert.Equal(HttpStatusCode.Created, editors.StatusCode);
        Assert.False(editors.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(
            (await BodyOf(owners)).GetProperty("task").GetProperty("id").GetGuid(),
            (await BodyOf(editors)).GetProperty("task").GetProperty("id").GetGuid());
        Assert.Equal(2, await CountAsync());
    }

    // ---- Helpers ----

    private static bool IsNull(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>A real run, so its steps are the ones a task may be made from.</summary>
    private async Task<(Guid RunId, string StepKey, string StepTitle, string RunTitle)> StartRunAsync()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs",
            new { definitionKey = "images.create" },
            Guid.NewGuid().ToString(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await BodyOf(response);

        // A step not yet done: the first is the one a fresh run is on.
        var step = run.GetProperty("steps").EnumerateArray().First(candidate => candidate.GetProperty("status").GetString() != "Completed");

        return (run.GetProperty("id").GetGuid(), step.GetProperty("key").GetString()!, step.GetProperty("title").GetString()!,
            run.GetProperty("title").GetString()!);
    }

    private async Task<JsonElement> RunAsync(GatewayClient client, Guid runId)
    {
        var response = await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs/{runId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private Task<Guid> SeedRunInAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var run = WorkflowRunSeeds.Run(Thursday);
            db.WorkflowRuns.Add(run);
            db.WorkflowStepRuns.Add(WorkflowRunSeeds.Step(run, "start"));
            await db.SaveChangesAsync(Ct);

            return run.Id;
        });

    private Task<Guid> MembershipAsync(SeededWorkspace workspace, WorkspaceRole role) =>
        InWorkspaceAsync(workspace, db => db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.WorkspaceId == workspace.Id && membership.Role == role)
            .Select(membership => membership.Id)
            .SingleAsync(Ct));

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
