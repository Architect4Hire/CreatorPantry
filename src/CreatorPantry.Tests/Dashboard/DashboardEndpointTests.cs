using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Dashboard;

/// <summary>
/// <c>GET .../dashboard</c> through the real Gateway (13.11, 13A.33a, HOME-UI-001/002, PERF-001): what each
/// section counts and leaves out, that every list stays at its cap and the request's statements stay fixed
/// however much the workspace holds, that one failing section is only that section, that the two sections which
/// are the caller's own answer two members differently, and the boundary between workspaces.
/// </summary>
public sealed class DashboardEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly CommandCounter _commands = new();
    private readonly FailingLibrary _library = new();
    private readonly FailingLibrary _brand = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.ConfigureDbContext<CreatorPantryDbContext>(options => options.AddInterceptors(_commands));

            // The real library facade, behind a switch a test throws to make that one section fail.
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IMediaDashboardFacade));
            services.Remove(real);
            services.AddScoped<IMediaDashboardFacade>(provider => new SwitchableLibrary(
                (IMediaDashboardFacade)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), _library));

            // The same switch for the brand profile, which is where the workspace's zone comes from: a zone
            // that could not be read is a different answer from a zone nobody has set.
            var brandProfile = services.Single(descriptor => descriptor.ServiceType == typeof(IBrandProfileFacade));
            services.Remove(brandProfile);
            services.AddScoped<IBrandProfileFacade>(provider => new SwitchableBrand(
                (IBrandProfileFacade)ActivatorUtilities.CreateInstance(provider, brandProfile.ImplementationType!), _brand));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DashboardOf(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/dashboard";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private async Task<JsonElement> ReadAsync(SeededWorkspace workspace, string? email = null)
    {
        using var client = await SignInAsync(email ?? workspace.OwnerEmail);
        var response = await client.GetAsync(DashboardOf(workspace), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static JsonElement Data(JsonElement body, string section)
    {
        var part = body.GetProperty(section);
        Assert.Equal("Ready", part.GetProperty("status").GetString());

        return part.GetProperty("data");
    }

    /// <summary>
    /// Gives a workspace the scheduling zone every date-dependent section now needs. Without one the
    /// dashboard answers with no day and marks `upcoming` unavailable rather than guessing at UTC, so a
    /// test about what is coming up has to say which calendar it means.
    /// </summary>
    private async Task SetZoneAsync(SeededWorkspace workspace, string zone = "America/Chicago")
    {
        using var client = await SignInAsync(workspace.OwnerEmail);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/brand-profile",
            new { brandName = "Cozy Fall", timeZoneId = zone },
            Ct)).StatusCode);
    }

    private static int CountOf(JsonElement counts, string key, string name) =>
        counts.EnumerateArray().Single(entry => entry.GetProperty(key).GetString() == name).GetProperty("count").GetInt32();

    // ---- The sections ----

    [Fact]
    public async Task An_empty_workspace_answers_every_section_with_zeros_and_empty_lists()
    {
        // A zone, so that what this test is about is the workspace being empty rather than unconfigured.
        await SetZoneAsync(_fixture.WorkspaceA);

        var body = await ReadAsync(_fixture.WorkspaceA);

        foreach (var section in new[] { "workflows", "workPlan", "recipesInProgress", "readinessBlockers", "aiProposals", "testFollowUps", "upcoming", "board", "recentUploads", "publishing" })
        {
            Assert.Equal("Ready", body.GetProperty(section).GetProperty("status").GetString());
        }

        var recipes = Data(body, "recipesInProgress");
        Assert.Equal(["Draft", "InDevelopment", "Testing", "ReadyForReview"], recipes.GetProperty("counts").EnumerateArray().Select(entry => entry.GetProperty("status").GetString()));
        Assert.All(recipes.GetProperty("counts").EnumerateArray(), entry => Assert.Equal(0, entry.GetProperty("count").GetInt32()));
        Assert.Equal(0, Data(body, "readinessBlockers").GetProperty("evaluated").GetInt32());
        Assert.Equal(0, Data(body, "aiProposals").GetProperty("awaitingDecision").GetInt32());
        Assert.Equal(0, Data(body, "testFollowUps").GetProperty("open").GetInt32());
        Assert.Equal(0, Data(body, "board").GetProperty("total").GetInt32());
        Assert.Equal(4, Data(body, "board").GetProperty("columns").GetArrayLength());
        Assert.Equal(0, Data(body, "recentUploads").GetProperty("items").GetArrayLength());
        Assert.Equal(0, Data(body, "publishing").GetProperty("needsAttentionCount").GetInt32());

        var workflows = Data(body, "workflows");
        Assert.Equal(0, workflows.GetProperty("inProgressCount").GetInt32());
        Assert.Equal(0, workflows.GetProperty("needsAttentionCount").GetInt32());
        Assert.Equal(0, workflows.GetProperty("continue").GetArrayLength());
        var plan = Data(body, "workPlan");
        Assert.Equal(0, plan.GetProperty("overdueCount").GetInt32());
        Assert.Equal(0, plan.GetProperty("todayCount").GetInt32());
        Assert.Equal(0, plan.GetProperty("unplannedCount").GetInt32());
        Assert.Equal(0, plan.GetProperty("topThree").GetArrayLength());
        Assert.Equal(0, plan.GetProperty("thisWeek").GetProperty("plannedTaskCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("thisWeek").GetProperty("estimatedMinutes").ValueKind);

        Assert.Equal("America/Chicago", body.GetProperty("timeZoneId").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("today").ValueKind);
    }

    [Fact]
    public async Task Setting_a_zone_is_what_gives_the_dashboard_a_day()
    {
        // Same workspace, before and after the one setting that decides it.
        var before = await ReadAsync(_fixture.WorkspaceA);

        // No zone, so no day: the dashboard says it does not know rather than answering in UTC, which
        // would be wrong by up to a day at either end.
        Assert.Equal(JsonValueKind.Null, before.GetProperty("timeZoneId").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("today").ValueKind);
        Assert.Equal("Unavailable", before.GetProperty("upcoming").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("upcoming").GetProperty("data").ValueKind);

        // The day's work plan is a claim about days too: late, today and this week all need to know which day it is.
        Assert.Equal("Unavailable", before.GetProperty("workPlan").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("workPlan").GetProperty("data").ValueKind);

        // The sections that do not turn on a date answered all along, the guided workflows among them: which
        // run you were in the middle of is true whatever day it is.
        Assert.Equal("Ready", before.GetProperty("recipesInProgress").GetProperty("status").GetString());
        Assert.Equal("Ready", before.GetProperty("board").GetProperty("status").GetString());
        Assert.Equal("Ready", before.GetProperty("workflows").GetProperty("status").GetString());

        await SetZoneAsync(_fixture.WorkspaceA);

        var after = await ReadAsync(_fixture.WorkspaceA);

        Assert.Equal("America/Chicago", after.GetProperty("timeZoneId").GetString());
        Assert.Equal(JsonValueKind.String, after.GetProperty("today").ValueKind);
        Assert.Equal("Ready", after.GetProperty("upcoming").GetProperty("status").GetString());
        Assert.Equal("Ready", after.GetProperty("workPlan").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Recipes_in_progress_count_every_unsettled_recipe_and_list_the_five_changed_last()
    {
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Draft, 3, "Draft");
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Testing, 3, "Testing");
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.ReadyForReview, 1, "Review");
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Approved, 4, "Approved");
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Archived, 2, "Archived");

        var recipes = Data(await ReadAsync(_fixture.WorkspaceA), "recipesInProgress");

        Assert.Equal(3, CountOf(recipes.GetProperty("counts"), "status", "Draft"));
        Assert.Equal(0, CountOf(recipes.GetProperty("counts"), "status", "InDevelopment"));
        Assert.Equal(3, CountOf(recipes.GetProperty("counts"), "status", "Testing"));
        Assert.Equal(1, CountOf(recipes.GetProperty("counts"), "status", "ReadyForReview"));

        // Five, the most recently changed first, and never an approved or archived one.
        var recent = recipes.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(5, recent.Count);
        Assert.Equal("Review 0", recent[0].GetProperty("title").GetString());
        Assert.DoesNotContain(recent, recipe => recipe.GetProperty("title").GetString()!.StartsWith("Approved", StringComparison.Ordinal)
            || recipe.GetProperty("title").GetString()!.StartsWith("Archived", StringComparison.Ordinal));
        Assert.True(recent.Zip(recent.Skip(1)).All(pair =>
            pair.First.GetProperty("updatedAt").GetDateTimeOffset() >= pair.Second.GetProperty("updatedAt").GetDateTimeOffset()));
    }

    [Fact]
    public async Task Readiness_is_evaluated_for_the_listed_recipes_only_and_names_their_blockers()
    {
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Draft, 7, "Draft");

        var body = await ReadAsync(_fixture.WorkspaceA);
        var readiness = Data(body, "readinessBlockers");
        var listed = Data(body, "recipesInProgress").GetProperty("recent").EnumerateArray().Select(recipe => recipe.GetProperty("id").GetGuid()).ToList();

        // Seven in progress, five listed, five evaluated: the other two are not claimed either way.
        Assert.Equal(5, readiness.GetProperty("evaluated").GetInt32());
        Assert.Equal(0, readiness.GetProperty("unevaluated").GetInt32());

        // A recipe with no ingredients and no method cannot be approved: each listed one is blocked, in list order.
        var blocked = readiness.GetProperty("blocked").EnumerateArray().ToList();
        Assert.Equal(listed, blocked.Select(recipe => recipe.GetProperty("recipeId").GetGuid()));
        Assert.All(blocked, recipe =>
        {
            Assert.True(recipe.GetProperty("blockerCount").GetInt32() >= 1);
            var named = recipe.GetProperty("blockers").EnumerateArray().ToList();
            Assert.InRange(named.Count, 1, 3);
            Assert.All(named, blocker => Assert.False(string.IsNullOrWhiteSpace(blocker.GetProperty("summary").GetString())));
        });
    }

    [Fact]
    public async Task Proposals_awaiting_a_decision_are_counted_and_named_by_their_recipe_and_settled_ones_are_not()
    {
        var recipeId = (await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Draft, 1, "Soda bread"))[0];
        await SeedOperationsAsync(_fixture.WorkspaceA, AiOperationStatus.Proposed, 6, recipeId);
        await SeedOperationsAsync(_fixture.WorkspaceA, AiOperationStatus.Accepted, 3, recipeId);
        await SeedOperationsAsync(_fixture.WorkspaceA, AiOperationStatus.Rejected, 2, null);

        var proposals = Data(await ReadAsync(_fixture.WorkspaceA), "aiProposals");

        Assert.Equal(6, proposals.GetProperty("awaitingDecision").GetInt32());
        var recent = proposals.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(5, recent.Count);
        Assert.All(recent, proposal =>
        {
            Assert.Equal(recipeId, proposal.GetProperty("recipeId").GetGuid());
            Assert.Equal("Soda bread 0", proposal.GetProperty("recipeTitle").GetString());
        });
    }

    [Fact]
    public async Task Test_follow_ups_are_the_unresolved_issues_most_severe_first()
    {
        var recipeId = (await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Testing, 1, "Ramen"))[0];
        await SeedIssuesAsync(_fixture.WorkspaceA, recipeId,
        [
            (TestIssueSeverity.Minor, false), (TestIssueSeverity.Minor, false), (TestIssueSeverity.Major, false),
            (TestIssueSeverity.Blocking, false), (TestIssueSeverity.Major, true), (TestIssueSeverity.Blocking, true),
            (TestIssueSeverity.Minor, false), (TestIssueSeverity.Minor, false),
        ]);

        var followUps = Data(await ReadAsync(_fixture.WorkspaceA), "testFollowUps");

        // Six open; the two resolved are not follow-ups.
        Assert.Equal(6, followUps.GetProperty("open").GetInt32());
        Assert.Equal(1, followUps.GetProperty("blocking").GetInt32());
        Assert.Equal(1, followUps.GetProperty("major").GetInt32());
        Assert.Equal(4, followUps.GetProperty("minor").GetInt32());

        var recent = followUps.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(5, recent.Count);
        Assert.Equal(["Blocking", "Major", "Minor", "Minor", "Minor"], recent.Select(issue => issue.GetProperty("severity").GetString()));
        Assert.All(recent, issue => Assert.Equal("Ramen 0", issue.GetProperty("recipeTitle").GetString()));
    }

    [Fact]
    public async Task Upcoming_is_the_next_fourteen_days_of_planned_cards_and_scheduled_posts_and_the_board_counts_every_card()
    {
        await SetZoneAsync(_fixture.WorkspaceA);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cards = await SeedCardsAsync(_fixture.WorkspaceA,
        [
            (ContentCardColumn.ToDo, today.AddDays(-1)), (ContentCardColumn.ToDo, today), (ContentCardColumn.InProcess, today.AddDays(3)),
            (ContentCardColumn.ToDo, today.AddDays(13)), (ContentCardColumn.Approved, today.AddDays(14)), (ContentCardColumn.ToDo, null),
            (ContentCardColumn.ReadyForReview, today.AddDays(1)), (ContentCardColumn.ToDo, today.AddDays(2)), (ContentCardColumn.ToDo, today.AddDays(5)),
        ]);
        var profileId = await SeedProfileAsync(_fixture.WorkspaceA);
        await SeedRecordsAsync(_fixture.WorkspaceA, profileId,
        [
            (PublishingRecordStatus.Scheduled, Now.AddDays(2), cards[2]), (PublishingRecordStatus.Pending, Now.AddHours(2), null),
            (PublishingRecordStatus.Scheduled, Now.AddDays(20), null), (PublishingRecordStatus.Published, Now.AddDays(1), null),
            (PublishingRecordStatus.Cancelled, Now.AddDays(1), null),
        ]);

        var body = await ReadAsync(_fixture.WorkspaceA);
        var upcoming = Data(body, "upcoming");

        Assert.Equal(today, DateOnly.Parse(upcoming.GetProperty("from").GetString()!));
        Assert.Equal(today.AddDays(13), DateOnly.Parse(upcoming.GetProperty("through").GetString()!));

        // Today through the thirteenth day after: not yesterday, not day fourteen, not undated.
        Assert.Equal(6, upcoming.GetProperty("cardCount").GetInt32());
        var listed = upcoming.GetProperty("cards").EnumerateArray().Select(card => DateOnly.Parse(card.GetProperty("plannedDate").GetString()!)).ToList();
        Assert.Equal([today, today.AddDays(1), today.AddDays(2), today.AddDays(3), today.AddDays(5)], listed);

        // Still on their way and inside the window; named by their card where they have one.
        Assert.Equal(2, upcoming.GetProperty("postCount").GetInt32());
        var posts = upcoming.GetProperty("posts").EnumerateArray().ToList();
        Assert.Equal(["Pending", "Scheduled"], posts.Select(post => post.GetProperty("status").GetString()));
        Assert.Equal("Card 2", posts[1].GetProperty("contentCardTitle").GetString());
        Assert.Equal(JsonValueKind.Null, posts[0].GetProperty("contentCardTitle").ValueKind);

        var board = Data(body, "board");
        Assert.Equal(9, board.GetProperty("total").GetInt32());
        Assert.Equal(6, CountOf(board.GetProperty("columns"), "column", "ToDo"));
        Assert.Equal(1, CountOf(board.GetProperty("columns"), "column", "Approved"));
    }

    [Fact]
    public async Task Publishing_counts_every_state_and_lists_what_needs_attention_without_the_posts_words()
    {
        var profileId = await SeedProfileAsync(_fixture.WorkspaceA);
        await SeedRecordsAsync(_fixture.WorkspaceA, profileId,
        [
            .. Enumerable.Repeat((PublishingRecordStatus.Failed, Now.AddDays(-1), (Guid?)null), 4),
            .. Enumerable.Repeat((PublishingRecordStatus.NeedsAttention, Now.AddDays(-2), (Guid?)null), 3),
            (PublishingRecordStatus.Published, Now.AddDays(-3), null),
        ]);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var raw = await (await client.GetAsync(DashboardOf(_fixture.WorkspaceA), Ct)).Content.ReadAsStringAsync(Ct);
        var publishing = Data(JsonDocument.Parse(raw).RootElement, "publishing");

        Assert.Equal(4, CountOf(publishing.GetProperty("counts"), "status", "Failed"));
        Assert.Equal(3, CountOf(publishing.GetProperty("counts"), "status", "NeedsAttention"));
        Assert.Equal(1, CountOf(publishing.GetProperty("counts"), "status", "Published"));
        Assert.Equal(0, CountOf(publishing.GetProperty("counts"), "status", "Scheduled"));
        Assert.Equal(7, publishing.GetProperty("needsAttentionCount").GetInt32());
        Assert.Equal(5, publishing.GetProperty("needsAttention").GetArrayLength());

        // The post's own words, its provider ids and messages never reach the dashboard.
        Assert.DoesNotContain(PostWords, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_uploads_are_the_five_newest_pictures_still_in_the_library_by_name_alone()
    {
        var ids = await SeedAssetsAsync(_fixture.WorkspaceA, 8);
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var newestId = ids[^1];
            var newest = await db.MediaAssets.SingleAsync(asset => asset.Id == newestId, Ct);
            newest.DeletedAt = Now;
            newest.DeletedByMembershipId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var items = Data(await ReadAsync(_fixture.WorkspaceA), "recentUploads").GetProperty("items").EnumerateArray().ToList();

        // The removed one is gone; the next five newest stand in its place.
        Assert.Equal(Enumerable.Reverse(ids[^6..^1]), items.Select(item => item.GetProperty("id").GetGuid()));
        Assert.All(items, item => Assert.Equal(
            ["createdAt", "id", "kind", "title"],
            item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Today_is_the_workspaces_own_day_in_its_brand_profiles_zone()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/brand-profile", new { brandName = "Cozy Fall", timeZoneId = "Pacific/Kiritimati" }, Ct)).StatusCode);

        var body = await ReadAsync(_fixture.WorkspaceA);

        // UTC+14: for ten hours of every UTC day it is already tomorrow there.
        var expected = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(14));
        Assert.Equal("Pacific/Kiritimati", body.GetProperty("timeZoneId").GetString());
        Assert.Equal(expected, DateOnly.Parse(body.GetProperty("today").GetString()!));
        Assert.Equal(expected, DateOnly.Parse(Data(body, "upcoming").GetProperty("from").GetString()!));
    }

    // ---- The caller's own sections (13A.33a) ----

    [Fact]
    public async Task Workflows_name_the_runs_to_carry_on_with_newest_first_and_the_stuck_ones_oldest_first()
    {
        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var someone = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);

        await SeedRunsAsync(_fixture.WorkspaceA,
            Run_("Touched today", mine, Now),
            Run_("Touched yesterday", mine, Now.AddDays(-1)),
            Run_("Touched last week", mine, Now.AddDays(-3)),
            Run_("Untouched for a fortnight", mine, Now.AddDays(-14)),

            // Not started by the caller, but handed to them: theirs to carry on.
            Run_("Handed to me", someone, Now.AddMinutes(-5), assignee: mine),

            // Somebody else's, and so on nobody else's list to continue — but it has stood still, and a run
            // that has stopped is the workspace's business whoever started it.
            Run_("The editor's, and stuck", someone, Now.AddDays(-30)),
            Completed_("Finished last month", mine, Now.AddDays(-30)));

        var workflows = Data(await ReadAsync(_fixture.WorkspaceA), "workflows");

        // Five in progress are the caller's; the editor's is not, and the finished one is in no count here.
        Assert.Equal(5, workflows.GetProperty("inProgressCount").GetInt32());

        var carryOn = workflows.GetProperty("continue").EnumerateArray().ToList();
        Assert.Equal(3, carryOn.Count);
        Assert.Equal(["Touched today", "Handed to me", "Touched yesterday"], carryOn.Select(run => run.GetProperty("title").GetString()));

        // The step the run is on, by name, and that it is the caller's to take: an Owner may do anything a
        // Contributor may. The run itself is the deep link — it resumes on that step.
        Assert.Equal("start", carryOn[0].GetProperty("stepKey").GetString());
        Assert.Equal("Start your recipe", carryOn[0].GetProperty("stepTitle").GetString());
        Assert.True(carryOn[0].GetProperty("youCanDoIt").GetBoolean());
        Assert.Equal("Create a recipe", carryOn[0].GetProperty("goal").GetString());
        Assert.Empty(carryOn[0].GetProperty("attentionReasons").EnumerateArray());

        // Idle past a week, whoever started it, longest-waiting first.
        Assert.Equal(2, workflows.GetProperty("needsAttentionCount").GetInt32());
        var stuck = workflows.GetProperty("needsAttention").EnumerateArray().ToList();
        Assert.Equal(["The editor's, and stuck", "Untouched for a fortnight"], stuck.Select(run => run.GetProperty("title").GetString()));
        Assert.All(stuck, run => Assert.Equal(["idle"], run.GetProperty("attentionReasons").EnumerateArray().Select(reason => reason.GetString())));

        // No draft, no reason, no linked record and no route: a run's contents are the run page's.
        Assert.Equal(
            ["attentionReasons", "definitionKey", "goal", "id", "stepKey", "stepTitle", "title", "updatedAt", "youCanDoIt"],
            carryOn[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_run_on_a_step_above_the_readers_role_is_not_theirs_to_do_and_needs_a_look()
    {
        // Workspace B's member is a Viewer, and starting a recipe takes a Contributor.
        var viewer = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Viewer);
        await SeedRunsAsync(_fixture.WorkspaceB, Run_("Someone started this for me", viewer, Now));

        var workflows = Data(await ReadAsync(_fixture.WorkspaceB, _fixture.WorkspaceB.MemberEmail), "workflows");
        var run = workflows.GetProperty("continue").EnumerateArray().Single();

        Assert.Equal(1, workflows.GetProperty("inProgressCount").GetInt32());
        Assert.False(run.GetProperty("youCanDoIt").GetBoolean());
        Assert.Equal(["not_yours_to_do"], run.GetProperty("attentionReasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal(1, workflows.GetProperty("needsAttentionCount").GetInt32());

        // The same row, read by the Owner: not theirs to carry on — it is the Viewer's — and because an Owner
        // may take that step, nothing about it needs a look. Whose it is and what needs attention are both the
        // reader's question, exactly as they are in the hub's lists (13A.11).
        var asOwner = Data(await ReadAsync(_fixture.WorkspaceB), "workflows");
        Assert.Equal(0, asOwner.GetProperty("inProgressCount").GetInt32());
        Assert.Equal(0, asOwner.GetProperty("continue").GetArrayLength());
        Assert.Equal(0, asOwner.GetProperty("needsAttentionCount").GetInt32());
    }

    [Fact]
    public async Task The_work_plan_is_the_callers_own_day_and_week_and_its_day_groups_do_not_overlap()
    {
        await SetZoneAsync(_fixture.WorkspaceA);

        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var today = TodayInChicago();
        var weekStart = WorkTaskWeek.StartOf(today);

        await SeedTasksAsync(_fixture.WorkspaceA,
            Task_("Late by two days", mine, WorkTaskStatus.Backlog, due: today.AddDays(-2)),
            Task_("Late by one day, and started", mine, WorkTaskStatus.InProgress, due: today.AddDays(-1)),

            // Planned for today and already late: late is what presses, so it is counted there and nowhere else.
            Task_("Planned today but late", mine, WorkTaskStatus.Backlog, scheduled: today, due: today.AddDays(-3)),
            Task_("Shoot the galette", mine, WorkTaskStatus.Backlog, scheduled: today, estimate: 30),
            Task_("Write the headnote", mine, WorkTaskStatus.Backlog, scheduled: today, estimate: 45),
            Task_("Sometime this week", mine, WorkTaskStatus.Backlog, week: weekStart),
            Task_("Next week", mine, WorkTaskStatus.Backlog, week: weekStart.AddDays(7)),
            Task_("Captured, not planned", mine, WorkTaskStatus.Backlog),
            Task_("Under way", mine, WorkTaskStatus.InProgress),
            Task_("Held up", mine, WorkTaskStatus.Waiting),
            Done_("Finished this morning", mine, Now.AddHours(-1)),
            Done_("Finished a fortnight ago", mine, Now.AddDays(-14)),

            // Another member's work, in the same workspace.
            Task_("The editor's own", editor, WorkTaskStatus.Backlog, scheduled: today));

        var body = await ReadAsync(_fixture.WorkspaceA);
        var plan = Data(body, "workPlan");

        Assert.Equal(body.GetProperty("today").GetString(), plan.GetProperty("date").GetString());
        Assert.Equal("America/Chicago", plan.GetProperty("timeZoneId").GetString());

        // Late first, soonest due at the top, and the three groups do not share a task.
        Assert.Equal(3, plan.GetProperty("overdueCount").GetInt32());
        Assert.Equal(
            ["Planned today but late", "Late by two days", "Late by one day, and started"],
            Titles(plan, "overdue"));

        Assert.Equal(2, plan.GetProperty("todayCount").GetInt32());
        Assert.Equal(["Shoot the galette", "Write the headnote"], Titles(plan, "today"));

        // The precedence hides nothing: a row still says what it is.
        Assert.Equal("InProgress", plan.GetProperty("overdue")[2].GetProperty("status").GetString());

        // This week's own planned work: the three planned for today and the one planned for the week with no day.
        var week = plan.GetProperty("thisWeek");
        Assert.Equal(weekStart, DateOnly.Parse(week.GetProperty("weekStart").GetString()!));
        Assert.Equal(weekStart.AddDays(6), DateOnly.Parse(week.GetProperty("weekEnd").GetString()!));
        Assert.Equal(4, week.GetProperty("plannedTaskCount").GetInt32());
        Assert.Equal(2, week.GetProperty("estimatedTaskCount").GetInt32());
        Assert.Equal(75, week.GetProperty("estimatedMinutes").GetInt64());
        Assert.Equal(2, week.GetProperty("inProgressCount").GetInt32());
        Assert.Equal(1, week.GetProperty("waitingCount").GetInt32());
        Assert.Equal(1, week.GetProperty("completedCount").GetInt32());

        // Captured and planned for no week at all — the task nobody has planned, and the late one that was
        // never planned either. This is the week's frame, not the day's: it is My Week's Unplanned column, and
        // a task can be both late and unplanned, which is why planning it is the thing to do.
        Assert.Equal(2, plan.GetProperty("unplannedCount").GetInt32());

        // Nobody else's work is anywhere in it.
        Assert.DoesNotContain("The editor's own", JsonSerializer.Serialize(plan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_week_with_nothing_estimated_reports_no_minutes_rather_than_none()
    {
        await SetZoneAsync(_fixture.WorkspaceA);

        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        await SeedTasksAsync(_fixture.WorkspaceA, Task_("No idea how long", mine, WorkTaskStatus.Backlog, scheduled: TodayInChicago()));

        var week = Data(await ReadAsync(_fixture.WorkspaceA), "workPlan").GetProperty("thisWeek");

        // Zero would claim the work takes no time.
        Assert.Equal(1, week.GetProperty("plannedTaskCount").GetInt32());
        Assert.Equal(0, week.GetProperty("estimatedTaskCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, week.GetProperty("estimatedMinutes").ValueKind);
    }

    [Fact]
    public async Task The_top_three_names_only_the_tasks_the_dashboard_is_showing()
    {
        await SetZoneAsync(_fixture.WorkspaceA);

        var mine = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var today = TodayInChicago();
        var shown = Task_("Shoot the galette", mine, WorkTaskStatus.Backlog, scheduled: today);
        var late = Task_("Answer the brand email", mine, WorkTaskStatus.Backlog, due: today.AddDays(-1));
        var elsewhere = Task_("Under way, and not on show", mine, WorkTaskStatus.InProgress);
        await SeedTasksAsync(_fixture.WorkspaceA, shown, late, elsewhere);

        // Chosen in this order; the third is in no group the dashboard lists.
        await SeedTopThreeAsync(_fixture.WorkspaceA, mine, today, late.Id, shown.Id, elsewhere.Id);

        var plan = Data(await ReadAsync(_fixture.WorkspaceA), "workPlan");

        Assert.Equal([late.Id, shown.Id], plan.GetProperty("topThree").EnumerateArray().Select(id => id.GetGuid()));
    }

    [Fact]
    public async Task Two_members_of_one_workspace_are_answered_with_their_own_runs_and_their_own_plan()
    {
        await SetZoneAsync(_fixture.WorkspaceA);

        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var today = TodayInChicago();

        await SeedRunsAsync(_fixture.WorkspaceA, Run_("The owner's run", owner, Now), Run_("The editor's run", editor, Now));
        await SeedTasksAsync(_fixture.WorkspaceA,
            Task_("The owner's task", owner, WorkTaskStatus.Backlog, scheduled: today),
            Task_("The editor's task", editor, WorkTaskStatus.Backlog, scheduled: today));
        await SeedCardsAsync(_fixture.WorkspaceA, [(ContentCardColumn.ToDo, today)]);

        var asOwner = await ReadAsync(_fixture.WorkspaceA);
        var asEditor = await ReadAsync(_fixture.WorkspaceA, _fixture.WorkspaceA.MemberEmail);

        Assert.Equal(["The owner's run"], Titles(Data(asOwner, "workflows"), "continue"));
        Assert.Equal(["The editor's run"], Titles(Data(asEditor, "workflows"), "continue"));
        Assert.Equal(["The owner's task"], Titles(Data(asOwner, "workPlan"), "today"));
        Assert.Equal(["The editor's task"], Titles(Data(asEditor, "workPlan"), "today"));

        // And the workspace's own sections read the same for both: only these two are personal.
        Assert.Equal(
            Data(asOwner, "board").GetProperty("total").GetInt32(),
            Data(asEditor, "board").GetProperty("total").GetInt32());
        Assert.Equal(
            Data(asOwner, "upcoming").GetProperty("cardCount").GetInt32(),
            Data(asEditor, "upcoming").GetProperty("cardCount").GetInt32());
    }

    [Fact]
    public async Task A_run_with_every_step_done_is_the_readers_own_to_finish()
    {
        await SetZoneAsync(_fixture.WorkspaceA);

        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        await SeedRunsAsync(_fixture.WorkspaceA, Finishing_("Waiting to be finished", owner, Now));

        var run = Data(await ReadAsync(_fixture.WorkspaceA), "workflows").GetProperty("continue").EnumerateArray().Single();

        // No step is left, so none is named. The run is still the owner's to finish, and answering otherwise put
        // a "Needs someone else" warning next to "Next: Finish it" on a run the reader could finish themselves.
        Assert.Equal(JsonValueKind.Null, run.GetProperty("stepKey").ValueKind);
        Assert.True(run.GetProperty("youCanDoIt").GetBoolean());
    }

    [Fact]
    public async Task A_workspace_with_no_zone_says_so_and_one_whose_zone_could_not_be_read_says_only_that()
    {
        // Two answers that used to be one. "Not set" is the creator's to fix and the page says where; a brand
        // profile that would not read says nothing about whether a zone is set, and telling that creator to go
        // and set one sends them to a page where it already is.
        var unset = await ReadAsync(_fixture.WorkspaceA);
        Assert.Equal("NotSet", unset.GetProperty("timeZoneState").GetString());
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("today").ValueKind);

        await SetZoneAsync(_fixture.WorkspaceA);
        Assert.Equal("Set", (await ReadAsync(_fixture.WorkspaceA)).GetProperty("timeZoneState").GetString());

        _brand.Broken = true;
        try
        {
            var unreadable = await ReadAsync(_fixture.WorkspaceA);

            Assert.Equal("Unreadable", unreadable.GetProperty("timeZoneState").GetString());
            Assert.Equal(JsonValueKind.Null, unreadable.GetProperty("today").ValueKind);
            Assert.Equal("Unavailable", unreadable.GetProperty("workPlan").GetProperty("status").GetString());

            // And the rest of the page is unaffected: one unreadable fact is not a broken dashboard.
            Assert.Equal("Ready", unreadable.GetProperty("workflows").GetProperty("status").GetString());
        }
        finally
        {
            _brand.Broken = false;
        }
    }

    [Fact]
    public async Task A_day_without_the_week_holding_it_is_refused_by_the_database()
    {
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);
        var stray = Task_("Scheduled but never planned", owner, WorkTaskStatus.Backlog, scheduled: TodayInChicago());
        stray.PlanningWeekStart = null;

        // Why the plan can count "planned this week" and "waiting to be planned" as separate groups without
        // either ever claiming the other's task: CK_WorkTasks_ScheduledDate_InWeek makes a day without its week
        // impossible, so "no week at all" also means "on no day", and the two cannot both be true. The counting
        // read asks only about the week, and leans on exactly this.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => SeedTasksAsync(_fixture.WorkspaceA, stray));
    }

    // ---- One section at a time ----

    [Fact]
    public async Task A_section_whose_source_fails_is_unavailable_and_every_other_section_still_answers()
    {
        await SeedAssetsAsync(_fixture.WorkspaceA, 2);
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Draft, 2, "Draft");
        _library.Broken = true;

        var body = await ReadAsync(_fixture.WorkspaceA);

        Assert.Equal("Unavailable", body.GetProperty("recentUploads").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("recentUploads").GetProperty("data").ValueKind);
        Assert.Equal(2, CountOf(Data(body, "recipesInProgress").GetProperty("counts"), "status", "Draft"));
        Assert.Equal("Ready", body.GetProperty("board").GetProperty("status").GetString());
        Assert.Equal("Ready", body.GetProperty("publishing").GetProperty("status").GetString());

        // What failed is logged by section and type, and not by anything it held.
        Assert.Contains(_fixture.Api.Logs.Entries, entry => entry.Contains("Dashboard section uploads could not be read", StringComparison.Ordinal));
    }

    // ---- Performance (PERF-001) ----

    [Fact]
    public async Task Every_list_stays_at_five_and_the_request_runs_the_same_statements_however_much_the_workspace_holds()
    {
        await SetZoneAsync(_fixture.WorkspaceA);
        await SeedEverythingAsync(_fixture.WorkspaceA, scale: 1);
        await ReadAsync(_fixture.WorkspaceA);

        _commands.Reset();
        var small = await ReadAsync(_fixture.WorkspaceA);
        var smallCount = _commands.Count;

        await SeedEverythingAsync(_fixture.WorkspaceA, scale: 12);
        _commands.Reset();
        var large = await ReadAsync(_fixture.WorkspaceA);
        var largeCount = _commands.Count;

        Assert.Equal(smallCount, largeCount);

        // Recipes 5, readiness at most 5 evaluations, everything else a handful each: well inside this.
        Assert.InRange(largeCount, 1, 120);

        foreach (var (section, list) in new[]
                 {
                     ("recipesInProgress", "recent"), ("aiProposals", "recent"), ("testFollowUps", "recent"),
                     ("upcoming", "cards"), ("upcoming", "posts"), ("recentUploads", "items"), ("publishing", "needsAttention"),
                 })
        {
            Assert.InRange(Data(large, section).GetProperty(list).GetArrayLength(), 0, 5);
            Assert.Equal(5, Data(large, section).GetProperty(list).GetArrayLength());
        }

        // The caller's own sections hold three, not five: the dashboard answers "what next".
        foreach (var (section, list) in new[]
                 {
                     ("workflows", "continue"), ("workflows", "needsAttention"), ("workPlan", "overdue"), ("workPlan", "today"),
                 })
        {
            Assert.Equal(3, Data(large, section).GetProperty(list).GetArrayLength());
        }

        // Nothing in the answer grew with the data but the counts.
        Assert.True(Data(large, "board").GetProperty("total").GetInt32() > Data(small, "board").GetProperty("total").GetInt32());

        // The uploads list reads the asset rows alone: no statement over the library joins its stored versions.
        Assert.DoesNotContain(_commands.Texts, text => text.Contains("MediaAssetVersions", StringComparison.OrdinalIgnoreCase)
            && text.Contains("\"CreatedAt\" DESC", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task One_workspaces_dashboard_holds_only_its_own_and_another_workspace_is_refused_as_unknown()
    {
        await SetZoneAsync(_fixture.WorkspaceA);
        await SetZoneAsync(_fixture.WorkspaceB);
        await SeedEverythingAsync(_fixture.WorkspaceB, scale: 2);
        await SeedRecipesAsync(_fixture.WorkspaceA, RecipeStatus.Draft, 1, "Only A");

        var a = await ReadAsync(_fixture.WorkspaceA);

        Assert.Equal(1, CountOf(Data(a, "recipesInProgress").GetProperty("counts"), "status", "Draft"));
        Assert.Equal(["Only A 0"], Data(a, "recipesInProgress").GetProperty("recent").EnumerateArray().Select(recipe => recipe.GetProperty("title").GetString()));
        Assert.Equal(0, Data(a, "aiProposals").GetProperty("awaitingDecision").GetInt32());
        Assert.Equal(0, Data(a, "testFollowUps").GetProperty("open").GetInt32());
        Assert.Equal(0, Data(a, "board").GetProperty("total").GetInt32());
        Assert.Equal(0, Data(a, "upcoming").GetProperty("cardCount").GetInt32());
        Assert.Equal(0, Data(a, "upcoming").GetProperty("postCount").GetInt32());
        Assert.Equal(0, Data(a, "recentUploads").GetProperty("items").GetArrayLength());
        Assert.All(Data(a, "publishing").GetProperty("counts").EnumerateArray(), entry => Assert.Equal(0, entry.GetProperty("count").GetInt32()));
        Assert.Equal(0, Data(a, "workflows").GetProperty("inProgressCount").GetInt32());
        Assert.Equal(0, Data(a, "workflows").GetProperty("needsAttentionCount").GetInt32());
        Assert.Equal(0, Data(a, "workPlan").GetProperty("overdueCount").GetInt32());
        Assert.Equal(0, Data(a, "workPlan").GetProperty("thisWeek").GetProperty("plannedTaskCount").GetInt32());

        // B's own dashboard has B's.
        Assert.True(Data(await ReadAsync(_fixture.WorkspaceB), "board").GetProperty("total").GetInt32() > 0);

        // A's owner on B's route, and on a route for no workspace: the same refusal.
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var foreign = await inA.GetAsync(DashboardOf(_fixture.WorkspaceB), Ct);
        var unknown = await inA.GetAsync("/api/v1/workspaces/no-such-workspace/dashboard", Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(
            (await foreign.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString(),
            (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task With_both_workspaces_busy_nothing_named_in_one_appears_anywhere_in_the_others()
    {
        // Both zoned, so every section answers — the day's work plan included.
        await SetZoneAsync(_fixture.WorkspaceA);
        await SetZoneAsync(_fixture.WorkspaceB);
        await SeedEverythingAsync(_fixture.WorkspaceA, scale: 1);
        await SeedEverythingAsync(_fixture.WorkspaceB, scale: 2);

        // Everything B holds that has a name, renamed so it can be recognised wherever it turns up.
        await InWorkspaceAsync(_fixture.WorkspaceB, async db =>
        {
            await db.Recipes.ForEachAsync(recipe => recipe.Title = $"B_ONLY recipe {recipe.Id:N}", Ct);
            await db.ContentCards.ForEachAsync(card => card.Title = $"B_ONLY card {card.Id:N}", Ct);
            await db.MediaAssets.ForEachAsync(asset => asset.Title = $"B_ONLY picture {asset.Id:N}", Ct);
            await db.TestIssues.ForEachAsync(issue => issue.Title = $"B_ONLY issue {issue.Id:N}", Ct);
            await db.WorkflowRuns.ForEachAsync(run => run.Title = $"B_ONLY run {run.Id:N}", Ct);
            await db.WorkTasks.ForEachAsync(task => task.Title = $"B_ONLY task {task.Id:N}", Ct);
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var bIds = await InWorkspaceAsync(_fixture.WorkspaceB, async db => new HashSet<string>(
            (await db.Recipes.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.ContentCards.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.MediaAssets.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.AiOperations.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.PublishingRecords.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.WorkflowRuns.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.WorkTasks.Select(item => item.Id).ToListAsync(Ct))
                .Select(id => id.ToString("D"))));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var raw = await (await client.GetAsync(DashboardOf(_fixture.WorkspaceA), Ct)).Content.ReadAsStringAsync(Ct);

        // Not a name, and not an id: A's dashboard is A's, every section of it, readiness included.
        Assert.DoesNotContain("B_ONLY", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(bIds, id => raw.Contains(id, StringComparison.OrdinalIgnoreCase));

        // And A's own lists are full: the absence of B is not an empty dashboard.
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(5, Data(body, "recipesInProgress").GetProperty("recent").GetArrayLength());
        Assert.Equal(5, Data(body, "readinessBlockers").GetProperty("evaluated").GetInt32());
        Assert.Equal(5, Data(body, "recentUploads").GetProperty("items").GetArrayLength());
        Assert.Equal(5, Data(body, "publishing").GetProperty("needsAttention").GetArrayLength());
        Assert.Equal(3, Data(body, "workflows").GetProperty("continue").GetArrayLength());
        Assert.Equal(3, Data(body, "workPlan").GetProperty("overdue").GetArrayLength());
    }

    [Fact]
    public async Task A_viewer_may_read_it_and_nobody_signed_out_may()
    {
        Assert.Equal(HttpStatusCode.OK, (await (await SignInAsync(_fixture.WorkspaceB.MemberEmail)).GetAsync(DashboardOf(_fixture.WorkspaceB), Ct)).StatusCode);

        using var anonymous = _fixture.Gateway.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(DashboardOf(_fixture.WorkspaceB), Ct)).StatusCode);
    }

    // ---- Seeding ----

    private const string PostWords = "POST_WORDS_NEVER_ON_THE_DASHBOARD";

    private int _recipeClock;

    private static int _taskRank;

    private async Task SeedEverythingAsync(SeededWorkspace workspace, int scale)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var mine = await MembershipAsync(workspace, WorkspaceRole.Owner);
        var localToday = TodayInChicago();
        var recipeIds = await SeedRecipesAsync(workspace, RecipeStatus.Draft, 6 * scale, $"Scale {scale}");
        await SeedOperationsAsync(workspace, AiOperationStatus.Proposed, 6 * scale, recipeIds[0]);
        await SeedIssuesAsync(workspace, recipeIds[0], [.. Enumerable.Repeat((TestIssueSeverity.Minor, false), 6 * scale)]);
        var cards = await SeedCardsAsync(workspace, [.. Enumerable.Range(0, 6 * scale).Select(index => (ContentCardColumn.ToDo, (DateOnly?)today.AddDays(index % 14)))]);
        await SeedAssetsAsync(workspace, 6 * scale);
        var profileId = await SeedProfileAsync(workspace);
        await SeedRecordsAsync(workspace, profileId,
        [
            .. Enumerable.Range(0, 6 * scale).Select(index => (PublishingRecordStatus.Scheduled, Now.AddHours(index + 1), (Guid?)cards[index % cards.Count])),
            .. Enumerable.Repeat((PublishingRecordStatus.Failed, Now.AddDays(-1), (Guid?)null), 6 * scale),
        ]);

        // The caller's own two sections: runs to carry on with, runs that have stood still, work that is late
        // and work planned for today.
        await SeedRunsAsync(workspace,
        [
            .. Enumerable.Range(0, 4 * scale).Select(index => Run_($"Run {index}", mine, Now.AddMinutes(-index))),
            .. Enumerable.Range(0, 4 * scale).Select(index => Run_($"Stuck run {index}", mine, Now.AddDays(-14 - index))),
        ]);
        await SeedTasksAsync(workspace,
        [
            .. Enumerable.Range(0, 4 * scale).Select(index => Task_($"Late {index}", mine, WorkTaskStatus.Backlog, due: localToday.AddDays(-1 - index))),
            .. Enumerable.Range(0, 4 * scale).Select(index =>
                Task_($"Today {index}", mine, WorkTaskStatus.Backlog, scheduled: localToday, estimate: 30)),
        ]);
    }

    /// <summary>
    /// Today where <see cref="SetZoneAsync"/> puts a workspace, which is not the server's own date at every
    /// hour: a task planned for "today" has to mean the day the dashboard will read.
    /// </summary>
    private static DateOnly TodayInChicago() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")).DateTime);

    private static IReadOnlyList<string> Titles(JsonElement section, string group) =>
        [.. section.GetProperty(group).EnumerateArray().Select(item => item.GetProperty("title").GetString()!)];

    /// <summary>The membership in that role — which is what "the caller's own runs and tasks" is keyed on.</summary>
    private Task<Guid> MembershipAsync(SeededWorkspace workspace, WorkspaceRole role) =>
        InWorkspaceAsync(workspace, db => db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.WorkspaceId == workspace.Id && membership.Role == role)
            .Select(membership => membership.Id)
            .SingleAsync(Ct));

    private Task SeedRunsAsync(SeededWorkspace workspace, params WorkflowRun[] runs) =>
        InWorkspaceAsync(workspace, async db =>
        {
            db.WorkflowRuns.AddRange(runs);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    /// <summary>
    /// A run in progress of the shipped <c>recipe.create</c> v1, on its first step. Pinned to a real
    /// definition because the dashboard reads the goal, the step's title and who may take it from the
    /// catalogue rather than from the row.
    /// </summary>
    private static WorkflowRun Run_(string title, Guid owner, DateTimeOffset updatedAt, Guid? assignee = null) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionKey = WorkflowCatalogue.RecipeCreateKey,
        DefinitionVersion = 1,
        Title = title,
        Status = WorkflowRunStatus.InProgress,
        CurrentStepKey = "start",
        StepCount = 5,
        CreatedByMembershipId = owner,
        AssignedMembershipId = assignee,
        StartedAt = updatedAt,
        UpdatedAt = updatedAt,
    };

    /// <summary>
    /// A run in progress with every step done, so it waits only to be finished. No step is left to name, which
    /// is not the same as waiting on somebody else.
    /// </summary>
    private static WorkflowRun Finishing_(string title, Guid owner, DateTimeOffset at)
    {
        var run = Run_(title, owner, at);
        run.CompletedStepCount = run.StepCount;

        return run;
    }

    /// <summary>A finished run, with the two facts its check constraints insist travel together.</summary>
    private static WorkflowRun Completed_(string title, Guid owner, DateTimeOffset at)
    {
        var run = Run_(title, owner, at);
        run.Status = WorkflowRunStatus.Completed;
        run.CurrentStepKey = null;
        run.CompletedStepCount = run.StepCount;
        run.CompletedAt = at;

        return run;
    }

    private Task SeedTasksAsync(SeededWorkspace workspace, params WorkTask[] tasks) =>
        InWorkspaceAsync(workspace, async db =>
        {
            db.WorkTasks.AddRange(tasks);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    /// <summary>
    /// One person's task. A planned day carries the week holding it, as the write seam insists, and every task
    /// gets the next place in its list, so the order a test seeds them in is the order a group reads.
    /// </summary>
    private static WorkTask Task_(
        string title,
        Guid assignee,
        WorkTaskStatus status,
        DateOnly? scheduled = null,
        DateOnly? due = null,
        DateOnly? week = null,
        int? estimate = null) => new()
    {
        Id = Guid.NewGuid(),
        OwnerMembershipId = assignee,
        AssigneeMembershipId = assignee,
        Title = title,
        Status = status,
        Priority = WorkTaskPriority.Normal,
        SortRank = Interlocked.Increment(ref _taskRank) * WorkTaskPolicy.RankGap,
        ScheduledDate = scheduled,
        PlanningWeekStart = scheduled is { } day ? WorkTaskWeek.StartOf(day) : week,
        DueDate = due,
        EstimateMinutes = estimate,
        CreatedAt = Now,
        UpdatedAt = Now,
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

    /// <summary>The caller's chosen top three for one day, in the order given (13A.32).</summary>
    private Task SeedTopThreeAsync(SeededWorkspace workspace, Guid assignee, DateOnly date, params Guid[] taskIds) =>
        InWorkspaceAsync(workspace, async db =>
        {
            for (var index = 0; index < taskIds.Length; index++)
            {
                db.Add(new WorkTaskFocus
                {
                    AssigneeMembershipId = assignee, FocusDate = date, Position = index + 1, WorkTaskId = taskIds[index],
                });
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private Task<List<Guid>> SeedRecipesAsync(SeededWorkspace workspace, RecipeStatus status, int count, string prefix) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var ids = new List<Guid>();
            for (var index = 0; index < count; index++)
            {
                var author = Guid.NewGuid();

                // Strictly newer than everything seeded before it, in this call or an earlier one.
                var at = Now.AddSeconds(Interlocked.Increment(ref _recipeClock));
                var recipe = new Recipe
                {
                    Id = Guid.NewGuid(), Title = $"{prefix} {index}", Status = status,
                    CreatedByMembershipId = author, UpdatedByMembershipId = author, CreatedAt = at, UpdatedAt = at,
                };
                db.Add(recipe);
                db.Add(new RecipeVersion
                {
                    Id = Guid.NewGuid(), RecipeId = recipe.Id, VersionNumber = 1, CreatedByMembershipId = author, CreatedAt = at,
                });
                ids.Add(recipe.Id);
            }

            await db.SaveChangesAsync(Ct);

            // The first one listed is the last one seeded; the caller's "prefix 0" is the one it names.
            ids.Reverse();
            for (var index = 0; index < ids.Count; index++)
            {
                var recipe = await db.Recipes.SingleAsync(item => item.Id == ids[index], Ct);
                recipe.Title = $"{prefix} {index}";
            }

            await db.SaveChangesAsync(Ct);

            return ids;
        });

    private Task SeedOperationsAsync(SeededWorkspace workspace, AiOperationStatus status, int count, Guid? recipeId) =>
        InWorkspaceAsync(workspace, async db =>
        {
            for (var index = 0; index < count; index++)
            {
                var id = Guid.NewGuid();
                db.AiOperations.Add(new AiOperation
                {
                    Id = id, TaskType = AiTaskType.RecipeConcepts, Scope = AiOperationScope.NotApplicable, Status = status,
                    RecipeId = recipeId, IdempotencyKey = $"dash-{id}", RequestedByMembershipId = Guid.NewGuid(),
                    RequestedAt = Now, StatusChangedAt = Now.AddSeconds(index), AvailableAt = Now,

                    // A settled request records when it settled; one awaiting a decision has not.
                    CompletedAt = status is AiOperationStatus.Proposed ? null : Now.AddSeconds(index),
                });
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private Task SeedIssuesAsync(SeededWorkspace workspace, Guid recipeId, IReadOnlyList<(TestIssueSeverity Severity, bool Resolved)> issues) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var versionId = await db.RecipeVersions.Where(version => version.RecipeId == recipeId).Select(version => version.Id).FirstAsync(Ct);
            var author = Guid.NewGuid();
            var run = new RecipeTestRun
            {
                Id = Guid.NewGuid(), RecipeId = recipeId, RecipeVersionId = versionId, TestedAt = Now, TestedByMembershipId = author,
                Outcome = TestRunOutcome.NotStated, CreatedByMembershipId = author, UpdatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now,
            };
            var resolutions = new List<TestIssueResolution>();
            for (var index = 0; index < issues.Count; index++)
            {
                var issue = new TestIssue
                {
                    Id = Guid.NewGuid(), RecipeId = recipeId, RecipeTestRunId = run.Id, Severity = issues[index].Severity,
                    Title = $"Issue {index}", SortOrder = index,
                };
                run.Issues.Add(issue);
                if (issues[index].Resolved)
                {
                    resolutions.Add(new TestIssueResolution
                    {
                        Id = Guid.NewGuid(), RecipeId = recipeId, TestIssueId = issue.Id, Kind = TestIssueResolutionKind.WontFix,
                        ResolvedByMembershipId = author, ResolvedAt = Now,
                    });
                }
            }

            db.RecipeTestRuns.Add(run);
            await db.SaveChangesAsync(Ct);
            db.TestIssueResolutions.AddRange(resolutions);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private Task<List<Guid>> SeedCardsAsync(SeededWorkspace workspace, IReadOnlyList<(ContentCardColumn Column, DateOnly? Planned)> cards) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var ids = new List<Guid>();
            var author = Guid.NewGuid();
            for (var index = 0; index < cards.Count; index++)
            {
                var card = new ContentCard
                {
                    Id = Guid.NewGuid(), Title = $"Card {index}", Column = cards[index].Column, SortRank = (index + 1) * ContentCardPolicy.RankGap,
                    PlannedDate = cards[index].Planned, PlannedDayOfWeek = cards[index].Planned?.DayOfWeek,
                    CreatedByMembershipId = author, UpdatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now,
                };
                db.Add(card);
                db.Add(new CardMoveHistory
                {
                    Id = Guid.NewGuid(), ContentCardId = card.Id, Sequence = 1, ToColumn = card.Column, ActorMembershipId = author, OccurredAt = Now,
                });
                ids.Add(card.Id);
            }

            await db.SaveChangesAsync(Ct);

            return ids;
        });

    private Task<List<Guid>> SeedAssetsAsync(SeededWorkspace workspace, int count) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var ids = new List<Guid>();
            for (var index = 0; index < count; index++)
            {
                ids.Add(await CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now.AddSeconds(index), Ct));
            }

            return ids;
        });

    private Task<Guid> SeedProfileAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var profile = new PublishingProfile
            {
                Id = Guid.NewGuid(), ProviderKey = PublishingPolicy.BufferProviderKey, Platform = PublishingPlatform.Instagram,
                DisplayName = "The test kitchen", TimeZoneId = "America/Chicago", Status = PublishingProfileStatus.Ready,
                FirstSeenAt = Now, LastSeenAt = Now,
            };
            db.PublishingProfiles.Add(profile);
            await db.SaveChangesAsync(Ct);

            return profile.Id;
        });

    private Task SeedRecordsAsync(
        SeededWorkspace workspace, Guid profileId, IReadOnlyList<(PublishingRecordStatus Status, DateTimeOffset ScheduledAt, Guid? CardId)> records) =>
        InWorkspaceAsync(workspace, async db =>
        {
            for (var index = 0; index < records.Count; index++)
            {
                var (status, scheduledAt, cardId) = records[index];
                db.PublishingRecords.Add(new PublishingRecord
                {
                    Id = Guid.NewGuid(), ContentCardId = cardId, PublishingProfileId = profileId, Platform = PublishingPlatform.Instagram,
                    ContentText = PostWords, ScheduledAt = scheduledAt, ScheduleTimeZoneId = "America/Chicago",
                    ScheduledLocal = scheduledAt.UtcDateTime.AddHours(-5), Status = status, StatusChangedAt = Now.AddSeconds(index),
                    AvailableAt = Now, ConfirmedByMembershipId = Guid.NewGuid(), CreatedAt = Now, IdempotencyKey = $"dash-{Guid.NewGuid():N}",
                    FailureCategory = status is PublishingRecordStatus.Failed or PublishingRecordStatus.NeedsAttention ? "provider_refused" : null,
                });
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    // ---- Doubles ----

    /// <summary>Counts every SQL statement the API host runs while it is listening.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public int Count => _texts.Count;

        public IReadOnlyCollection<string> Texts => _texts;

        public void Reset() => _texts.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);

            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FailingLibrary
    {
        public volatile bool Broken;
    }

    /// <summary>The real library facade, unless the test has broken it.</summary>
    private sealed class SwitchableLibrary(IMediaDashboardFacade real, FailingLibrary state) : IMediaDashboardFacade
    {
        public Task<IReadOnlyList<Domain.Modules.Media.Managers.RecentMediaUploadServiceModel>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
            state.Broken ? throw new InvalidOperationException("The library is down.") : real.ListRecentAsync(limit, cancellationToken);
    }

    /// <summary>The real brand profile facade, unless the test has broken the one read the dashboard makes.</summary>
    private sealed class SwitchableBrand(IBrandProfileFacade real, FailingLibrary state) : IBrandProfileFacade
    {
        public Task<OperationResult<Domain.Modules.Brand.Managers.BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken) =>
            state.Broken ? throw new InvalidOperationException("The brand profile is down.") : real.GetAsync(cancellationToken);

        public Task<IdempotentOutcome<Domain.Modules.Brand.Managers.BrandProfileServiceModel>> CreateAsync(
            string userId,
            Domain.Modules.Brand.Managers.CreateBrandProfileViewModel model,
            string? idempotencyKey,
            CancellationToken cancellationToken) =>
            real.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        public Task<IdempotentOutcome<Domain.Modules.Brand.Managers.BrandProfileServiceModel>> UpdateAsync(
            string userId,
            Domain.Modules.Brand.Managers.UpdateBrandProfileViewModel model,
            string? idempotencyKey,
            CancellationToken cancellationToken) =>
            real.UpdateAsync(userId, model, idempotencyKey, cancellationToken);
    }
}
