using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Dashboard;

/// <summary>
/// <c>GET .../dashboard</c> through the real Gateway (13.11, HOME-UI-001/002, PERF-001): what each section
/// counts and leaves out, that every list stays at five and the request's statements stay fixed however much
/// the workspace holds, that one failing section is only that section, and the boundary between workspaces.
/// </summary>
public sealed class DashboardEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly CommandCounter _commands = new();
    private readonly FailingLibrary _library = new();
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

    private static int CountOf(JsonElement counts, string key, string name) =>
        counts.EnumerateArray().Single(entry => entry.GetProperty(key).GetString() == name).GetProperty("count").GetInt32();

    // ---- The sections ----

    [Fact]
    public async Task An_empty_workspace_answers_every_section_with_zeros_and_empty_lists()
    {
        var body = await ReadAsync(_fixture.WorkspaceA);

        foreach (var section in new[] { "recipesInProgress", "readinessBlockers", "aiProposals", "testFollowUps", "upcoming", "board", "recentUploads", "publishing" })
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

        // No brand profile, so no zone: today is UTC's, and the answer says so.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("timeZoneId").ValueKind);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.Parse(body.GetProperty("today").GetString()!));
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
        await SeedEverythingAsync(_fixture.WorkspaceA, scale: 1);
        await SeedEverythingAsync(_fixture.WorkspaceB, scale: 2);

        // Everything B holds that has a name, renamed so it can be recognised wherever it turns up.
        await InWorkspaceAsync(_fixture.WorkspaceB, async db =>
        {
            await db.Recipes.ForEachAsync(recipe => recipe.Title = $"B_ONLY recipe {recipe.Id:N}", Ct);
            await db.ContentCards.ForEachAsync(card => card.Title = $"B_ONLY card {card.Id:N}", Ct);
            await db.MediaAssets.ForEachAsync(asset => asset.Title = $"B_ONLY picture {asset.Id:N}", Ct);
            await db.TestIssues.ForEachAsync(issue => issue.Title = $"B_ONLY issue {issue.Id:N}", Ct);
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var bIds = await InWorkspaceAsync(_fixture.WorkspaceB, async db => new HashSet<string>(
            (await db.Recipes.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.ContentCards.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.MediaAssets.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.AiOperations.Select(item => item.Id).ToListAsync(Ct))
                .Concat(await db.PublishingRecords.Select(item => item.Id).ToListAsync(Ct))
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

    private async Task SeedEverythingAsync(SeededWorkspace workspace, int scale)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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
    }

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
}
