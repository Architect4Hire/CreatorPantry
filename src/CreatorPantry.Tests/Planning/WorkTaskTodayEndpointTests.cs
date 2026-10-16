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
/// <c>GET .../work-tasks/today</c> through the real Gateway (13A.25, TASK-002): the grouping, the day
/// boundary in the workspace's own zone, the two days a year that are not 24 hours long, suggestions, and
/// the boundary between two workspaces and between two people in one.
/// </summary>
public sealed class WorkTaskTodayEndpointTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";

    /// <summary>
    /// Midday in <see cref="Zone"/> on an ordinary autumn day, so nothing here turns on a boundary unless
    /// the test moves the clock to one.
    /// </summary>
    /// <remarks>
    /// Years ahead of the real date on purpose. The gateway fixture's sign-in cookies are stamped from this fake
    /// clock and judged by the test client's real one, so a clock that falls behind real time signs nobody in and
    /// every test here fails at setup. 2037 has 2026's calendar exactly — the same weekdays and the same Chicago
    /// clock changes — which is why it was chosen rather than any later year.
    /// </remarks>
    private static readonly DateTimeOffset Midday = new(2037, 10, 18, 18, 0, 0, TimeSpan.Zero);

    /// <summary>The local date <see cref="Midday"/> falls on. Deliberately not the UTC date at every hour.</summary>
    private const string Today = "2037-10-18";

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

    private static Task<HttpResponseMessage> TodayAsync(
        GatewayClient client, SeededWorkspace workspace, bool suggestions = false) =>
        client.GetAsync(
            $"/api/v1/workspaces/{workspace.Slug}/work-tasks/today" + (suggestions ? "?includeSuggestions=true" : string.Empty),
            Ct);

    private static IReadOnlyList<string> Titles(JsonElement day, string group) =>
        [.. day.GetProperty(group).EnumerateArray().Select(task => task.GetProperty("title").GetString()!)];

    // ---- Grouping ----

    [Fact]
    public async Task The_day_is_grouped_by_precedence_and_no_task_is_in_two_groups()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            // Started and already late. Overdue outranks in progress: it is the one that presses.
            Task_("Late and started", mine, WorkTaskStatus.InProgress, due: new DateOnly(2037, 10, 16)),
            Task_("Just started", mine, WorkTaskStatus.InProgress),
            Task_("Held up", mine, WorkTaskStatus.Waiting),
            Task_("Planned for today", mine, WorkTaskStatus.Backlog, scheduled: new DateOnly(2037, 10, 18)),
            Done_("Finished this morning", mine, Midday.AddHours(-3)),

            // Neither of these belongs to today at all.
            Done_("Finished yesterday", mine, Midday.AddDays(-1)),
            Task_("Someday", mine, WorkTaskStatus.Backlog));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        Assert.Equal(["Late and started"], Titles(day, "overdue"));
        Assert.Equal(["Just started"], Titles(day, "inProgress"));
        Assert.Equal(["Held up"], Titles(day, "waiting"));
        Assert.Equal(["Planned for today"], Titles(day, "scheduledToday"));
        Assert.Equal(["Finished this morning"], Titles(day, "completedToday"));

        // The precedence hides nothing: the row still says what it is.
        Assert.Equal("InProgress", day.GetProperty("overdue")[0].GetProperty("status").GetString());

        // Nothing counted twice, and an unplanned task is offered only when asked for.
        var everywhere = new[] { "overdue", "inProgress", "waiting", "scheduledToday", "completedToday" }
            .SelectMany(group => day.GetProperty(group).EnumerateArray().Select(task => task.GetProperty("id").GetGuid()))
            .ToList();
        Assert.Equal(everywhere.Count, everywhere.Distinct().Count());
        Assert.Equal(5, everywhere.Count);
    }

    [Fact]
    public async Task The_day_is_the_workspaces_own_and_says_which_zone_it_read()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // 02:00 UTC on the 19th is still the evening of the 18th in Chicago. A server reading its own date
        // would answer 2037-10-19.
        _clock.SetUtcNow(new DateTimeOffset(2037, 10, 19, 2, 0, 0, TimeSpan.Zero));

        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        Assert.Equal(Today, day.GetProperty("date").GetString());
        Assert.Equal(Zone, day.GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task A_workspace_with_no_time_zone_is_told_to_set_one_rather_than_guessed_at()
    {
        using var client = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var response = await TodayAsync(client, _fixture.WorkspaceB);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(PlanningErrorCodes.TodayTimeZoneNotSet, problem.GetProperty("code").GetString());
        Assert.Contains("brand settings", problem.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- The day boundary ----

    [Fact]
    public async Task Finished_today_starts_at_local_midnight_and_ends_before_the_next()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // Chicago is UTC-5 on this date, so the local day is 05:00Z to 05:00Z.
        var midnight = new DateTimeOffset(2037, 10, 18, 5, 0, 0, TimeSpan.Zero);

        await SeedAsync(_fixture.WorkspaceA,
            Done_("On the stroke of midnight", mine, midnight),
            Done_("A moment before", mine, midnight.AddMilliseconds(-1)),
            Done_("The last moment of the day", mine, midnight.AddDays(1).AddMilliseconds(-1)),
            Done_("Tomorrow's first", mine, midnight.AddDays(1)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        // Newest first, and the two midnights belong to one day each.
        Assert.Equal(["The last moment of the day", "On the stroke of midnight"], Titles(day, "completedToday"));
    }

    [Fact]
    public async Task A_day_that_falls_back_is_twenty_five_hours_and_counts_each_one_once()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // 1 November 2037: Chicago leaves CDT. The local day runs 05:00Z to 06:00Z the next day, and the
        // local hour 01:00-02:00 happens twice.
        _clock.SetUtcNow(new DateTimeOffset(2037, 11, 1, 18, 0, 0, TimeSpan.Zero));

        await SeedAsync(_fixture.WorkspaceA,
            Done_("Before the clocks changed", mine, new DateTimeOffset(2037, 11, 1, 5, 30, 0, TimeSpan.Zero)),
            Done_("The same hour again", mine, new DateTimeOffset(2037, 11, 1, 6, 30, 0, TimeSpan.Zero)),
            Done_("The twenty-fifth hour", mine, new DateTimeOffset(2037, 11, 2, 5, 59, 0, TimeSpan.Zero)),
            Done_("Into the next day", mine, new DateTimeOffset(2037, 11, 2, 6, 0, 0, TimeSpan.Zero)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        Assert.Equal("2037-11-01", day.GetProperty("date").GetString());
        Assert.Equal(
            ["The twenty-fifth hour", "The same hour again", "Before the clocks changed"],
            Titles(day, "completedToday"));
    }

    [Fact]
    public async Task A_day_that_springs_forward_is_twenty_three_hours()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // 14 March 2038: Chicago enters CDT. The local day runs 06:00Z to 05:00Z the next day. The next such
        // day after the fixture's clock, because a fake clock is not allowed to run backwards.
        _clock.SetUtcNow(new DateTimeOffset(2038, 3, 14, 18, 0, 0, TimeSpan.Zero));

        await SeedAsync(_fixture.WorkspaceA,
            Done_("The first minute", mine, new DateTimeOffset(2038, 3, 14, 6, 0, 0, TimeSpan.Zero)),
            Done_("The last minute", mine, new DateTimeOffset(2038, 3, 15, 4, 59, 0, TimeSpan.Zero)),
            Done_("Already tomorrow", mine, new DateTimeOffset(2038, 3, 15, 5, 0, 0, TimeSpan.Zero)),
            Done_("Still yesterday", mine, new DateTimeOffset(2038, 3, 14, 5, 59, 0, TimeSpan.Zero)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        Assert.Equal("2038-03-14", day.GetProperty("date").GetString());
        Assert.Equal(["The last minute", "The first minute"], Titles(day, "completedToday"));
    }

    // ---- Order and bounds ----

    [Fact]
    public async Task Overdue_reads_soonest_due_first_and_then_by_what_presses()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var longAgo = new DateOnly(2037, 10, 10);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("Second", mine, WorkTaskStatus.Backlog, due: longAgo.AddDays(1)),
            Task_("First", mine, WorkTaskStatus.Backlog, due: longAgo),
            Task_("Third, and urgent", mine, WorkTaskStatus.Backlog, due: longAgo.AddDays(2), priority: WorkTaskPriority.High),
            Task_("Fourth", mine, WorkTaskStatus.Backlog, due: longAgo.AddDays(2)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        Assert.Equal(["First", "Second", "Third, and urgent", "Fourth"], Titles(day, "overdue"));
    }

    [Fact]
    public async Task A_group_past_its_cap_says_so_and_returns_the_cap()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var today = new DateOnly(2037, 10, 18);

        await SeedAsync(
            _fixture.WorkspaceA,
            [.. Enumerable.Range(0, WorkTaskPolicy.TodayGroupMaxRows + 1)
                .Select(index => Task_($"Task {index:D3}", mine, WorkTaskStatus.Backlog, scheduled: today))]);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var day = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));

        Assert.Equal(WorkTaskPolicy.TodayGroupMaxRows, day.GetProperty("scheduledToday").GetArrayLength());
        Assert.True(day.GetProperty("scheduledTodayHasMore").GetBoolean());

        // A group that fits says the opposite, so the flag means something.
        Assert.False(day.GetProperty("overdueHasMore").GetBoolean());
    }

    // ---- Suggestions ----

    [Fact]
    public async Task Suggestions_are_off_unless_asked_for_and_then_labelled_capped_and_harmless()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("Urgent and unplanned", mine, WorkTaskStatus.Backlog, priority: WorkTaskPriority.High),
            Task_("Due soon and unplanned", mine, WorkTaskStatus.Backlog, due: new DateOnly(2037, 10, 20)),
            Task_("Ordinary one", mine, WorkTaskStatus.Backlog),
            Task_("Ordinary two", mine, WorkTaskStatus.Backlog),
            Task_("Ordinary three", mine, WorkTaskStatus.Backlog),

            // Already in a group above, so never offered.
            Task_("Planned for today", mine, WorkTaskStatus.Backlog, scheduled: new DateOnly(2037, 10, 18)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var without = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA));
        Assert.Empty(without.GetProperty("suggested").EnumerateArray());

        var with = await BodyOf(await TodayAsync(client, _fixture.WorkspaceA, suggestions: true));
        var suggested = with.GetProperty("suggested");

        Assert.Equal(WorkTaskPolicy.TodaySuggestionCount, suggested.GetArrayLength());
        Assert.All(suggested.EnumerateArray(), item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));

            // Never one the day already accounts for.
            Assert.NotEqual("Planned for today", item.GetProperty("task").GetProperty("title").GetString());
        });

        // Most pressing first, and each says why in its own words.
        Assert.Equal("Urgent and unplanned", suggested[0].GetProperty("task").GetProperty("title").GetString());
        Assert.Contains("High priority", suggested[0].GetProperty("reason").GetString()!, StringComparison.Ordinal);

        // Offering one schedules nothing: every task is exactly as it was.
        var untouched = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking()
            .CountAsync(task => task.Status == WorkTaskStatus.Backlog && task.ScheduledDate == null, Ct));
        Assert.Equal(5, untouched);
    }

    // ---- Whose day it is ----

    [Fact]
    public async Task One_members_day_never_shows_another_members_work()
    {
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("The owner's", owner, WorkTaskStatus.InProgress),
            Task_("The editor's", editor, WorkTaskStatus.InProgress));

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(["The owner's"], Titles(await BodyOf(await TodayAsync(asOwner, _fixture.WorkspaceA)), "inProgress"));
        Assert.Equal(["The editor's"], Titles(await BodyOf(await TodayAsync(asEditor, _fixture.WorkspaceA)), "inProgress"));
    }

    [Fact]
    public async Task One_workspaces_day_never_shows_anothers()
    {
        await SetZoneAsync(_fixture.WorkspaceB, Zone);

        var inA = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var inB = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA, Task_("A's work", inA, WorkTaskStatus.InProgress));
        await SeedAsync(_fixture.WorkspaceB, Task_("B's work", inB, WorkTaskStatus.InProgress));

        using var ownerOfA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        Assert.Equal(["A's work"], Titles(await BodyOf(await TodayAsync(ownerOfA, _fixture.WorkspaceA)), "inProgress"));
        Assert.Equal(["B's work"], Titles(await BodyOf(await TodayAsync(ownerOfB, _fixture.WorkspaceB)), "inProgress"));

        // And the slug of a workspace the caller is not in is not a way to read it.
        Assert.Equal(HttpStatusCode.NotFound, (await TodayAsync(ownerOfA, _fixture.WorkspaceB)).StatusCode);
    }

    // ---- Helpers ----

    private static WorkTask Task_(
        string title,
        Guid assignee,
        WorkTaskStatus status,
        DateOnly? scheduled = null,
        DateOnly? due = null,
        WorkTaskPriority priority = WorkTaskPriority.Normal) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = assignee,
            AssigneeMembershipId = assignee,
            Title = title,
            Status = status,
            Priority = priority,
            ScheduledDate = scheduled,
            PlanningWeekStart = scheduled is { } day ? WorkTaskWeek.StartOf(day) : null,
            DueDate = due,
            CreatedAt = Midday,
            UpdatedAt = Midday,
        };

    /// <summary>A finished task, with the three facts its check constraint insists travel together.</summary>
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

    /// <summary>
    /// One workspace's membership for a role. The role is what tells the two seeded members apart, and the
    /// membership id is what every task is keyed on.
    /// </summary>
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
