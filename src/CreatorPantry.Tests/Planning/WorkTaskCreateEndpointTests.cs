using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// <c>POST .../work-tasks</c> through the real Gateway (13A.24, TASK-002): the one-field contract, what the
/// server decides for itself, the links it checks, replay, the role bar, and the boundary between two
/// workspaces.
/// </summary>
public sealed class WorkTaskCreateEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// A Monday, 02:00 UTC — which in <see cref="Zone"/> is still the Sunday evening before, and so in the
    /// <em>previous</em> week. Every assertion about the defaulted week turns on that: a server reading its
    /// own UTC date would answer 2026-10-19, and the workspace's own calendar says 2026-10-12.
    /// </summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 2, 0, 0, TimeSpan.Zero);

    private const string Zone = "America/Chicago";

    /// <summary>The Monday beginning the week <see cref="Now"/> falls in, read in <see cref="Zone"/>.</summary>
    private const string CurrentWeek = "2026-10-12";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        // Both hosts share it: the API checks the gateway's token against this clock too.
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(time: new FakeTimeProvider(Now));

        // Workspace A keeps a calendar; workspace B deliberately does not, which is the "no zone set" case.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.BrandProfiles.Add(new BrandProfile
            {
                Id = Guid.NewGuid(),
                BrandName = "Sam's Kitchen",
                TimeZoneId = Zone,
                CreatedByMembershipId = Guid.NewGuid(),
                UpdatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Now,
                UpdatedAt = Now,
            });

            return await db.SaveChangesAsync(Ct);
        });
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TasksIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/work-tasks";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> CaptureAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? idempotencyKey = null) =>
        idempotencyKey is null
            ? client.PostAsJsonAsync(TasksIn(workspace), body, Ct)
            : client.PostAsJsonAsync(TasksIn(workspace), body, idempotencyKey, Ct);

    // ---- The contract ----

    [Fact]
    public async Task A_title_is_enough_and_the_server_decides_everything_else()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "  Shoot the galette  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        // A task has no read route yet, so there is nothing honest to point a Location at.
        Assert.Null(response.Headers.Location);

        var task = await BodyOf(response);

        Assert.Equal("Shoot the galette", task.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("description").ValueKind);
        Assert.Equal("Backlog", task.GetProperty("status").GetString());
        Assert.Equal("Normal", task.GetProperty("priority").GetString());

        // Captured, not planned for a day: it is in this week's backlog and on nobody's daily list.
        Assert.Equal(JsonValueKind.Null, task.GetProperty("scheduledDate").ValueKind);
        Assert.Equal(CurrentWeek, task.GetProperty("planningWeekStart").GetString());

        Assert.Equal(JsonValueKind.Null, task.GetProperty("dueDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("estimateMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("completedAt").ValueKind);

        // The caller owns it and is doing it, named the way a workflow run names people.
        var owner = task.GetProperty("owner");
        Assert.Equal(owner.GetProperty("membershipId").GetGuid(), task.GetProperty("assignee").GetProperty("membershipId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(owner.GetProperty("displayName").GetString()));

        var links = task.GetProperty("links");
        foreach (var name in new[] { "workflowRunId", "workflowStepKey", "recipeId", "contentCardId", "mediaAssetId" })
        {
            Assert.Equal(JsonValueKind.Null, links.GetProperty(name).ValueKind);
        }

        Assert.Equal(Now, task.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(Now, task.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.False(string.IsNullOrWhiteSpace(task.GetProperty("concurrencyToken").GetString()));

        // The row itself, including the facts the response does not publish.
        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking().SingleAsync(Ct));
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);
        Assert.Equal(stored.OwnerMembershipId, stored.AssigneeMembershipId);
        Assert.Equal(WorkTaskStatus.Backlog, stored.Status);
        Assert.Equal(WorkTaskPolicy.RankGap, stored.SortRank);
    }

    [Fact]
    public async Task A_workspace_that_has_set_no_calendar_captures_a_task_into_no_week()
    {
        using var client = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var response = await CaptureAsync(client, _fixture.WorkspaceB, new { title = "Answer the brand email" });

        // Capture never fails for want of a setting: the task is simply unplanned until someone plans it.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await BodyOf(response)).GetProperty("planningWeekStart").ValueKind);
    }

    [Fact]
    public async Task A_day_on_its_own_puts_the_task_in_the_week_holding_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // A Thursday, five weeks past the one the creator is in now.
        var response = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Proof the dough", scheduledDate = "2026-11-05" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await BodyOf(response);

        Assert.Equal("2026-11-05", task.GetProperty("scheduledDate").GetString());
        Assert.Equal("2026-11-02", task.GetProperty("planningWeekStart").GetString());
    }

    [Fact]
    public async Task Everything_optional_is_kept_as_it_was_sent()
    {
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        var cardId = await CreateCardAsync(_fixture.WorkspaceA);
        var assetId = await SeedAssetAsync(_fixture.WorkspaceA);
        var (runId, stepKey) = await StartRunAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CaptureAsync(client, _fixture.WorkspaceA, new
        {
            title = "Shoot the galette",
            description = "  Morning light, the blue plate  ",
            priority = "High",
            scheduledDate = "2026-10-16",
            planningWeekStart = CurrentWeek,
            recipeId,
            contentCardId = cardId,
            mediaAssetId = assetId,
            workflowRunId = runId,
            workflowStepKey = stepKey,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await BodyOf(response);

        Assert.Equal("Morning light, the blue plate", task.GetProperty("description").GetString());
        Assert.Equal("High", task.GetProperty("priority").GetString());
        Assert.Equal("2026-10-16", task.GetProperty("scheduledDate").GetString());
        Assert.Equal(CurrentWeek, task.GetProperty("planningWeekStart").GetString());

        var links = task.GetProperty("links");
        Assert.Equal(recipeId, links.GetProperty("recipeId").GetGuid());
        Assert.Equal(cardId, links.GetProperty("contentCardId").GetGuid());
        Assert.Equal(assetId, links.GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(runId, links.GetProperty("workflowRunId").GetGuid());
        Assert.Equal(stepKey, links.GetProperty("workflowStepKey").GetString());
    }

    [Fact]
    public async Task A_new_task_goes_to_the_bottom_of_its_list()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(client, _fixture.WorkspaceA, new { title = "First" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Second" })).StatusCode);

        var inOrder = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking()
            .OrderBy(task => task.SortRank).ThenBy(task => task.Id)
            .Select(task => task.Title)
            .ToListAsync(Ct));

        Assert.Equal(["First", "Second"], inOrder);
    }

    // ---- What cannot be read ----

    public static TheoryData<string, object> Unreadable => new()
    {
        { "title", new { } },
        { "title", new { title = "   " } },
        { "title", new { title = new string('x', WorkTaskPolicy.TitleMaxLength + 1) } },
        { "description", new { title = "Shoot it", description = new string('x', WorkTaskPolicy.DescriptionMaxLength + 1) } },
        { "priority", new { title = "Shoot it", priority = 0 } },

        // A week is named by its first day, and that day is a Monday.
        { "planningWeekStart", new { title = "Shoot it", planningWeekStart = "2026-10-14" } },
        { "scheduledDate", new { title = "Shoot it", planningWeekStart = CurrentWeek, scheduledDate = "2026-10-20" } },
        { "workflowStepKey", new { title = "Shoot it", workflowStepKey = "shoot" } },
        { "recipeId", new { title = "Shoot it", recipeId = "00000000-0000-0000-0000-000000000000" } },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task A_task_that_cannot_be_read_is_refused_naming_the_field(string field, object body)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CaptureAsync(client, _fixture.WorkspaceA, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);

        Assert.Equal(PlanningErrorCodes.WorkTaskInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"no error named {field}");
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    // ---- Links ----

    [Fact]
    public async Task Every_link_this_workspace_cannot_use_is_named_in_one_answer()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CaptureAsync(client, _fixture.WorkspaceA, new
        {
            title = "Shoot the galette",
            recipeId = Guid.NewGuid(),
            contentCardId = Guid.NewGuid(),
            mediaAssetId = Guid.NewGuid(),
            workflowRunId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await BodyOf(response);

        Assert.Equal(PlanningErrorCodes.WorkTaskLinkUnprocessable, Code(problem));
        var errors = problem.GetProperty("errors");
        foreach (var field in new[] { "recipeId", "contentCardId", "mediaAssetId", "workflowRunId" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"no error named {field}");
        }

        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_run_of_this_workspace_with_no_such_step_is_refused()
    {
        var (runId, _) = await StartRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CaptureAsync(
            client, _fixture.WorkspaceA, new { title = "Shoot it", workflowRunId = runId, workflowStepKey = "no-such-step" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("workflowStepKey", out _));
    }

    // ---- Replay ----

    [Fact]
    public async Task A_replay_returns_the_first_task_and_makes_no_second_one()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var body = new { title = "Shoot the galette" };

        var first = await CaptureAsync(client, _fixture.WorkspaceA, body, key);
        var again = await CaptureAsync(client, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotent-Replayed")));

        Assert.Equal((await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(again)).GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task The_same_key_with_a_different_task_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(client, _fixture.WorkspaceA, new { title = "First" }, key)).StatusCode);

        var other = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Second" }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task One_key_in_two_workspaces_is_two_requests()
    {
        var key = Guid.NewGuid().ToString();
        var body = new { title = "Shoot the galette" };

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var first = await CaptureAsync(inA, _fixture.WorkspaceA, body, key);
        var second = await CaptureAsync(inB, _fixture.WorkspaceB, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Not a replay: the key is scoped to the workspace, so one workspace's retry can never be answered
        // with — or suppressed by — another's.
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task One_key_from_two_people_in_one_workspace_is_two_requests()
    {
        var key = Guid.NewGuid().ToString();
        var body = new { title = "Shoot the galette" };

        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var first = await CaptureAsync(owner, _fixture.WorkspaceA, body, key);
        var second = await CaptureAsync(editor, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // The key is the caller's too: two people who happen to pick the same one each keep their own task.
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(2, await CountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Without_a_key_two_identical_captures_are_two_tasks()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });
        await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        Assert.Equal(2, await CountAsync(_fixture.WorkspaceA));
    }

    // ---- The role bar ----

    [Fact]
    public async Task A_viewer_may_not_keep_work_in_the_workspace_and_a_contributor_may()
    {
        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var refused = await CaptureAsync(viewer, _fixture.WorkspaceB, new { title = "Not mine to plan" });

        // The route policy refuses first, so this is the edge's own code rather than the module's; the
        // facade's bar is the backstop for a worker or plugin that never passes through a route.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceB));

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(
            HttpStatusCode.Created,
            (await CaptureAsync(editor, _fixture.WorkspaceA, new { title = "An editor's own work" })).StatusCode);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_task_is_captured_into_the_workspace_the_route_names_and_no_other()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(inA, _fixture.WorkspaceA, new { title = "A's work" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(inB, _fixture.WorkspaceB, new { title = "B's work" })).StatusCode);

        Assert.Equal(["A's work"], await TitlesAsync(_fixture.WorkspaceA));
        Assert.Equal(["B's work"], await TitlesAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_capture_into_another()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CaptureAsync(client, _fixture.WorkspaceB, new { title = "Not mine to plan" });

        // 404, not 403: whether that workspace exists is not this caller's to learn.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceB));
    }

    /// <summary>
    /// Every kind of link, each seeded for real in workspace B and then named from workspace A.
    /// </summary>
    /// <remarks>
    /// One case per link rather than only a recipe: these are four separate lookups through four modules,
    /// and a regression in any one of them would be a way to ask what a neighbour owns while the other three
    /// still answered correctly.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_record_is_refused_exactly_as_one_that_never_existed()
    {
        var theirRecipe = await CreateRecipeAsync(_fixture.WorkspaceB);
        var theirCard = await CreateCardAsync(_fixture.WorkspaceB);
        var theirAsset = await SeedAssetAsync(_fixture.WorkspaceB);
        var (theirRun, theirStep) = await StartRunAsync(_fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        await SameAnswerAsync(client, "recipeId", theirRecipe);
        await SameAnswerAsync(client, "contentCardId", theirCard);
        await SameAnswerAsync(client, "mediaAssetId", theirAsset);
        await SameAnswerAsync(client, "workflowRunId", theirRun);

        // The one branch with a message of its own. A neighbour's run must still read as a run that is not
        // here, rather than as a run whose steps this caller may ask about.
        await SameAnswerAsync(client, "workflowRunId", theirRun, theirStep);

        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// Captures twice — once naming <paramref name="theirs"/>, once naming an id nobody has — and insists the
    /// two answers are the same one.
    /// </summary>
    private async Task SameAnswerAsync(GatewayClient client, string field, Guid theirs, string? stepKey = null)
    {
        var neighbour = await CaptureAsync(client, _fixture.WorkspaceA, Body(theirs));
        var nobodys = await CaptureAsync(client, _fixture.WorkspaceA, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, neighbour.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nobodys.StatusCode);

        // The same code, the same message, the same field errors: the two cases are one answer.
        var left = await BodyOf(neighbour);
        var right = await BodyOf(nobodys);
        Assert.Equal(Code(right), Code(left));
        Assert.Equal(right.GetProperty("title").GetString(), left.GetProperty("title").GetString());
        Assert.Equal(right.GetProperty("errors").ToString(), left.GetProperty("errors").ToString());

        Dictionary<string, object> Body(Guid id)
        {
            var body = new Dictionary<string, object> { ["title"] = "Shoot it", [field] = id };
            if (stepKey is not null)
            {
                body["workflowStepKey"] = stepKey;
            }

            return body;
        }
    }

    [Fact]
    public async Task Nothing_the_body_says_about_ownership_or_status_is_believed()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // None of these is a field of the request. They are ignored rather than honoured, and the point of
        // the test is that the stored row says so (tenancy.md: never take WorkspaceId from a body).
        var response = await CaptureAsync(client, _fixture.WorkspaceA, new
        {
            title = "Shoot the galette",
            workspaceId = _fixture.WorkspaceB.Id,
            ownerMembershipId = Guid.NewGuid(),
            assigneeMembershipId = Guid.NewGuid(),
            status = "Done",
            sortRank = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Backlog", (await BodyOf(response)).GetProperty("status").GetString());

        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking().SingleAsync(Ct));
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);
        Assert.Equal(WorkTaskStatus.Backlog, stored.Status);
        Assert.Equal(stored.OwnerMembershipId, stored.AssigneeMembershipId);
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceB));
    }

    // ---- Helpers ----

    private Task<int> CountAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.WorkTasks.CountAsync(Ct));

    private Task<List<string>> TitlesAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.WorkTasks.AsNoTracking()
            .OrderBy(task => task.SortRank).ThenBy(task => task.Id)
            .Select(task => task.Title)
            .ToListAsync(Ct));

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private async Task<Guid> CreateRecipeAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title = "Plum galette" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private async Task<Guid> CreateCardAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/content-cards", new { title = "Autumn galette post" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("card").GetProperty("id").GetGuid();
    }

    private Task<Guid> SeedAssetAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => Content.CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now, Ct));

    /// <summary>A real run, so its steps are the ones a task may name.</summary>
    private async Task<(Guid RunId, string StepKey)> StartRunAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/workflow-runs",
            new { definitionKey = "images.create" },
            Guid.NewGuid().ToString(),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await BodyOf(response);

        return (run.GetProperty("id").GetGuid(), run.GetProperty("steps")[0].GetProperty("key").GetString()!);
    }
}
