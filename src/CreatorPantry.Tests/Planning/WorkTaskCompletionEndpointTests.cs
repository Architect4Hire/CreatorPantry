using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// <c>POST .../work-tasks/{taskId}/complete</c> and <c>/reopen</c> through the real Gateway (13A.28b): finishing
/// from every open status and coming back to it, on the same day and in the same place; the transitions that are
/// not allowed; a replay that changes nothing more; and a task nobody finishes but the person doing it.
/// </summary>
/// <remarks>
/// That two completions racing write nothing the second time is <see cref="WorkTaskSqlServerTests"/>' to prove:
/// under SQLite a row version never changes.
/// </remarks>
public sealed class WorkTaskCompletionEndpointTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";

    /// <summary>
    /// Thursday lunchtime in <see cref="Zone"/>, years ahead of the real date on purpose: the gateway fixture's
    /// cookies are stamped from this clock and judged by the client's real one. 2038 has 2027's calendar.
    /// </summary>
    private static readonly DateTimeOffset Thursday = new(2038, 10, 14, 17, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly ThisMonday = new(2038, 10, 11);

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

    private static string Token(WorkTask task) => WorkTaskConcurrencyToken.From(task.RowVersion);

    private static Task<HttpResponseMessage> SendAsync(
        GatewayClient client, SeededWorkspace workspace, Guid taskId, string action, string token, string? key = null)
    {
        var path = $"/api/v1/workspaces/{workspace.Slug}/work-tasks/{taskId}/{action}";
        var body = new { expectedConcurrencyToken = token };

        return key is null ? client.PostAsJsonAsync(path, body, Ct) : client.PostAsJsonAsync(path, body, key, Ct);
    }

    private async Task<JsonElement> OkAsync(GatewayClient client, Guid taskId, string action, string token)
    {
        var response = await SendAsync(client, _fixture.WorkspaceA, taskId, action, token);
        var body = await BodyOf(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {body}");

        return body;
    }

    private async Task<JsonElement> GetAsync(GatewayClient client, string view)
    {
        var response = await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/{view}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static IReadOnlyList<string> Titles(JsonElement tasks) =>
        [.. tasks.EnumerateArray().Select(task => task.GetProperty("title").GetString()!)];

    /// <summary>The list a not-finished task of <paramref name="status"/> planned for Monday reads in, in the week.</summary>
    private static IReadOnlyList<string> ListOf(JsonElement week, WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.InProgress => Titles(week.GetProperty("inProgress").GetProperty("tasks")),
        WorkTaskStatus.Waiting => Titles(week.GetProperty("waiting").GetProperty("tasks")),
        _ => Titles(week.GetProperty("days")[0].GetProperty("tasks")),
    };

    // ---- Complete and reopen ----

    [Theory]
    [InlineData(WorkTaskStatus.Backlog)]
    [InlineData(WorkTaskStatus.InProgress)]
    [InlineData(WorkTaskStatus.Waiting)]
    public async Task Finishing_keeps_the_plan_and_reopening_puts_the_task_back_exactly_where_it_stood(WorkTaskStatus status)
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var before = Monday("Before", mine, status, 1024);
        var task = Monday("The task", mine, status, 2048);
        var after = Monday("After", mine, status, 3072);
        await SeedAsync(before, task, after);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var finished = await OkAsync(client, task.Id, "complete", Token(task));
        Assert.Equal("Done", finished.GetProperty("status").GetString());
        Assert.Equal(Thursday, finished.GetProperty("completedAt").GetDateTimeOffset());

        var stored = await StoredAsync(task.Id);
        Assert.Equal(WorkTaskStatus.Done, stored.Status);
        Assert.Equal(status, stored.CompletedFromStatus);
        Assert.Equal(mine, stored.CompletedByMembershipId);
        Assert.Equal(ThisMonday, stored.ScheduledDate);
        Assert.Equal(ThisMonday, stored.PlanningWeekStart);
        Assert.Equal(2048, stored.SortRank);

        // It reads as finished today and this week, and is in its old list no longer.
        var week = await GetAsync(client, "week");
        Assert.Equal(["The task"], Titles(week.GetProperty("completed").GetProperty("tasks")));
        Assert.Equal(["Before", "After"], ListOf(week, status));
        Assert.Equal(["The task"], Titles((await GetAsync(client, "today")).GetProperty("completedToday")));

        var reopened = await OkAsync(client, task.Id, "reopen", WorkTaskConcurrencyToken.From(stored.RowVersion));
        Assert.Equal(status.ToString(), reopened.GetProperty("status").GetString());

        stored = await StoredAsync(task.Id);
        Assert.Null(stored.CompletedAt);
        Assert.Null(stored.CompletedByMembershipId);
        Assert.Null(stored.CompletedFromStatus);
        Assert.Equal(ThisMonday, stored.ScheduledDate);

        week = await GetAsync(client, "week");
        Assert.Equal(["Before", "The task", "After"], ListOf(week, status));
        Assert.Empty(Titles(week.GetProperty("completed").GetProperty("tasks")));
    }

    [Fact]
    public async Task A_finished_task_is_not_finished_again_and_an_open_one_is_not_reopened()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var open = Monday("Open", mine, WorkTaskStatus.InProgress, 1024);
        var done = Monday("Done", mine, WorkTaskStatus.Done, 2048);
        done.CompletedAt = Thursday.AddHours(-1);
        done.CompletedByMembershipId = mine;
        done.CompletedFromStatus = WorkTaskStatus.Backlog;
        await SeedAsync(open, done);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var again = await SendAsync(client, _fixture.WorkspaceA, done.Id, "complete", Token(done));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskCompleted, Code(await BodyOf(again)));
        Assert.Equal(Thursday.AddHours(-1), (await StoredAsync(done.Id)).CompletedAt);

        var notFinished = await SendAsync(client, _fixture.WorkspaceA, open.Id, "reopen", Token(open));
        Assert.Equal(HttpStatusCode.Conflict, notFinished.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskOpen, Code(await BodyOf(notFinished)));
        Assert.Equal(WorkTaskStatus.InProgress, (await StoredAsync(open.Id)).Status);
    }

    [Fact]
    public async Task A_stale_request_writes_nothing_and_a_malformed_token_is_a_bad_request()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Monday("The task", mine, WorkTaskStatus.Backlog, 1024);
        await SeedAsync(task);

        var bytes = (byte[])task.RowVersion.Clone();
        bytes[^1] ^= 0xFF;

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var stale = await SendAsync(client, _fixture.WorkspaceA, task.Id, "complete", Convert.ToBase64String(bytes));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskStale, Code(await BodyOf(stale)));
        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(task.Id)).Status);

        var malformed = await SendAsync(client, _fixture.WorkspaceA, task.Id, "complete", "nope");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    // ---- Replay ----

    [Fact]
    public async Task A_replayed_completion_returns_the_first_answer_and_does_not_finish_a_reopened_task()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Monday("The task", mine, WorkTaskStatus.InProgress, 1024);
        await SeedAsync(task);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(client, _fixture.WorkspaceA, task.Id, "complete", Token(task), key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        await OkAsync(client, task.Id, "reopen", WorkTaskConcurrencyToken.From((await StoredAsync(task.Id)).RowVersion));

        var again = await SendAsync(client, _fixture.WorkspaceA, task.Id, "complete", Token(task), key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal("Done", (await BodyOf(again)).GetProperty("status").GetString());
        Assert.Equal(WorkTaskStatus.InProgress, (await StoredAsync(task.Id)).Status);
    }

    [Fact]
    public async Task One_key_reused_for_another_task_is_refused_and_finishes_nothing()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var one = Monday("One", mine, WorkTaskStatus.Backlog, 1024);
        var two = Monday("Two", mine, WorkTaskStatus.Backlog, 2048);
        await SeedAsync(one, two);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, _fixture.WorkspaceA, one.Id, "complete", Token(one), key)).StatusCode);

        var reused = await SendAsync(client, _fixture.WorkspaceA, two.Id, "complete", Token(two), key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency.key_reused", Code(await BodyOf(reused)));
        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(two.Id)).Status);
    }

    [Fact]
    public async Task One_key_for_completing_and_then_reopening_is_two_commands_and_a_reopen_replays_too()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Monday("The task", mine, WorkTaskStatus.Waiting, 1024);
        await SeedAsync(task);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        var completed = await SendAsync(client, _fixture.WorkspaceA, task.Id, "complete", Token(task), key);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var finishedToken = (await BodyOf(completed)).GetProperty("concurrencyToken").GetString()!;

        // The same key to reopen is not a replay of the completion: each command has its own name.
        var reopened = await SendAsync(client, _fixture.WorkspaceA, task.Id, "reopen", finishedToken, key);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.False(reopened.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("Waiting", (await BodyOf(reopened)).GetProperty("status").GetString());

        // And the reopen, sent again, replays its own first answer and changes nothing more.
        var again = await SendAsync(client, _fixture.WorkspaceA, task.Id, "reopen", finishedToken, key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(WorkTaskStatus.Waiting, (await StoredAsync(task.Id)).Status);
    }

    // ---- Whose work it is ----

    [Fact]
    public async Task Only_the_person_doing_a_task_finishes_or_reopens_it_not_even_the_owner_who_handed_it_on()
    {
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var handed = Monday("Handed on", editor, WorkTaskStatus.InProgress, 1024);
        handed.OwnerMembershipId = owner;
        await SeedAsync(handed);

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var byOwner = await SendAsync(asOwner, _fixture.WorkspaceA, handed.Id, "complete", Token(handed));
        Assert.Equal(HttpStatusCode.NotFound, byOwner.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskNotFound, Code(await BodyOf(byOwner)));

        var byEditor = await OkAsync(asEditor, handed.Id, "complete", Token(handed));
        Assert.Equal(editor, (await StoredAsync(handed.Id)).CompletedByMembershipId);

        var reopenByOwner = await SendAsync(
            asOwner, _fixture.WorkspaceA, handed.Id, "reopen", byEditor.GetProperty("concurrencyToken").GetString()!);
        Assert.Equal(HttpStatusCode.NotFound, reopenByOwner.StatusCode);
        Assert.Equal(WorkTaskStatus.Done, (await StoredAsync(handed.Id)).Status);
    }

    [Fact]
    public async Task Another_workspaces_task_does_not_exist_and_a_viewer_may_not_finish_one()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Monday("A's", mine, WorkTaskStatus.Backlog, 1024);
        await SeedAsync(task);

        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        using var viewerOfB = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        foreach (var action in new[] { "complete", "reopen" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(ownerOfB, _fixture.WorkspaceB, task.Id, action, Token(task))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(ownerOfB, _fixture.WorkspaceA, task.Id, action, Token(task))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(viewerOfB, _fixture.WorkspaceB, task.Id, action, Token(task))).StatusCode);
        }

        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(task.Id)).Status);
    }

    [Fact]
    public async Task Finishing_a_task_and_reopening_it_leave_its_workflow_step_exactly_as_it_was()
    {
        var (runId, stepKey) = await StartRunAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var stepsBefore = await RunStepsAsync(client, runId);

        var capture = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks",
            new { title = "Shoot the galette", workflowRunId = runId, workflowStepKey = stepKey },
            Ct);
        Assert.Equal(HttpStatusCode.Created, capture.StatusCode);
        var task = await BodyOf(capture);
        var taskId = task.GetProperty("id").GetGuid();

        var finished = await OkAsync(client, taskId, "complete", task.GetProperty("concurrencyToken").GetString()!);
        Assert.Equal(stepsBefore, await RunStepsAsync(client, runId));

        await OkAsync(client, taskId, "reopen", finished.GetProperty("concurrencyToken").GetString()!);
        Assert.Equal(stepsBefore, await RunStepsAsync(client, runId));
    }

    // ---- Helpers ----

    private static WorkTask Monday(string title, Guid assignee, WorkTaskStatus status, long rank) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = assignee,
            AssigneeMembershipId = assignee,
            Title = title,
            Status = status,
            ScheduledDate = ThisMonday,
            PlanningWeekStart = ThisMonday,
            SortRank = rank,
            CreatedAt = Thursday,
            UpdatedAt = Thursday,
        };

    private Task SeedAsync(params WorkTask[] tasks) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(tasks);

            return await db.SaveChangesAsync(Ct);
        });

    private Task<WorkTask> StoredAsync(Guid taskId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking().SingleAsync(task => task.Id == taskId, Ct));

    private Task<Guid> MembershipAsync(SeededWorkspace workspace, WorkspaceRole role) =>
        InWorkspaceAsync(workspace, db => db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.WorkspaceId == workspace.Id && membership.Role == role)
            .Select(membership => membership.Id)
            .SingleAsync(Ct));

    private async Task<string> RunStepsAsync(GatewayClient client, Guid runId)
    {
        var response = await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs/{runId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await BodyOf(response)).GetProperty("steps").ToString();
    }

    /// <summary>A real run, so its steps are the ones a task may name.</summary>
    private async Task<(Guid RunId, string StepKey)> StartRunAsync()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs",
            new { definitionKey = "images.create" },
            Guid.NewGuid().ToString(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await BodyOf(response);

        return (run.GetProperty("id").GetGuid(), run.GetProperty("steps")[0].GetProperty("key").GetString()!);
    }

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
