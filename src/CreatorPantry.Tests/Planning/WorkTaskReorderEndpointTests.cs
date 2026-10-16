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
/// <c>POST .../work-tasks/{taskId}/reorder</c> through the real Gateway (13A.28a): first, last and between, a
/// neighbour that shares its rank, a list re-spaced when there is no room and nothing else touched, a task already
/// in place left alone, and a list nobody reorders but the person doing it.
/// </summary>
/// <remarks>
/// That two reorders racing — or one whose re-spacing meets a neighbour edited meanwhile — write nothing is
/// <see cref="WorkTaskSqlServerTests"/>' to prove: under SQLite a row version never changes.
/// </remarks>
public sealed class WorkTaskReorderEndpointTests : IAsyncLifetime
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

    private static Task<HttpResponseMessage> ReorderAsync(GatewayClient client, SeededWorkspace workspace, WorkTask task, Guid? before) =>
        client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/work-tasks/{task.Id}/reorder",
            new { expectedConcurrencyToken = Token(task), beforeTaskId = before },
            Ct);

    private static async Task<JsonElement> ReorderedAsync(GatewayClient client, WorkTask task, Guid? before, SeededWorkspace workspace)
    {
        var response = await ReorderAsync(client, workspace, task, before);
        var body = await BodyOf(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {body}");

        return body;
    }

    private Task<JsonElement> ReorderedAsync(GatewayClient client, WorkTask task, Guid? before) =>
        ReorderedAsync(client, task, before, _fixture.WorkspaceA);

    private async Task<IReadOnlyList<string>> MondayAsync(GatewayClient client)
    {
        var response = await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/week", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await BodyOf(response)).GetProperty("days")[0].GetProperty("tasks").EnumerateArray()
            .Select(task => task.GetProperty("title").GetString()!)];
    }

    // ---- Places ----

    [Fact]
    public async Task A_task_goes_first_last_or_between_and_the_order_is_the_lists_own()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var a = Monday("A", mine, 1024);
        var b = Monday("B", mine, 2048);
        var c = Monday("C", mine, 3072);
        var d = Monday("D", mine, 4096);
        await SeedAsync(a, b, c, d);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        await ReorderedAsync(client, d, a.Id);
        Assert.Equal(["D", "A", "B", "C"], await MondayAsync(client));

        await ReorderedAsync(client, await StoredAsync(a.Id), null);
        Assert.Equal(["D", "B", "C", "A"], await MondayAsync(client));

        await ReorderedAsync(client, await StoredAsync(c.Id), b.Id);
        Assert.Equal(["D", "C", "B", "A"], await MondayAsync(client));
    }

    [Fact]
    public async Task Before_a_task_that_shares_its_rank_it_still_lands_immediately_before_it()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var one = Monday("Tied one", mine, 1024);
        var two = Monday("Tied two", mine, 1024);
        var last = Monday("Last", mine, 2048);
        await SeedAsync(one, two, last);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var before = await MondayAsync(client);
        var second = before[1] == "Tied one" ? one : two;

        await ReorderedAsync(client, last, second.Id);

        Assert.Equal([before[0], "Last", before[1]], await MondayAsync(client));
    }

    [Fact]
    public async Task With_no_room_the_list_alone_is_re_spaced_in_its_own_order()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var a = Monday("A", mine, 1);
        var b = Monday("B", mine, 2);
        var c = Monday("C", mine, 3);
        var tuesday = Monday("Tuesday's", mine, 2);
        tuesday.ScheduledDate = ThisMonday.AddDays(1);
        var colleagues = Monday("The editor's", editor, 2);
        await SeedAsync(a, b, c, tuesday, colleagues);
        var theirs = Monday("B's", await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner), 2);
        await SeedInAsync(_fixture.WorkspaceB, theirs);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await ReorderedAsync(client, c, b.Id);

        Assert.Equal(["A", "C", "B"], await MondayAsync(client));

        var ranks = new List<long>();
        foreach (var id in new[] { a.Id, c.Id, b.Id })
        {
            ranks.Add((await StoredAsync(id)).SortRank);
        }

        Assert.Equal(new[] { WorkTaskPolicy.RankGap, 2 * WorkTaskPolicy.RankGap, 3 * WorkTaskPolicy.RankGap }, ranks);
        Assert.Equal(2, (await StoredAsync(tuesday.Id)).SortRank);
        Assert.Equal(2, (await StoredAsync(colleagues.Id)).SortRank);
        Assert.Equal(2, (await StoredInAsync(_fixture.WorkspaceB, theirs.Id)).SortRank);
    }

    [Fact]
    public async Task A_task_already_exactly_there_is_left_alone_and_keeps_its_token()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var a = Monday("A", mine, 1024);
        var b = Monday("B", mine, 2048);
        await SeedAsync(a, b);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var beforeNext = await ReorderedAsync(client, a, b.Id);
        Assert.Equal(Token(a), beforeNext.GetProperty("concurrencyToken").GetString());

        var alreadyLast = await ReorderedAsync(client, b, null);
        Assert.Equal(Token(b), alreadyLast.GetProperty("concurrencyToken").GetString());

        Assert.Equal(1024, (await StoredAsync(a.Id)).SortRank);
        Assert.Equal(2048, (await StoredAsync(b.Id)).SortRank);
        Assert.Equal(Thursday, (await StoredAsync(a.Id)).UpdatedAt);
    }

    [Fact]
    public async Task Only_the_place_changes_and_in_progress_is_one_list_whatever_the_day()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var first = Monday("Started Monday", mine, 1024);
        first.Status = WorkTaskStatus.InProgress;
        var second = Monday("Started Friday", mine, 2048);
        second.Status = WorkTaskStatus.InProgress;
        second.ScheduledDate = ThisMonday.AddDays(4);
        second.DueDate = ThisMonday.AddDays(6);
        await SeedAsync(first, second);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var moved = await ReorderedAsync(client, second, first.Id);

        Assert.Equal("InProgress", moved.GetProperty("status").GetString());

        var stored = await StoredAsync(second.Id);
        Assert.Equal(WorkTaskStatus.InProgress, stored.Status);
        Assert.Equal(ThisMonday.AddDays(4), stored.ScheduledDate);
        Assert.Equal(ThisMonday, stored.PlanningWeekStart);
        Assert.Equal(ThisMonday.AddDays(6), stored.DueDate);
        Assert.True(stored.SortRank < (await StoredAsync(first.Id)).SortRank);
    }

    [Fact]
    public async Task A_task_tied_at_its_own_rank_is_not_taken_to_be_already_in_place()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var peer = Monday("Peer", mine, 1024);
        var task = Monday("Tied task", mine, 1024);
        var next = Monday("Next", mine, 2048);
        await SeedAsync(peer, task, next);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await ReorderedAsync(client, task, next.Id);

        // Immediately before "Next" whichever way the tie used to read: after its peer, not merely near it.
        Assert.Equal(["Peer", "Tied task", "Next"], await MondayAsync(client));
    }

    [Fact]
    public async Task A_task_tied_for_last_is_moved_to_the_bottom_rather_than_left()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var first = Monday("First", mine, 1024);
        var tiedOne = Monday("Tied one", mine, 2048);
        var tiedTwo = Monday("Tied two", mine, 2048);
        await SeedAsync(first, tiedOne, tiedTwo);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var before = await MondayAsync(client);
        var upper = before[1] == "Tied one" ? tiedOne : tiedTwo;

        await ReorderedAsync(client, upper, null);

        Assert.Equal(["First", before[2], before[1]], await MondayAsync(client));
    }

    [Fact]
    public async Task Before_a_neighbour_tied_with_another_task_the_reorder_is_not_taken_to_be_in_place()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Monday("Task", mine, 1024);
        var tiedOne = Monday("Tied one", mine, 2048);
        var tiedTwo = Monday("Tied two", mine, 2048);
        await SeedAsync(task, tiedOne, tiedTwo);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var before = await MondayAsync(client);
        var lower = before[2] == "Tied one" ? tiedOne : tiedTwo;

        await ReorderedAsync(client, task, lower.Id);

        Assert.Equal([before[1], "Task", before[2]], await MondayAsync(client));
    }

    [Fact]
    public async Task The_backlog_of_no_day_and_waiting_are_lists_a_task_is_reordered_in_too()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var backlogFirst = Monday("Backlog first", mine, 1024);
        var backlogSecond = Monday("Backlog second", mine, 2048);
        backlogFirst.ScheduledDate = backlogSecond.ScheduledDate = null;
        var waitingFirst = Monday("Waiting first", mine, 1024);
        var waitingSecond = Monday("Waiting second", mine, 2048);
        waitingFirst.Status = waitingSecond.Status = WorkTaskStatus.Waiting;
        await SeedAsync(backlogFirst, backlogSecond, waitingFirst, waitingSecond);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await ReorderedAsync(client, backlogSecond, backlogFirst.Id);
        await ReorderedAsync(client, waitingSecond, waitingFirst.Id);

        var response = await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/week", Ct);
        var week = await BodyOf(response);
        Assert.Equal(["Backlog second", "Backlog first"], Titles(week.GetProperty("backlog")));
        Assert.Equal(["Waiting second", "Waiting first"], Titles(week.GetProperty("waiting")));
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_place_not_in_the_tasks_own_list_is_refused_the_same_way_whatever_it_is()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var task = Monday("Mine", mine, 1024);
        var other = Monday("Monday's other", mine, 2048);
        var tuesday = Monday("Tuesday's", mine, 1024);
        tuesday.ScheduledDate = ThisMonday.AddDays(1);
        var colleagues = Monday("The editor's", editor, 1024);
        await SeedAsync(task, other, tuesday, colleagues);
        var theirs = Monday("B's", await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner), 1024);
        await SeedInAsync(_fixture.WorkspaceB, theirs);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var messages = new List<string>();
        foreach (var before in new[] { task.Id, tuesday.Id, colleagues.Id, theirs.Id, Guid.NewGuid() })
        {
            var response = await ReorderAsync(client, _fixture.WorkspaceA, task, before);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await BodyOf(response);
            Assert.Equal(PlanningErrorCodes.WorkTaskMoveUnprocessable, Code(problem));
            messages.Add(problem.GetProperty("errors").GetProperty("beforeTaskId").ToString());
        }

        Assert.Single(messages.Distinct());
        Assert.Equal(1024, (await StoredAsync(task.Id)).SortRank);
    }

    [Fact]
    public async Task A_finished_task_has_no_place_to_change()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var done = Monday("Finished", mine, 1024);
        done.Status = WorkTaskStatus.Done;
        done.CompletedAt = Thursday;
        done.CompletedByMembershipId = mine;
        done.CompletedFromStatus = WorkTaskStatus.Backlog;
        await SeedAsync(done);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await ReorderAsync(client, _fixture.WorkspaceA, done, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskCompleted, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_stale_reorder_writes_nothing_and_a_malformed_token_is_a_bad_request()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var a = Monday("A", mine, 1024);
        var b = Monday("B", mine, 2048);
        await SeedAsync(a, b);

        var bytes = (byte[])b.RowVersion.Clone();
        bytes[^1] ^= 0xFF;

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var path = $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/{b.Id}/reorder";

        var stale = await client.PostAsJsonAsync(path, new { expectedConcurrencyToken = Convert.ToBase64String(bytes), beforeTaskId = a.Id }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskStale, Code(await BodyOf(stale)));
        Assert.Equal(["A", "B"], await MondayAsync(client));

        var malformed = await client.PostAsJsonAsync(path, new { expectedConcurrencyToken = "nope", beforeTaskId = a.Id }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    // ---- Whose list it is ----

    [Fact]
    public async Task Only_the_person_doing_a_task_reorders_it_not_even_the_owner_who_handed_it_on()
    {
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var editorsOwn = Monday("The editor's own", editor, 1024);
        var handed = Monday("Handed on", editor, 2048);
        handed.OwnerMembershipId = owner;
        await SeedAsync(editorsOwn, handed);

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var byOwner = await ReorderAsync(asOwner, _fixture.WorkspaceA, handed, editorsOwn.Id);
        Assert.Equal(HttpStatusCode.NotFound, byOwner.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskNotFound, Code(await BodyOf(byOwner)));

        await ReorderedAsync(asEditor, handed, editorsOwn.Id);
        Assert.True((await StoredAsync(handed.Id)).SortRank < (await StoredAsync(editorsOwn.Id)).SortRank);
    }

    [Fact]
    public async Task Another_workspaces_task_does_not_exist_and_a_viewer_may_not_reorder()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Monday("A's", mine, 1024);
        await SeedAsync(task);

        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        using var viewerOfB = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(HttpStatusCode.NotFound, (await ReorderAsync(ownerOfB, _fixture.WorkspaceB, task, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReorderAsync(ownerOfB, _fixture.WorkspaceA, task, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReorderAsync(viewerOfB, _fixture.WorkspaceB, task, null)).StatusCode);
        Assert.Equal(1024, (await StoredAsync(task.Id)).SortRank);
    }

    // ---- Helpers ----

    private static IReadOnlyList<string> Titles(JsonElement group) =>
        [.. group.GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("title").GetString()!)];

    private static WorkTask Monday(string title, Guid assignee, long rank) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = assignee,
            AssigneeMembershipId = assignee,
            Title = title,
            ScheduledDate = ThisMonday,
            PlanningWeekStart = ThisMonday,
            SortRank = rank,
            CreatedAt = Thursday,
            UpdatedAt = Thursday,
        };

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

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
