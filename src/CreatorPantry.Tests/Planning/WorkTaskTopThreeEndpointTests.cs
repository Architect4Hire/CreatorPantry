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
/// <c>PUT .../work-tasks/top-three</c> and the <c>topThree</c> it puts on the day, through the real Gateway
/// (13A.32): the whole set replaced in order, only tasks on the caller's day, one refusal for every way a task is
/// not, a choice that changes no task and lasts one day, and the boundaries between people and workspaces.
/// </summary>
public sealed class WorkTaskTopThreeEndpointTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";

    /// <summary>
    /// Midday in <see cref="Zone"/>. Years ahead of the real date, as every gateway fixture's clock must be; 2037
    /// has 2026's calendar. See <c>WorkTaskTodayEndpointTests.Midday</c>.
    /// </summary>
    private static readonly DateTimeOffset Midday = new(2037, 10, 18, 18, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2037, 10, 18);

    private readonly FakeTimeProvider _clock = new(Midday);
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(time: _clock);

        await SetZoneAsync(_fixture.WorkspaceA, Zone);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> ChooseAsync(GatewayClient client, SeededWorkspace workspace, object body) =>
        client.PutAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/work-tasks/top-three", body, Ct);

    private static Task<HttpResponseMessage> ChooseAsync(GatewayClient client, SeededWorkspace workspace, params Guid[] ids) =>
        ChooseAsync(client, workspace, new { taskIds = ids });

    private static async Task<IReadOnlyList<Guid>> TopThreeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var day = await BodyOf(await client.GetAsync($"/api/v1/workspaces/{workspace.Slug}/work-tasks/today", Ct));

        return [.. day.GetProperty("topThree").EnumerateArray().Select(id => id.GetGuid())];
    }

    // ---- Choosing ----

    [Fact]
    public async Task A_choice_comes_back_on_the_day_in_its_order_and_a_new_one_replaces_it()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var (late, started, planned) = (
            Task_("Late", mine, WorkTaskStatus.Backlog, due: Today.AddDays(-2)),
            Task_("Started", mine, WorkTaskStatus.InProgress),
            Task_("Planned", mine, WorkTaskStatus.Backlog, scheduled: Today));
        await SeedAsync(_fixture.WorkspaceA, late, started, planned);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Empty(await TopThreeAsync(client, _fixture.WorkspaceA));

        var response = await ChooseAsync(client, _fixture.WorkspaceA, planned.Id, late.Id, started.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);
        Assert.Equal("2037-10-18", body.GetProperty("date").GetString());
        Assert.Equal([planned.Id, late.Id, started.Id], body.GetProperty("taskIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal([planned.Id, late.Id, started.Id], await TopThreeAsync(client, _fixture.WorkspaceA));

        // A new order, and then fewer: the set is replaced each time, never added to.
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA, started.Id, planned.Id)).StatusCode);
        Assert.Equal([started.Id, planned.Id], await TopThreeAsync(client, _fixture.WorkspaceA));

        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA)).StatusCode);
        Assert.Empty(await TopThreeAsync(client, _fixture.WorkspaceA));
    }

    [Fact]
    public async Task Choosing_changes_nothing_about_the_task_and_each_task_stays_in_its_own_group()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Task_("Write the headnote", mine, WorkTaskStatus.Backlog, scheduled: Today);
        await SeedAsync(_fixture.WorkspaceA, task);
        var before = await StoredAsync(task.Id);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA, task.Id)).StatusCode);

        var after = await StoredAsync(task.Id);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.SortRank, after.SortRank);

        var day = await BodyOf(await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/work-tasks/today", Ct));
        Assert.Equal(task.Id, day.GetProperty("scheduledToday")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_task_finished_today_may_be_chosen_and_stays_chosen_when_it_is_ticked_off()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var done = Done_("Finished this morning", mine, Midday.AddHours(-3));
        var open = Task_("Still to do", mine, WorkTaskStatus.InProgress);
        await SeedAsync(_fixture.WorkspaceA, done, open);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA, open.Id, done.Id)).StatusCode);
        Assert.Equal([open.Id, done.Id], await TopThreeAsync(client, _fixture.WorkspaceA));
    }

    [Fact]
    public async Task Tomorrow_starts_with_no_top_three()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var task = Task_("Started", mine, WorkTaskStatus.InProgress);
        await SeedAsync(_fixture.WorkspaceA, task);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA, task.Id)).StatusCode);

        // 04:30 UTC on the 19th is still the 18th in Chicago: the choice holds until local midnight, not UTC's. A jump
        // of hours outlives the session the fixture stamped from the old time, so each read signs in afresh.
        _clock.SetUtcNow(new DateTimeOffset(2037, 10, 19, 4, 30, 0, TimeSpan.Zero));
        using (var lateEvening = await SignInAsync(_fixture.WorkspaceA.OwnerEmail))
        {
            Assert.Equal([task.Id], await TopThreeAsync(lateEvening, _fixture.WorkspaceA));
        }

        _clock.SetUtcNow(new DateTimeOffset(2037, 10, 19, 5, 30, 0, TimeSpan.Zero));
        using var nextMorning = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Empty(await TopThreeAsync(nextMorning, _fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_chosen_task_handed_to_someone_else_drops_out_rather_than_pointing_at_nothing()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var (kept, handedOn) = (Task_("Kept", mine, WorkTaskStatus.InProgress), Task_("Handed on", mine, WorkTaskStatus.InProgress));
        await SeedAsync(_fixture.WorkspaceA, kept, handedOn);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA, handedOn.Id, kept.Id)).StatusCode);

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var row = await db.WorkTasks.SingleAsync(task => task.Id == handedOn.Id, Ct);
            row.AssigneeMembershipId = editor;

            return await db.SaveChangesAsync(Ct);
        });

        Assert.Equal([kept.Id], await TopThreeAsync(client, _fixture.WorkspaceA));
    }

    // ---- Refusals ----

    [Fact]
    public async Task More_than_three_one_named_twice_or_no_list_is_a_bad_request()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var tasks = Enumerable.Range(1, 4).Select(n => Task_($"Task {n}", mine, WorkTaskStatus.InProgress)).ToArray();
        await SeedAsync(_fixture.WorkspaceA, tasks);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        foreach (var body in new object[]
        {
            new { taskIds = tasks.Select(task => task.Id).ToArray() },
            new { taskIds = new[] { tasks[0].Id, tasks[0].Id } },
            new { taskIds = new[] { Guid.Empty } },
            new { },
        })
        {
            var response = await ChooseAsync(client, _fixture.WorkspaceA, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(PlanningErrorCodes.TopThreeInvalid, Code(await BodyOf(response)));
        }

        Assert.Empty(await TopThreeAsync(client, _fixture.WorkspaceA));
    }

    [Fact]
    public async Task Every_task_not_on_the_callers_day_is_refused_with_one_answer_and_the_last_choice_stands()
    {
        await SetZoneAsync(_fixture.WorkspaceB, Zone);

        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var inB = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner);

        var chosen = Task_("Chosen", mine, WorkTaskStatus.InProgress);
        var tomorrow = Task_("Tomorrow", mine, WorkTaskStatus.Backlog, scheduled: Today.AddDays(1));
        var someday = Task_("Someday", mine, WorkTaskStatus.Backlog);
        var yesterday = Done_("Finished yesterday", mine, Midday.AddDays(-1));
        var theEditors = Task_("The editor's", editor, WorkTaskStatus.InProgress);
        await SeedAsync(_fixture.WorkspaceA, chosen, tomorrow, someday, yesterday, theEditors);

        var bs = Task_("B's", inB, WorkTaskStatus.InProgress);
        await SeedAsync(_fixture.WorkspaceB, bs);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(client, _fixture.WorkspaceA, chosen.Id)).StatusCode);

        var answers = new List<string>();
        foreach (var refused in new[] { tomorrow.Id, someday.Id, yesterday.Id, theEditors.Id, bs.Id, Guid.NewGuid() })
        {
            var response = await ChooseAsync(client, _fixture.WorkspaceA, chosen.Id, refused);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var body = await BodyOf(response);
            Assert.Equal(PlanningErrorCodes.TopThreeUnprocessable, Code(body));
            answers.Add(body.GetProperty("errors").GetRawText());
        }

        // Nothing in the answer tells another workspace's task from a task that never was.
        Assert.Single(answers.Distinct());
        Assert.Equal([chosen.Id], await TopThreeAsync(client, _fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_workspace_with_no_time_zone_is_told_to_set_one()
    {
        using var client = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var response = await ChooseAsync(client, _fixture.WorkspaceB);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PlanningErrorCodes.TodayTimeZoneNotSet, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_viewer_may_read_the_day_but_not_choose_a_top_three()
    {
        await SetZoneAsync(_fixture.WorkspaceB, Zone);

        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await ChooseAsync(viewer, _fixture.WorkspaceB)).StatusCode);
        Assert.Empty(await TopThreeAsync(viewer, _fixture.WorkspaceB));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Two_people_in_one_workspace_keep_their_own_top_three()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var (ownersTask, editorsTask) = (Task_("Owner's", mine, WorkTaskStatus.InProgress), Task_("Editor's", editor, WorkTaskStatus.InProgress));
        await SeedAsync(_fixture.WorkspaceA, ownersTask, editorsTask);

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(asOwner, _fixture.WorkspaceA, ownersTask.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(asEditor, _fixture.WorkspaceA, editorsTask.Id)).StatusCode);

        Assert.Equal([ownersTask.Id], await TopThreeAsync(asOwner, _fixture.WorkspaceA));
        Assert.Equal([editorsTask.Id], await TopThreeAsync(asEditor, _fixture.WorkspaceA));

        // Clearing one person's leaves the other's.
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(asOwner, _fixture.WorkspaceA)).StatusCode);
        Assert.Equal([editorsTask.Id], await TopThreeAsync(asEditor, _fixture.WorkspaceA));
    }

    [Fact]
    public async Task One_workspaces_top_three_is_neither_readable_nor_writable_from_another()
    {
        await SetZoneAsync(_fixture.WorkspaceB, Zone);

        var inA = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var inB = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner);
        var (asTask, bsTask) = (Task_("A's", inA, WorkTaskStatus.InProgress), Task_("B's", inB, WorkTaskStatus.InProgress));
        await SeedAsync(_fixture.WorkspaceA, asTask);
        await SeedAsync(_fixture.WorkspaceB, bsTask);

        using var ownerOfA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(ownerOfA, _fixture.WorkspaceA, asTask.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(ownerOfB, _fixture.WorkspaceB, bsTask.Id)).StatusCode);

        Assert.Equal([asTask.Id], await TopThreeAsync(ownerOfA, _fixture.WorkspaceA));
        Assert.Equal([bsTask.Id], await TopThreeAsync(ownerOfB, _fixture.WorkspaceB));

        // A workspace the caller is not in answers as though it does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await ChooseAsync(ownerOfA, _fixture.WorkspaceB, bsTask.Id)).StatusCode);
        Assert.Equal([bsTask.Id], await TopThreeAsync(ownerOfB, _fixture.WorkspaceB));
    }

    // ---- Helpers ----

    private static WorkTask Task_(
        string title,
        Guid assignee,
        WorkTaskStatus status,
        DateOnly? scheduled = null,
        DateOnly? due = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = assignee,
            AssigneeMembershipId = assignee,
            Title = title,
            Status = status,
            Priority = WorkTaskPriority.Normal,
            ScheduledDate = scheduled,
            PlanningWeekStart = scheduled is { } day ? WorkTaskWeek.StartOf(day) : null,
            DueDate = due,
            CreatedAt = Midday,
            UpdatedAt = Midday,
        };

    private static WorkTask Done_(string title, Guid assignee, DateTimeOffset completedAt)
    {
        var task = Task_(title, assignee, WorkTaskStatus.Done);
        task.CompletedAt = completedAt;
        task.CompletedByMembershipId = assignee;
        task.CompletedFromStatus = WorkTaskStatus.InProgress;

        return task;
    }

    private Task SeedAsync(SeededWorkspace workspace, params WorkTask[] tasks) =>
        InWorkspaceAsync(workspace, async db =>
        {
            db.WorkTasks.AddRange(tasks);

            return await db.SaveChangesAsync(Ct);
        });

    private Task<WorkTask> StoredAsync(Guid taskId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking().SingleAsync(task => task.Id == taskId, Ct));

    private Task SetZoneAsync(SeededWorkspace workspace, string zone) =>
        InWorkspaceAsync(workspace, async db =>
        {
            db.BrandProfiles.Add(new BrandProfile
            {
                Id = Guid.NewGuid(),
                BrandName = "Sam's Kitchen",
                TimeZoneId = zone,
                CreatedByMembershipId = Guid.NewGuid(),
                UpdatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Midday,
                UpdatedAt = Midday,
            });

            return await db.SaveChangesAsync(Ct);
        });

    private Task<Guid> MembershipAsync(SeededWorkspace workspace, WorkspaceRole role) =>
        InWorkspaceAsync(workspace, db => db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.WorkspaceId == workspace.Id && membership.Role == role)
            .Select(membership => membership.Id)
            .SingleAsync(Ct));

    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
