using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
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
/// <c>GET .../work-tasks/week</c> through the real Gateway (13A.26): the groups and what is in none of them,
/// the week's edges in the workspace's own zone, the weeks that are not 168 hours long, a week that does not
/// move with the caller's language, the person's own order, totals that never invent an estimate, and the
/// boundary between two workspaces and between two people in one.
/// </summary>
public sealed class WorkTaskWeekEndpointTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";

    /// <summary>Thursday lunchtime in <see cref="Zone"/>, in an ordinary week with no clock change in it.</summary>
    /// <remarks>
    /// Ahead of the real date on purpose. The gateway fixture signs in with cookies whose expiry is stamped from
    /// the fake clock but judged by the client's real one, so a fake clock far enough behind real time signs
    /// nobody in.
    /// </remarks>
    private static readonly DateTimeOffset Thursday = new(2038, 10, 14, 17, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly ThisMonday = new(2038, 10, 11);

    private static readonly DateOnly LastMonday = ThisMonday.AddDays(-7);

    private static readonly DateOnly NextMonday = ThisMonday.AddDays(7);

    private readonly FakeTimeProvider _clock = new(Thursday);
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(time: _clock);

        await SetZoneAsync(_fixture.WorkspaceA, Zone);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Task<HttpResponseMessage> WeekAsync(
        GatewayClient client, SeededWorkspace workspace, string? weekStart = null, string? language = null) =>
        client.GetAsync(
            $"/api/v1/workspaces/{workspace.Slug}/work-tasks/week"
                + (weekStart is null ? string.Empty : $"?weekStart={Uri.EscapeDataString(weekStart)}"),
            language is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Accept-Language"] = language },
            Ct);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Titles(JsonElement group) =>
        [.. group.GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("title").GetString()!)];

    private static JsonElement Day(JsonElement week, int offset) => week.GetProperty("days")[offset];

    // ---- Groups ----

    [Fact]
    public async Task Every_task_is_in_the_one_group_its_state_and_dates_put_it_in_and_none_in_two()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("Monday's", mine, scheduled: ThisMonday),
            Task_("Sunday's", mine, scheduled: ThisMonday.AddDays(6)),
            Task_("This week, no day", mine, week: ThisMonday),
            Task_("No week at all", mine),
            Task_("Left over, with a day", mine, scheduled: LastMonday.AddDays(2)),
            Task_("Left over, no day", mine, week: LastMonday),
            Task_("Started last week", mine, WorkTaskStatus.InProgress, scheduled: LastMonday),
            Task_("Held up", mine, WorkTaskStatus.Waiting),
            Done_("Finished Tuesday", mine, Thursday.AddDays(-2)),

            // None of these belongs to this week.
            Task_("Next week's", mine, scheduled: NextMonday),
            Task_("Next week, no day", mine, week: NextMonday),
            Done_("Finished last week", mine, Thursday.AddDays(-7)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA));

        Assert.Equal(["Monday's"], Titles(Day(week, 0)));
        Assert.Equal(["Sunday's"], Titles(Day(week, 6)));
        Assert.Equal(["This week, no day"], Titles(week.GetProperty("backlog")));
        Assert.Equal(["No week at all"], Titles(week.GetProperty("unplanned")));
        Assert.Equal(["Left over, no day", "Left over, with a day"], Titles(week.GetProperty("carriedOver")));
        Assert.Equal(["Started last week"], Titles(week.GetProperty("inProgress")));
        Assert.Equal(["Held up"], Titles(week.GetProperty("waiting")));
        Assert.Equal(["Finished Tuesday"], Titles(week.GetProperty("completed")));

        var everywhere = AllTitles(week);
        Assert.Equal(everywhere.Count, everywhere.Distinct().Count());
        Assert.Equal(9, everywhere.Count);
    }

    [Fact]
    public async Task Work_carries_over_into_the_current_week_only_and_a_past_week_still_shows_its_own_plan()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("Never got to it", mine, scheduled: LastMonday.AddDays(1)),
            Task_("Someday", mine),
            Task_("Under way", mine, WorkTaskStatus.InProgress));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var past = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, Iso(LastMonday)));
        Assert.False(past.GetProperty("isCurrentWeek").GetBoolean());
        Assert.Equal(["Never got to it"], Titles(Day(past, 1)));
        Assert.Empty(Titles(past.GetProperty("carriedOver")));

        var ahead = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, Iso(NextMonday)));
        Assert.Empty(Titles(ahead.GetProperty("carriedOver")));
        Assert.DoesNotContain("Never got to it", AllTitles(ahead));

        // Unplanned work and the person's current state show in every week they look at.
        foreach (var week in new[] { past, ahead })
        {
            Assert.Equal(["Someday"], Titles(week.GetProperty("unplanned")));
            Assert.Equal(["Under way"], Titles(week.GetProperty("inProgress")));
        }
    }

    [Fact]
    public async Task The_answer_has_the_weeks_own_groups_and_none_of_the_content_boards_columns()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await WeekAsync(client, _fixture.WorkspaceA);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var week = await BodyOf(response);
        var names = week.EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(
            [
                "weekStart", "weekEnd", "today", "timeZoneId", "isCurrentWeek", "planned", "days",
                "backlog", "unplanned", "carriedOver", "inProgress", "waiting", "completed",
            ],
            names);

        var columns = Enum.GetNames<ContentCardColumn>();
        Assert.DoesNotContain(names, name => columns.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    // ---- Week boundaries ----

    [Fact]
    public async Task Left_out_the_week_is_the_one_holding_today_in_the_workspaces_zone()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA));
        Assert.Equal(Iso(ThisMonday), week.GetProperty("weekStart").GetString());
        Assert.Equal(Iso(ThisMonday.AddDays(6)), week.GetProperty("weekEnd").GetString());
        Assert.Equal("2038-10-14", week.GetProperty("today").GetString());
        Assert.Equal(Zone, week.GetProperty("timeZoneId").GetString());
        Assert.True(week.GetProperty("isCurrentWeek").GetBoolean());
        Assert.True(Day(week, 3).GetProperty("isToday").GetBoolean());

        // 04:59 UTC on Monday the 18th is still Sunday evening in Chicago. A server reading its own date would
        // already be in the next week.
        // Four days on, the first session has lapsed on the fake clock, so this is a fresh sign-in.
        _clock.SetUtcNow(new DateTimeOffset(2038, 10, 18, 4, 59, 0, TimeSpan.Zero));
        using var later = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        week = await BodyOf(await WeekAsync(later, _fixture.WorkspaceA));
        Assert.Equal(Iso(ThisMonday), week.GetProperty("weekStart").GetString());
        Assert.True(Day(week, 6).GetProperty("isToday").GetBoolean());

        _clock.SetUtcNow(new DateTimeOffset(2038, 10, 18, 5, 0, 0, TimeSpan.Zero));
        week = await BodyOf(await WeekAsync(later, _fixture.WorkspaceA));
        Assert.Equal(Iso(NextMonday), week.GetProperty("weekStart").GetString());
        Assert.True(Day(week, 0).GetProperty("isToday").GetBoolean());
    }

    [Fact]
    public async Task A_named_week_is_read_whatever_today_is_and_its_days_run_monday_to_sunday()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, "2039-01-03"));

        Assert.Equal("2039-01-03", week.GetProperty("weekStart").GetString());
        Assert.False(week.GetProperty("isCurrentWeek").GetBoolean());
        Assert.Equal(
            ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"],
            week.GetProperty("days").EnumerateArray().Select(day => day.GetProperty("dayOfWeek").GetString()!));
        Assert.All(week.GetProperty("days").EnumerateArray(), day => Assert.False(day.GetProperty("isToday").GetBoolean()));
    }

    [Theory]
    [InlineData("2026-10-13")] // a Tuesday
    [InlineData("2026-10-18")] // a Sunday, which starts the week where weeks start on Sunday
    [InlineData("10/12/2026")] // the Monday, but in a format that reads differently elsewhere
    [InlineData("12-10-2026")]
    [InlineData("2026-W42")]
    [InlineData("1899-12-25")] // a Monday, before any week that can be planned
    [InlineData("monday")]
    public async Task A_week_named_any_other_way_than_its_monday_as_yyyy_mm_dd_is_refused(string weekStart)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await WeekAsync(client, _fixture.WorkspaceA, weekStart);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(PlanningErrorCodes.WeekInvalid, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("weekStart", out _));
    }

    [Theory]
    [InlineData("en-US")] // weeks start on Sunday
    [InlineData("ar-SA")] // weeks start on Saturday, and dates format in another calendar
    [InlineData("fr-FR")]
    public async Task The_callers_language_does_not_move_the_week_or_reformat_its_dates(string culture)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var (current, currentUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            // Set on the test's own context too, which an in-process host shares, so code that formats or reads
            // a date by the current culture would show here rather than only on a foreign server.
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, language: culture));
            Assert.Equal(Iso(ThisMonday), week.GetProperty("weekStart").GetString());
            Assert.Equal("Monday", Day(week, 0).GetProperty("dayOfWeek").GetString());
            Assert.Equal(Iso(ThisMonday), Day(week, 0).GetProperty("date").GetString());

            var named = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, Iso(NextMonday), culture));
            Assert.Equal(Iso(NextMonday), named.GetProperty("weekStart").GetString());
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, currentUi);
        }
    }

    [Fact]
    public async Task A_workspace_with_no_time_zone_is_told_to_set_one_even_for_a_named_week()
    {
        using var client = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        foreach (var weekStart in new[] { null, Iso(ThisMonday) })
        {
            var response = await WeekAsync(client, _fixture.WorkspaceB, weekStart);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(PlanningErrorCodes.WeekTimeZoneNotSet, problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Finished_this_week_starts_at_mondays_local_midnight_and_ends_before_the_next()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // Chicago is on CDT all week: the week runs 05:00Z Monday to 05:00Z the following Monday.
        await SeedAsync(_fixture.WorkspaceA,
            Done_("The first minute", mine, new DateTimeOffset(2038, 10, 11, 5, 0, 0, TimeSpan.Zero)),
            Done_("The last minute", mine, new DateTimeOffset(2038, 10, 18, 4, 59, 0, TimeSpan.Zero)),
            Done_("Still the week before", mine, new DateTimeOffset(2038, 10, 11, 4, 59, 0, TimeSpan.Zero)),
            Done_("Already next week", mine, new DateTimeOffset(2038, 10, 18, 5, 0, 0, TimeSpan.Zero)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA));

        Assert.Equal(["The last minute", "The first minute"], Titles(week.GetProperty("completed")));
    }

    [Fact]
    public async Task A_week_the_clocks_fall_back_in_is_169_hours_and_counts_the_repeated_hour_once_each()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // Chicago leaves CDT at 02:00 on Sunday 7 November 2038. The week of 1 November runs 05:00Z that Monday
        // to 06:00Z on 8 November, and the local hour 01:00-02:00 on the Sunday happens twice.
        await SeedAsync(_fixture.WorkspaceA,
            Done_("01:30, the first time", mine, new DateTimeOffset(2038, 11, 7, 6, 30, 0, TimeSpan.Zero)),
            Done_("01:30, the second time", mine, new DateTimeOffset(2038, 11, 7, 7, 30, 0, TimeSpan.Zero)),
            Done_("The 169th hour", mine, new DateTimeOffset(2038, 11, 8, 5, 30, 0, TimeSpan.Zero)),
            Done_("Into the next week", mine, new DateTimeOffset(2038, 11, 8, 6, 0, 0, TimeSpan.Zero)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, "2038-11-01"));

        Assert.Equal(
            ["The 169th hour", "01:30, the second time", "01:30, the first time"],
            Titles(week.GetProperty("completed")));

        var next = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, "2038-11-08"));
        Assert.Equal(["Into the next week"], Titles(next.GetProperty("completed")));
    }

    [Fact]
    public async Task A_week_the_clocks_spring_forward_in_is_167_hours()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        // Chicago enters CDT on Sunday 13 March 2039. The week of 7 March runs 06:00Z that Monday to 05:00Z on
        // 14 March.
        await SeedAsync(_fixture.WorkspaceA,
            Done_("The first minute", mine, new DateTimeOffset(2039, 3, 7, 6, 0, 0, TimeSpan.Zero)),
            Done_("The last minute", mine, new DateTimeOffset(2039, 3, 14, 4, 59, 0, TimeSpan.Zero)),
            Done_("Still the week before", mine, new DateTimeOffset(2039, 3, 7, 5, 59, 0, TimeSpan.Zero)),
            Done_("Already next week", mine, new DateTimeOffset(2039, 3, 14, 5, 0, 0, TimeSpan.Zero)));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA, "2039-03-07"));

        Assert.Equal(["The last minute", "The first minute"], Titles(week.GetProperty("completed")));
    }

    // ---- Order ----

    [Fact]
    public async Task A_week_is_in_the_persons_own_order_and_priority_does_not_reorder_it()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("Second", mine, scheduled: ThisMonday, rank: 2048),
            Task_("Third, but urgent", mine, scheduled: ThisMonday, rank: 3072, priority: WorkTaskPriority.High),
            Task_("First", mine, scheduled: ThisMonday, rank: 1024),
            Task_("Backlog second", mine, week: ThisMonday, rank: 2048),
            Task_("Backlog first", mine, week: ThisMonday, rank: 1024, priority: WorkTaskPriority.Low));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA));

        Assert.Equal(["First", "Second", "Third, but urgent"], Titles(Day(week, 0)));
        Assert.Equal(["Backlog first", "Backlog second"], Titles(week.GetProperty("backlog")));
    }

    [Fact]
    public async Task Two_tasks_on_one_rank_read_in_the_same_order_every_time()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            [.. Enumerable.Range(1, 6).Select(n => Task_($"Tied {n}", mine, scheduled: ThisMonday.AddDays(2), rank: 1024))]);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = Titles(Day(await BodyOf(await WeekAsync(client, _fixture.WorkspaceA)), 2));

        for (var read = 0; read < 3; read++)
        {
            Assert.Equal(first, Titles(Day(await BodyOf(await WeekAsync(client, _fixture.WorkspaceA)), 2)));
        }

        Assert.Equal(6, first.Count);
    }

    // ---- Capacity ----

    [Fact]
    public async Task Totals_add_up_the_estimates_there_are_and_never_count_a_missing_one_as_nothing()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        await SeedAsync(_fixture.WorkspaceA,
            // Monday: two estimated, one not.
            Task_("Half an hour", mine, scheduled: ThisMonday, estimate: 30),
            Task_("An hour", mine, scheduled: ThisMonday, estimate: 60),
            Task_("Who knows", mine, scheduled: ThisMonday),

            // Tuesday: nothing estimated at all.
            Task_("Unknown", mine, scheduled: ThisMonday.AddDays(1)),

            // The week's backlog counts towards what is planned; unplanned and carried-over work does not.
            Task_("This week, sometime", mine, week: ThisMonday, estimate: 45),
            Task_("Someday", mine, estimate: 600),
            Task_("Left over", mine, week: LastMonday, estimate: 600),

            // Under way and finished carry their own totals, outside what is planned.
            Task_("Under way", mine, WorkTaskStatus.InProgress, estimate: 120),
            Done_("Finished", mine, Thursday.AddHours(-1), estimate: 15));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA));

        AssertTotals(Day(week, 0), tasks: 3, estimated: 2, minutes: 90);
        AssertTotals(Day(week, 1), tasks: 1, estimated: 0, minutes: null);
        AssertTotals(Day(week, 2), tasks: 0, estimated: 0, minutes: null);
        AssertTotals(week.GetProperty("backlog"), tasks: 1, estimated: 1, minutes: 45);
        AssertTotals(week.GetProperty("unplanned"), tasks: 1, estimated: 1, minutes: 600);
        AssertTotals(week.GetProperty("carriedOver"), tasks: 1, estimated: 1, minutes: 600);
        AssertTotals(week.GetProperty("inProgress"), tasks: 1, estimated: 1, minutes: 120);
        AssertTotals(week.GetProperty("completed"), tasks: 1, estimated: 1, minutes: 15);

        // Monday, Tuesday and the backlog: five tasks, three of them estimated, at 135 minutes.
        var planned = week.GetProperty("planned");
        Assert.Equal(5, planned.GetProperty("taskCount").GetInt32());
        Assert.Equal(3, planned.GetProperty("estimatedTaskCount").GetInt32());
        Assert.Equal(135, planned.GetProperty("estimatedMinutes").GetInt64());
    }

    [Fact]
    public async Task A_crowded_day_is_cut_at_its_cap_counts_everything_and_does_not_starve_the_rest_of_the_week()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        const int cap = WorkTaskPolicy.WeekGroupMaxRows;

        await SeedAsync(_fixture.WorkspaceA,
            [
                .. Enumerable.Range(0, cap + 3).Select(n => Task_($"Monday {n}", mine, scheduled: ThisMonday, rank: n, estimate: 10)),
                Task_("Tuesday's one", mine, scheduled: ThisMonday.AddDays(1)),
            ]);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceA));

        var monday = Day(week, 0);
        Assert.Equal(cap, Titles(monday).Count);
        Assert.Equal("Monday 0", Titles(monday)[0]);
        Assert.True(monday.GetProperty("hasMore").GetBoolean());
        AssertTotals(monday, tasks: cap + 3, estimated: cap + 3, minutes: (cap + 3) * 10);

        Assert.Equal(["Tuesday's one"], Titles(Day(week, 1)));
        Assert.False(Day(week, 1).GetProperty("hasMore").GetBoolean());
    }

    // ---- Who sees what ----

    [Fact]
    public async Task One_members_week_never_shows_another_members_work()
    {
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);

        await SeedAsync(_fixture.WorkspaceA,
            Task_("The owner's Monday", owner, scheduled: ThisMonday, estimate: 30),
            Task_("The editor's Monday", editor, scheduled: ThisMonday, estimate: 90));

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var ownersWeek = await BodyOf(await WeekAsync(asOwner, _fixture.WorkspaceA));
        var editorsWeek = await BodyOf(await WeekAsync(asEditor, _fixture.WorkspaceA));

        Assert.Equal(["The owner's Monday"], Titles(Day(ownersWeek, 0)));
        Assert.Equal(["The editor's Monday"], Titles(Day(editorsWeek, 0)));

        // The totals are the caller's too, not the workspace's.
        AssertTotals(Day(ownersWeek, 0), tasks: 1, estimated: 1, minutes: 30);
        AssertTotals(Day(editorsWeek, 0), tasks: 1, estimated: 1, minutes: 90);
    }

    [Fact]
    public async Task One_workspaces_week_never_shows_anothers()
    {
        await SetZoneAsync(_fixture.WorkspaceB, Zone);

        var inA = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var inB = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner);

        // The same shapes in both, so a leak would show as a second title or a doubled total.
        foreach (var (workspace, member, name) in new[] { (_fixture.WorkspaceA, inA, "A"), (_fixture.WorkspaceB, inB, "B") })
        {
            await SeedAsync(workspace,
                Task_($"{name}'s Monday", member, scheduled: ThisMonday, estimate: 30),
                Task_($"{name}'s backlog", member, week: ThisMonday),
                Task_($"{name}'s someday", member),
                Task_($"{name}'s leftover", member, week: LastMonday),
                Task_($"{name}'s work", member, WorkTaskStatus.InProgress),
                Done_($"{name}'s finished", member, Thursday.AddHours(-1)));
        }

        using var ownerOfA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var weekOfA = await BodyOf(await WeekAsync(ownerOfA, _fixture.WorkspaceA));
        var weekOfB = await BodyOf(await WeekAsync(ownerOfB, _fixture.WorkspaceB));

        Assert.All(AllTitles(weekOfA), title => Assert.StartsWith("A's", title, StringComparison.Ordinal));
        Assert.All(AllTitles(weekOfB), title => Assert.StartsWith("B's", title, StringComparison.Ordinal));
        Assert.Equal(6, AllTitles(weekOfA).Count);
        AssertTotals(Day(weekOfA, 0), tasks: 1, estimated: 1, minutes: 30);
        Assert.Equal(2, weekOfA.GetProperty("planned").GetProperty("taskCount").GetInt32());

        // And the slug of a workspace the caller is not in is not a way to read it.
        Assert.Equal(HttpStatusCode.NotFound, (await WeekAsync(ownerOfA, _fixture.WorkspaceB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await WeekAsync(ownerOfA, _fixture.WorkspaceB, Iso(ThisMonday))).StatusCode);
    }

    [Fact]
    public async Task A_viewer_reads_their_own_week()
    {
        await SetZoneAsync(_fixture.WorkspaceB, Zone);

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        var viewer = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Viewer);
        await SeedAsync(_fixture.WorkspaceB, Task_("Handed to the viewer", viewer, scheduled: ThisMonday));

        using var client = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var week = await BodyOf(await WeekAsync(client, _fixture.WorkspaceB));

        Assert.Equal(["Handed to the viewer"], Titles(Day(week, 0)));
    }

    // ---- Helpers ----

    private static void AssertTotals(JsonElement group, int tasks, int estimated, long? minutes)
    {
        var totals = group.GetProperty("totals");

        Assert.Equal(tasks, totals.GetProperty("taskCount").GetInt32());
        Assert.Equal(estimated, totals.GetProperty("estimatedTaskCount").GetInt32());

        if (minutes is { } expected)
        {
            Assert.Equal(expected, totals.GetProperty("estimatedMinutes").GetInt64());
        }
        else
        {
            Assert.True(
                !totals.TryGetProperty("estimatedMinutes", out var value) || value.ValueKind == JsonValueKind.Null,
                "a group with no estimate must not report minutes");
        }
    }

    private static List<string> AllTitles(JsonElement week) =>
    [
        .. week.GetProperty("days").EnumerateArray().SelectMany(Titles),
        .. new[] { "backlog", "unplanned", "carriedOver", "inProgress", "waiting", "completed" }
            .SelectMany(group => Titles(week.GetProperty(group))),
    ];

    /// <summary>A task as the planner stores one: a day implies the week holding it, as capture makes it.</summary>
    private static WorkTask Task_(
        string title,
        Guid assignee,
        WorkTaskStatus status = WorkTaskStatus.Backlog,
        DateOnly? scheduled = null,
        DateOnly? week = null,
        long rank = WorkTaskPolicy.RankGap,
        int? estimate = null,
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
            PlanningWeekStart = week ?? (scheduled is { } day ? WorkTaskWeek.StartOf(day) : null),
            EstimateMinutes = estimate,
            SortRank = rank,
            CreatedAt = Thursday,
            UpdatedAt = Thursday,
        };

    /// <summary>A finished task, with the three facts its check constraint insists travel together.</summary>
    private static WorkTask Done_(string title, Guid assignee, DateTimeOffset completedAt, int? estimate = null)
    {
        var task = Task_(title, assignee, WorkTaskStatus.Done, estimate: estimate);
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
                CreatedAt = Thursday,
                UpdatedAt = Thursday,
            });

            return await db.SaveChangesAsync(Ct);
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
