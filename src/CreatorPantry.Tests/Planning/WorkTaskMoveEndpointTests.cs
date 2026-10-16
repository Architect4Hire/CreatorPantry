using System.Globalization;
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
/// <c>POST .../work-tasks/{taskId}/move</c> through the real Gateway (13A.28): every list a task can be moved
/// to and what each keeps, a place named by a neighbour and kept deterministic when there is no room, the moves
/// that are not moves, a replay that moves nothing more, and a task nobody moves but the person doing it.
/// </summary>
/// <remarks>
/// That a concurrent move — or one whose neighbour changed while it was re-spaced — writes nothing is
/// <see cref="WorkTaskSqlServerTests"/>' to prove: under SQLite a row version never changes.
/// </remarks>
public sealed class WorkTaskMoveEndpointTests : IAsyncLifetime
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

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Token(JsonElement task) => task.GetProperty("concurrencyToken").GetString()!;

    private static Guid Id(JsonElement task) => task.GetProperty("id").GetGuid();

    private static async Task<JsonElement> CaptureAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/work-tasks", body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private static Task<HttpResponseMessage> MoveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid taskId, object body, string? key = null)
    {
        var path = $"/api/v1/workspaces/{workspace.Slug}/work-tasks/{taskId}/move";

        return key is null ? client.PostAsJsonAsync(path, body, Ct) : client.PostAsJsonAsync(path, body, key, Ct);
    }

    /// <summary>Moves and insists on a 200, returning the task as it now stands.</summary>
    private static async Task<JsonElement> MovedAsync(GatewayClient client, SeededWorkspace workspace, Guid taskId, object body)
    {
        var response = await MoveAsync(client, workspace, taskId, body);
        var task = await BodyOf(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {task}");

        return task;
    }

    private static async Task<JsonElement> WeekAsync(GatewayClient client, SeededWorkspace workspace, DateOnly? weekStart = null)
    {
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspace.Slug}/work-tasks/week" + (weekStart is { } start ? $"?weekStart={Iso(start)}" : string.Empty),
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static IReadOnlyList<string> Titles(JsonElement group) =>
        [.. group.GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("title").GetString()!)];

    private static IReadOnlyList<string> DayTitles(JsonElement week, int offset) => Titles(week.GetProperty("days")[offset]);

    private static bool IsNull(JsonElement task, string property) =>
        !task.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    // ---- Every list ----

    [Fact]
    public async Task In_progress_and_waiting_keep_the_plan_and_a_day_or_backlog_sets_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", scheduledDate = Iso(ThisMonday) });

        var started = await MovedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), status = "InProgress" });
        Assert.Equal("InProgress", started.GetProperty("status").GetString());
        Assert.Equal(Iso(ThisMonday), started.GetProperty("scheduledDate").GetString());

        var week = await WeekAsync(client, _fixture.WorkspaceA);
        Assert.Equal(["Shoot the galette"], Titles(week.GetProperty("inProgress")));
        Assert.Empty(DayTitles(week, 0));

        var waiting = await MovedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(started), status = "Waiting" });
        Assert.Equal("Waiting", waiting.GetProperty("status").GetString());
        Assert.Equal(Iso(ThisMonday), waiting.GetProperty("scheduledDate").GetString());

        // To a day of next week: not started again, and the week follows the day.
        var nextWednesday = ThisMonday.AddDays(9);
        var planned = await MovedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(waiting),
            status = "Backlog",
            scheduledDate = Iso(nextWednesday),
        });
        Assert.Equal("Backlog", planned.GetProperty("status").GetString());
        Assert.Equal(Iso(ThisMonday.AddDays(7)), planned.GetProperty("planningWeekStart").GetString());
        Assert.Equal(["Shoot the galette"], DayTitles(await WeekAsync(client, _fixture.WorkspaceA, ThisMonday.AddDays(7)), 2));

        // To the backlog of no day, keeping the week it has; then to another week's backlog.
        var unscheduled = await MovedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(planned), status = "Backlog" });
        Assert.True(IsNull(unscheduled, "scheduledDate"));
        Assert.Equal(Iso(ThisMonday.AddDays(7)), unscheduled.GetProperty("planningWeekStart").GetString());

        var thisWeek = await MovedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(unscheduled),
            status = "Backlog",
            planningWeekStart = Iso(ThisMonday),
        });
        Assert.Equal(Iso(ThisMonday), thisWeek.GetProperty("planningWeekStart").GetString());
        Assert.Equal(["Shoot the galette"], Titles((await WeekAsync(client, _fixture.WorkspaceA)).GetProperty("backlog")));
    }

    // ---- Place ----

    [Fact]
    public async Task Left_out_the_place_is_the_bottom_and_a_neighbour_names_any_other()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "A", scheduledDate = Iso(ThisMonday) });
        var second = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "B", scheduledDate = Iso(ThisMonday) });
        var bottom = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "To the bottom", scheduledDate = Iso(ThisMonday.AddDays(1)) });
        var top = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "To the top", scheduledDate = Iso(ThisMonday.AddDays(1)) });
        var between = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Between", scheduledDate = Iso(ThisMonday.AddDays(1)) });

        var monday = new { status = "Backlog", scheduledDate = Iso(ThisMonday) };
        await MovedAsync(client, _fixture.WorkspaceA, Id(bottom), new { expectedConcurrencyToken = Token(bottom), monday.status, monday.scheduledDate });
        await MovedAsync(client, _fixture.WorkspaceA, Id(top), new { expectedConcurrencyToken = Token(top), monday.status, monday.scheduledDate, beforeTaskId = Id(first) });
        await MovedAsync(client, _fixture.WorkspaceA, Id(between), new { expectedConcurrencyToken = Token(between), monday.status, monday.scheduledDate, beforeTaskId = Id(second) });

        Assert.Equal(["To the top", "A", "Between", "B", "To the bottom"], DayTitles(await WeekAsync(client, _fixture.WorkspaceA), 0));
    }

    [Fact]
    public async Task With_no_room_the_target_list_alone_is_re_spaced_in_its_own_order()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // Adjacent ranks on Monday leave no room between them; Tuesday's task must not be touched.
        var low = Task_("Rank one", mine, ThisMonday, rank: 1);
        var high = Task_("Rank two", mine, ThisMonday, rank: 2);
        var tuesday = Task_("Tuesday's", mine, ThisMonday.AddDays(1), rank: 7);
        var moving = Task_("Squeezed in", mine, ThisMonday.AddDays(2), rank: 1);
        await SeedAsync(low, high, tuesday, moving);

        // The same day and the same ranks in a colleague's list and in another workspace: not the target list.
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var colleagues = Task_("The editor's Monday", editor, ThisMonday, rank: 2);
        await SeedAsync(colleagues);
        var theirs = Task_("B's Monday", await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner), ThisMonday, rank: 2);
        await SeedInAsync(_fixture.WorkspaceB, theirs);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await MovedAsync(client, _fixture.WorkspaceA, moving.Id, new
        {
            expectedConcurrencyToken = WorkTaskConcurrencyToken.From(moving.RowVersion),
            status = "Backlog",
            scheduledDate = Iso(ThisMonday),
            beforeTaskId = high.Id,
        });

        Assert.Equal(["Rank one", "Squeezed in", "Rank two"], DayTitles(await WeekAsync(client, _fixture.WorkspaceA), 0));
        var ranks = new List<long>();
        foreach (var id in new[] { low.Id, moving.Id, high.Id })
        {
            ranks.Add((await StoredAsync(id)).SortRank);
        }

        Assert.Equal(new[] { WorkTaskPolicy.RankGap, 2 * WorkTaskPolicy.RankGap, 3 * WorkTaskPolicy.RankGap }, ranks);
        Assert.Equal(7, (await StoredAsync(tuesday.Id)).SortRank);
        Assert.Equal(2, (await StoredAsync(colleagues.Id)).SortRank);
        Assert.Equal(2, (await StoredInAsync(_fixture.WorkspaceB, theirs.Id)).SortRank);
    }

    [Fact]
    public async Task Before_a_task_that_shares_its_rank_the_move_still_lands_immediately_before_it()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var tiedOne = Task_("Tied one", mine, ThisMonday, rank: WorkTaskPolicy.RankGap);
        var tiedTwo = Task_("Tied two", mine, ThisMonday, rank: WorkTaskPolicy.RankGap);
        var moving = Task_("Moving", mine, ThisMonday.AddDays(3), rank: 1);
        await SeedAsync(tiedOne, tiedTwo, moving);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var before = DayTitles(await WeekAsync(client, _fixture.WorkspaceA), 0);
        var second = before[1] == "Tied one" ? tiedOne : tiedTwo;

        await MovedAsync(client, _fixture.WorkspaceA, moving.Id, new
        {
            expectedConcurrencyToken = WorkTaskConcurrencyToken.From(moving.RowVersion),
            status = "Backlog",
            scheduledDate = Iso(ThisMonday),
            beforeTaskId = second.Id,
        });

        Assert.Equal([before[0], "Moving", before[1]], DayTitles(await WeekAsync(client, _fixture.WorkspaceA), 0));
    }

    [Fact]
    public async Task A_move_to_another_weeks_backlog_never_counts_the_task_as_its_own_neighbour()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // All three are in the list of no day; ranks 1 and 2 leave no room, so placing before Q re-spaces it.
        var p = Unscheduled("P", mine, ThisMonday, rank: 1);
        var q = Unscheduled("Q", mine, ThisMonday, rank: 2);
        var moving = Unscheduled("Next week instead", mine, ThisMonday, rank: 3);
        await SeedAsync(p, q, moving);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var token = WorkTaskConcurrencyToken.From(moving.RowVersion);
        var nextWeek = Iso(ThisMonday.AddDays(7));

        var self = await MoveAsync(client, _fixture.WorkspaceA, moving.Id, new
        {
            expectedConcurrencyToken = token,
            status = "Backlog",
            planningWeekStart = nextWeek,
            beforeTaskId = moving.Id,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskMoveUnprocessable, Code(await BodyOf(self)));

        await MovedAsync(client, _fixture.WorkspaceA, moving.Id, new
        {
            expectedConcurrencyToken = token,
            status = "Backlog",
            planningWeekStart = nextWeek,
            beforeTaskId = q.Id,
        });

        // Ranked once, between its neighbours, with the list evenly re-spaced.
        var ranks = new List<long>();
        foreach (var id in new[] { p.Id, moving.Id, q.Id })
        {
            ranks.Add((await StoredAsync(id)).SortRank);
        }

        Assert.Equal(new[] { WorkTaskPolicy.RankGap, 2 * WorkTaskPolicy.RankGap, 3 * WorkTaskPolicy.RankGap }, ranks);
        Assert.Equal(ThisMonday.AddDays(7), (await StoredAsync(moving.Id)).PlanningWeekStart);
    }

    // ---- Moves that are not moves ----

    [Theory]
    [InlineData("Done", null, null)]
    [InlineData(null, null, null)]
    [InlineData("InProgress", "2038-10-13", null)]
    [InlineData("Waiting", null, "2038-10-11")]
    [InlineData("Backlog", "2038-10-20", "2038-10-11")]
    [InlineData("Backlog", null, "2038-10-12")]
    public async Task A_move_that_does_not_name_a_list_it_can_go_to_is_a_bad_request(string? status, string? day, string? week)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var response = await MoveAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            status,
            scheduledDate = day,
            planningWeekStart = week,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskInvalid, Code(await BodyOf(response)));
        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(Id(task))).Status);
    }

    [Fact]
    public async Task The_list_a_task_is_already_in_is_a_reorder_not_a_move()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", scheduledDate = Iso(ThisMonday) });

        var response = await MoveAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            status = "Backlog",
            scheduledDate = Iso(ThisMonday),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("status", out _));
    }

    [Fact]
    public async Task A_finished_task_is_reopened_not_moved()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var done = Task_("Finished", mine, ThisMonday, rank: 1);
        done.Status = WorkTaskStatus.Done;
        done.CompletedAt = Thursday;
        done.CompletedByMembershipId = mine;
        done.CompletedFromStatus = WorkTaskStatus.InProgress;
        await SeedAsync(done);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await MoveAsync(client, _fixture.WorkspaceA, done.Id, new
        {
            expectedConcurrencyToken = WorkTaskConcurrencyToken.From(done.RowVersion),
            status = "InProgress",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskCompleted, Code(await BodyOf(response)));
        Assert.Equal(WorkTaskStatus.Done, (await StoredAsync(done.Id)).Status);
    }

    [Fact]
    public async Task A_place_not_in_the_target_list_is_refused_the_same_way_whatever_it_is()
    {
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var colleagues = Task_("The editor's Monday", editor, ThisMonday, rank: 1);
        await SeedAsync(colleagues);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Moving", scheduledDate = Iso(ThisMonday.AddDays(1)) });
        var elsewhere = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "On Friday", scheduledDate = Iso(ThisMonday.AddDays(4)) });
        var otherWorkspaces = Task_("B's Monday", await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner), ThisMonday, rank: 1);
        await SeedInAsync(_fixture.WorkspaceB, otherWorkspaces);

        var messages = new List<string>();
        foreach (var before in new[] { Id(elsewhere), Id(task), colleagues.Id, otherWorkspaces.Id, Guid.NewGuid() })
        {
            var response = await MoveAsync(client, _fixture.WorkspaceA, Id(task), new
            {
                expectedConcurrencyToken = Token(task),
                status = "Backlog",
                scheduledDate = Iso(ThisMonday),
                beforeTaskId = before,
            });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await BodyOf(response);
            Assert.Equal(PlanningErrorCodes.WorkTaskMoveUnprocessable, Code(problem));
            messages.Add(problem.GetProperty("errors").GetProperty("beforeTaskId").ToString());
        }

        Assert.Single(messages.Distinct());
        Assert.Equal(ThisMonday.AddDays(1), (await StoredAsync(Id(task))).ScheduledDate);
    }

    // ---- Concurrency and replay ----

    [Fact]
    public async Task A_stale_move_is_refused_and_moves_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var bytes = Convert.FromBase64String(Token(task));
        bytes[^1] ^= 0xFF;
        var response = await MoveAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Convert.ToBase64String(bytes),
            status = "InProgress",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskStale, Code(await BodyOf(response)));
        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(Id(task))).Status);

        var malformed = await MoveAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = "nope", status = "InProgress" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task A_replay_returns_the_first_answer_and_moves_nothing_more()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });
        var body = new { expectedConcurrencyToken = Token(task), status = "InProgress" };
        var key = Guid.NewGuid().ToString();

        var first = await MoveAsync(client, _fixture.WorkspaceA, Id(task), body, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        var firstTask = await BodyOf(first);

        // Moved on since, by somebody's later move; the replay must not drag it back.
        await MovedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(firstTask), status = "Waiting" });

        var again = await MoveAsync(client, _fixture.WorkspaceA, Id(task), body, key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal("InProgress", (await BodyOf(again)).GetProperty("status").GetString());
        Assert.Equal(WorkTaskStatus.Waiting, (await StoredAsync(Id(task))).Status);

        var reused = await MoveAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), status = "Backlog" }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency.key_reused", Code(await BodyOf(reused)));
    }

    [Fact]
    public async Task One_key_reused_for_another_task_is_refused_rather_than_replaying_the_first()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var one = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "One" });
        var two = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Two" });
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, _fixture.WorkspaceA, Id(one), new { expectedConcurrencyToken = Token(one), status = "InProgress" }, key)).StatusCode);

        // The task is part of the request: the same key for another task must neither replay the first task's
        // answer nor pass silently, so it is refused and nothing moves.
        var second = await MoveAsync(client, _fixture.WorkspaceA, Id(two), new { expectedConcurrencyToken = Token(two), status = "InProgress" }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("idempotency.key_reused", Code(await BodyOf(second)));
        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(Id(two))).Status);
    }

    // ---- Whose list it is ----

    [Fact]
    public async Task Only_the_person_doing_a_task_moves_it_not_even_the_owner_who_handed_it_on()
    {
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var task = await CaptureAsync(asOwner, _fixture.WorkspaceA, new { title = "Handed on" });
        var handed = await BodyOf(await asOwner.PatchAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/{Id(task)}",
            new { expectedConcurrencyToken = Token(task), assigneeMembershipId = editor },
            Ct));

        var byOwner = await MoveAsync(asOwner, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(handed), status = "InProgress" });
        Assert.Equal(HttpStatusCode.NotFound, byOwner.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskNotFound, Code(await BodyOf(byOwner)));

        var byEditor = await MovedAsync(asEditor, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(handed), status = "InProgress" });
        Assert.Equal("InProgress", byEditor.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Another_members_or_another_workspaces_task_does_not_exist_and_a_viewer_may_not_move()
    {
        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        using var viewerOfB = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var task = await CaptureAsync(asOwner, _fixture.WorkspaceA, new { title = "The owner's own" });
        var body = new { expectedConcurrencyToken = Token(task), status = "InProgress" };

        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(asEditor, _fixture.WorkspaceA, Id(task), body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(asOwner, _fixture.WorkspaceA, Guid.NewGuid(), body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(ownerOfB, _fixture.WorkspaceB, Id(task), body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(ownerOfB, _fixture.WorkspaceA, Id(task), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MoveAsync(viewerOfB, _fixture.WorkspaceB, Id(task), body)).StatusCode);

        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(Id(task))).Status);
    }

    [Fact]
    public async Task Moving_a_task_leaves_the_workflow_step_it_is_about_exactly_as_it_was()
    {
        var (runId, stepKey) = await StartRunAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var runBefore = await RunStepsAsync(client, runId);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", workflowRunId = runId, workflowStepKey = stepKey });

        var started = await MovedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), status = "InProgress" });
        await MovedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(started), status = "Waiting" });

        Assert.Equal(runBefore, await RunStepsAsync(client, runId));
    }

    // ---- The ranking the move depends on ----

    [Fact]
    public async Task Re_planning_a_task_in_progress_keeps_its_place_in_progress()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "First started", scheduledDate = Iso(ThisMonday) });
        var second = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Second started", scheduledDate = Iso(ThisMonday.AddDays(1)) });

        var firstStarted = await MovedAsync(client, _fixture.WorkspaceA, Id(first), new { expectedConcurrencyToken = Token(first), status = "InProgress" });
        await MovedAsync(client, _fixture.WorkspaceA, Id(second), new { expectedConcurrencyToken = Token(second), status = "InProgress" });
        Assert.Equal(["First started", "Second started"], Titles((await WeekAsync(client, _fixture.WorkspaceA)).GetProperty("inProgress")));

        // In progress is one list whatever the day, so a new day is not a new list and not a new place.
        var edit = await client.PatchAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/{Id(first)}",
            new { expectedConcurrencyToken = Token(firstStarted), scheduledDate = Iso(ThisMonday.AddDays(3)) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal(["First started", "Second started"], Titles((await WeekAsync(client, _fixture.WorkspaceA)).GetProperty("inProgress")));
    }

    // ---- Helpers ----

    private static WorkTask Task_(string title, Guid assignee, DateOnly day, long rank) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = assignee,
            AssigneeMembershipId = assignee,
            Title = title,
            ScheduledDate = day,
            PlanningWeekStart = WorkTaskWeek.StartOf(day),
            SortRank = rank,
            CreatedAt = Thursday,
            UpdatedAt = Thursday,
        };

    private static WorkTask Unscheduled(string title, Guid assignee, DateOnly week, long rank)
    {
        var task = Task_(title, assignee, week, rank);
        task.ScheduledDate = null;

        return task;
    }

    private Task SeedAsync(params WorkTask[] tasks) => SeedInAsync(_fixture.WorkspaceA, tasks);

    private Task SeedInAsync(SeededWorkspace workspace, params WorkTask[] tasks) =>
        InWorkspaceAsync(workspace, async db =>
        {
            db.WorkTasks.AddRange(tasks);

            return await db.SaveChangesAsync(Ct);
        });

    private Task<WorkTask> StoredAsync(Guid taskId) => StoredInAsync(_fixture.WorkspaceA, taskId);

    private Task<WorkTask> StoredInAsync(SeededWorkspace workspace, Guid taskId) =>
        InWorkspaceAsync(workspace, db => db.WorkTasks.AsNoTracking().SingleAsync(task => task.Id == taskId, Ct));

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

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
