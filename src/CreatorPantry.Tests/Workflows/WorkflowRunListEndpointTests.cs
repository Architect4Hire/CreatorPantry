using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>GET .../workflow-runs</c> through the real Gateway (13A.11, PERF-001): what each view holds and in what order,
/// why a run needs a look, what a summary carries and leaves out, paging and its cursor, a fixed statement count however
/// many runs there are, and two workspaces.
/// </summary>
public sealed class WorkflowRunListEndpointTests : IAsyncLifetime
{
    private const string MemberPassword = "correct horse battery staple";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly CommandCounter _commands = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // recipe.create has a second version here, so a run on the first is on an older one.
            services.AddSingleton<IWorkflowCatalogue>(new CatalogueWithASecondVersion());
            services.ConfigureDbContext<CreatorPantryDbContext>(options => options.AddInterceptors(_commands));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Views ----

    [Fact]
    public async Task Mine_is_the_default_and_holds_my_runs_in_progress_whether_i_started_them_or_they_are_mine_to_do()
    {
        var me = await OwnerMembershipAsync(_fixture.WorkspaceA);
        var someoneElse = Guid.NewGuid();
        var started = await SeedAsync(_fixture.WorkspaceA, run => (run.CreatedByMembershipId, run.UpdatedAt) = (me, Now.AddHours(-1)));
        var assigned = await SeedAsync(_fixture.WorkspaceA, run =>
            (run.CreatedByMembershipId, run.AssignedMembershipId, run.UpdatedAt) = (someoneElse, me, Now.AddHours(-2)));
        await SeedAsync(_fixture.WorkspaceA, run => run.CreatedByMembershipId = someoneElse);
        await SeedAsync(_fixture.WorkspaceA, run => Complete(run, me));
        await SeedAsync(_fixture.WorkspaceA, run => Abandon(run, me));

        using var client = await OwnerAsync();

        Assert.Equal([started, assigned], Ids(await ListAsync(client, "")));
        Assert.Equal([started, assigned], Ids(await ListAsync(client, "?view=MINE")));
    }

    [Fact]
    public async Task Completed_holds_the_workspaces_completed_runs_most_recent_first()
    {
        var me = await OwnerMembershipAsync(_fixture.WorkspaceA);
        var older = await SeedAsync(_fixture.WorkspaceA, run => { run.UpdatedAt = Now.AddDays(-3); Complete(run, me); });
        var newer = await SeedAsync(_fixture.WorkspaceA, run => { run.UpdatedAt = Now.AddDays(-1); Complete(run, Guid.NewGuid()); });
        await SeedAsync(_fixture.WorkspaceA, run => run.CreatedByMembershipId = me);
        await SeedAsync(_fixture.WorkspaceA, run => Abandon(run, me));

        using var client = await OwnerAsync();

        Assert.Equal([newer, older], Ids(await ListAsync(client, "?view=completed")));
    }

    [Fact]
    public async Task All_holds_every_run_in_the_workspace_whatever_its_state()
    {
        var running = await SeedAsync(_fixture.WorkspaceA, run => run.UpdatedAt = Now.AddMinutes(-1));
        var completed = await SeedAsync(_fixture.WorkspaceA, run => { Complete(run, Guid.NewGuid()); run.UpdatedAt = Now.AddMinutes(-2); });
        var abandoned = await SeedAsync(_fixture.WorkspaceA, run => { Abandon(run, Guid.NewGuid()); run.UpdatedAt = Now.AddMinutes(-3); });

        using var client = await OwnerAsync();

        Assert.Equal([running, completed, abandoned], Ids(await ListAsync(client, "?view=all")));
    }

    [Fact]
    public async Task A_definition_narrows_any_view_and_one_no_workflow_has_is_an_empty_page()
    {
        var recipe = await SeedAsync(_fixture.WorkspaceA);
        await SeedAsync(_fixture.WorkspaceA, run =>
        {
            run.DefinitionKey = "images.create";
            run.DefinitionVersion = 1;
            run.CurrentStepKey = "about";
            run.StepCount = WorkflowCatalogue.Find("images.create", 1)!.Steps.Count;
        });

        using var client = await OwnerAsync();

        Assert.Equal([recipe], Ids(await ListAsync(client, "?view=all&definition=recipe.create")));
        var none = await ListAsync(client, "?view=all&definition=no.such-workflow");
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("nextCursor").ValueKind);
    }

    // ---- Needs attention ----

    [Fact]
    public async Task Attention_holds_runs_in_progress_that_are_idle_or_outdated_oldest_first_and_says_why()
    {
        var idle = await SeedAsync(_fixture.WorkspaceA, run => run.UpdatedAt = Now - WorkflowRunPolicy.IdleAfter - TimeSpan.FromHours(1));
        var outdated = await SeedAsync(_fixture.WorkspaceA, run => (run.DefinitionVersion, run.UpdatedAt) = (1, Now.AddMinutes(-5)));
        var both = await SeedAsync(_fixture.WorkspaceA, run => (run.DefinitionVersion, run.UpdatedAt) = (1, Now.AddDays(-30)));
        await SeedAsync(_fixture.WorkspaceA, run => run.UpdatedAt = Now.AddMinutes(-1));
        await SeedAsync(_fixture.WorkspaceA, run => { Complete(run, Guid.NewGuid()); run.DefinitionVersion = 1; run.UpdatedAt = Now.AddDays(-30); });
        await SeedAsync(_fixture.WorkspaceA, run => { Abandon(run, Guid.NewGuid()); run.UpdatedAt = Now.AddDays(-30); });

        using var client = await OwnerAsync();
        var page = await ListAsync(client, "?view=attention");

        Assert.Equal([both, idle, outdated], Ids(page));
        Assert.Equal(["idle", "newer_version"], Reasons(page, both));
        Assert.Equal(["idle"], Reasons(page, idle));
        Assert.Equal(["newer_version"], Reasons(page, outdated));
    }

    [Fact]
    public async Task A_step_the_reader_cannot_take_needs_their_attention_and_not_an_owners()
    {
        // The ingredients step's one action needs a Contributor.
        var onIngredients = await SeedAsync(_fixture.WorkspaceA, run => run.CurrentStepKey = "ingredients");

        using var viewer = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Viewer), MemberPassword, Ct);
        var theirs = await ListAsync(viewer, "?view=attention");
        Assert.Equal([onIngredients], Ids(theirs));
        Assert.Equal(["not_yours_to_do"], Reasons(theirs, onIngredients));
        Assert.False(Item(theirs, onIngredients).GetProperty("nextAction").GetProperty("youCanDoIt").GetBoolean());

        using var owner = await OwnerAsync();
        Assert.Empty(Ids(await ListAsync(owner, "?view=attention")));
        Assert.True(Item(await ListAsync(owner, "?view=all"), onIngredients).GetProperty("nextAction").GetProperty("youCanDoIt").GetBoolean());
    }

    [Fact]
    public async Task Reasons_ride_on_every_run_in_progress_whatever_the_view_and_never_on_a_finished_one()
    {
        var me = await OwnerMembershipAsync(_fixture.WorkspaceA);
        var idle = await SeedAsync(_fixture.WorkspaceA, run => (run.CreatedByMembershipId, run.UpdatedAt) = (me, Now.AddDays(-10)));
        var finished = await SeedAsync(_fixture.WorkspaceA, run => { Complete(run, me); run.DefinitionVersion = 1; run.UpdatedAt = Now.AddDays(-10); });

        using var client = await OwnerAsync();

        Assert.Equal(["idle"], Reasons(await ListAsync(client, "?view=mine"), idle));
        Assert.Empty(Reasons(await ListAsync(client, "?view=all"), finished));
    }

    // ---- What a summary carries ----

    [Fact]
    public async Task A_summary_carries_pointers_counts_and_names_and_nothing_a_run_or_its_records_hold()
    {
        var me = await OwnerMembershipAsync(_fixture.WorkspaceA);
        var startedFrom = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var runId = await SeedAsync(
            _fixture.WorkspaceA,
            run =>
            {
                (run.CreatedByMembershipId, run.AssignedMembershipId) = (me, gone);
                run.CurrentStepKey = "start";
                run.CompletedStepCount = 1;
                run.SkippedStepCount = 1;
                run.LastRoute = "recipes/new";
                run.Title = "Grandma's soda bread";
            },
            (run, db) =>
            {
                db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now.AddMinutes(-10), startedFrom, stepKey: null));
                var made = WorkflowRunSeeds.Link(run, Now.AddMinutes(-1));
                made.ResourceKind = WorkflowResourceKind.MediaAsset;
                db.WorkflowRunLinks.Add(made);
                var skipped = WorkflowRunSeeds.Step(run, "method");
                skipped.Status = WorkflowStepRunStatus.Skipped;
                skipped.SkippedAt = Now;
                skipped.SkippedByMembershipId = me;
                skipped.SkipReason = "A private note";
                skipped.DraftResourceKind = WorkflowResourceKind.Recipe;
                skipped.DraftResourceId = Guid.NewGuid();
                skipped.DraftSavedAt = Now;
                db.WorkflowStepRuns.Add(skipped);
            });

        using var client = await OwnerAsync();
        var item = Item(await ListAsync(client, "?view=all"), runId);

        Assert.Equal("Grandma's soda bread", item.GetProperty("title").GetString());
        Assert.Equal("InProgress", item.GetProperty("status").GetString());
        Assert.Equal("recipe.create", item.GetProperty("definitionKey").GetString());
        Assert.Equal(2, item.GetProperty("definitionVersion").GetInt32());
        Assert.Equal("Make a recipe", item.GetProperty("goal").GetString());
        Assert.Equal(me, item.GetProperty("owner").GetProperty("membershipId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("owner").GetProperty("displayName").GetString()));
        Assert.Equal(gone, item.GetProperty("assignee").GetProperty("membershipId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("assignee").GetProperty("displayName").ValueKind);
        Assert.Equal(1, item.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(1, item.GetProperty("skippedStepCount").GetInt32());
        Assert.Equal("Begin your recipe", item.GetProperty("currentStep").GetProperty("title").GetString());

        // The record it started from, not the later one it made.
        Assert.Equal("Recipe", item.GetProperty("linkedResource").GetProperty("kind").GetString());
        Assert.Equal(startedFrom, item.GetProperty("linkedResource").GetProperty("id").GetGuid());

        var next = item.GetProperty("nextAction");
        Assert.Equal("start", next.GetProperty("stepKey").GetString());
        Assert.Equal(WorkflowCapabilities.ByKey[WorkflowCapabilities.RecipesNew].Name, next.GetProperty("capabilityName").GetString());
        Assert.True(next.GetProperty("youCanDoIt").GetBoolean());

        // Nothing the run page has and a list does not: no steps, drafts, reasons, links, routes or token.
        var names = item.EnumerateObject().Select(property => property.Name).ToHashSet();
        foreach (var absent in new[] { "steps", "links", "lastRoute", "resumeRoute", "concurrencyToken", "warnings", "abandonReason", "readyToComplete" })
        {
            Assert.DoesNotContain(absent, names);
        }

        Assert.DoesNotContain("A private note", item.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("recipes/new", item.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_with_no_origin_shows_its_latest_record_and_one_with_none_shows_none()
    {
        var latest = Guid.NewGuid();
        var withMade = await SeedAsync(_fixture.WorkspaceA, links: (run, db) =>
        {
            db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now.AddMinutes(-9)));
            db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now.AddMinutes(-2), latest, stepKey: "ingredients"));
        });
        var bare = await SeedAsync(_fixture.WorkspaceA);

        using var client = await OwnerAsync();
        var page = await ListAsync(client, "?view=all");

        Assert.Equal(latest, Item(page, withMade).GetProperty("linkedResource").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, Item(page, bare).GetProperty("linkedResource").ValueKind);
    }

    [Fact]
    public async Task Only_a_run_with_a_step_still_to_do_has_a_next_action()
    {
        var steps = WorkflowCatalogue.RecipeCreateV1.Steps.Count;
        var waiting = await SeedAsync(_fixture.WorkspaceA, run =>
            (run.CurrentStepKey, run.CompletedStepCount, run.SkippedStepCount) = ("yield-and-time", steps - 1, 1));
        var completed = await SeedAsync(_fixture.WorkspaceA, run => Complete(run, Guid.NewGuid()));
        var abandoned = await SeedAsync(_fixture.WorkspaceA, run => { Abandon(run, Guid.NewGuid()); run.CurrentStepKey = "method"; });

        using var client = await OwnerAsync();
        var page = await ListAsync(client, "?view=all");

        Assert.Equal(JsonValueKind.Null, Item(page, waiting).GetProperty("nextAction").ValueKind);
        Assert.Equal(JsonValueKind.Null, Item(page, completed).GetProperty("nextAction").ValueKind);
        Assert.Equal(JsonValueKind.Null, Item(page, completed).GetProperty("currentStep").ValueKind);

        // A stopped run keeps the step it was left on: how far it got.
        Assert.Equal(JsonValueKind.Null, Item(page, abandoned).GetProperty("nextAction").ValueKind);
        Assert.Equal("method", Item(page, abandoned).GetProperty("currentStep").GetProperty("key").GetString());
    }

    // ---- Paging ----

    [Fact]
    public async Task Pages_hold_every_run_once_in_order_even_when_runs_share_a_moment()
    {
        var shared = Now.AddMinutes(-30);
        var expected = new List<(DateTimeOffset UpdatedAt, Guid Id)>();
        for (var index = 0; index < 7; index++)
        {
            var updatedAt = index < 4 ? shared : Now.AddMinutes(-index);
            expected.Add((updatedAt, await SeedAsync(_fixture.WorkspaceA, run => run.UpdatedAt = updatedAt)));
        }

        using var client = await OwnerAsync();
        var seen = new List<Guid>();
        var sizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(client, $"?view=all&limit=3{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}");
            var ids = Ids(page);
            seen.AddRange(ids);
            sizes.Add(ids.Count);
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal([3, 3, 1], sizes);
        Assert.Equal(
            expected.OrderByDescending(run => run.UpdatedAt).ThenByDescending(run => run.Id, GuidOrder.Instance).Select(run => run.Id),
            seen);
    }

    [Fact]
    public async Task An_out_of_range_limit_is_clamped_rather_than_refused()
    {
        await SeedAsync(_fixture.WorkspaceA);
        await SeedAsync(_fixture.WorkspaceA);

        using var client = await OwnerAsync();
        var one = await ListAsync(client, "?view=all&limit=0");

        Assert.Single(one.GetProperty("items").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, one.GetProperty("nextCursor").ValueKind);
        Assert.Equal(2, Ids(await ListAsync(client, "?view=all&limit=5000")).Count);
    }

    [Fact]
    public async Task A_cursor_works_only_for_the_list_and_the_reader_it_was_issued_to()
    {
        for (var index = 0; index < 3; index++)
        {
            await SeedAsync(_fixture.WorkspaceA, run => run.UpdatedAt = Now.AddMinutes(-index));
        }

        using var owner = await OwnerAsync();
        var cursor = Uri.EscapeDataString((await ListAsync(owner, "?view=all&limit=1")).GetProperty("nextCursor").GetString()!);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{RunsIn(_fixture.WorkspaceA)}?view=all&limit=1&cursor={cursor}", Ct)).StatusCode);

        await AssertCursorRefusedAsync(owner, $"{RunsIn(_fixture.WorkspaceA)}?view=completed&cursor={cursor}");
        await AssertCursorRefusedAsync(owner, $"{RunsIn(_fixture.WorkspaceA)}?view=all&definition=recipe.create&cursor={cursor}");
        await AssertCursorRefusedAsync(owner, $"{RunsIn(_fixture.WorkspaceA)}?view=all&cursor=not-a-cursor");

        using var editor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Editor), MemberPassword, Ct);
        await AssertCursorRefusedAsync(editor, $"{RunsIn(_fixture.WorkspaceA)}?view=all&cursor={cursor}");

        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        await AssertCursorRefusedAsync(ownerB, $"{RunsIn(_fixture.WorkspaceB)}?view=all&cursor={cursor}");
    }

    [Theory]
    [InlineData("?view=3")]
    [InlineData("?view=everything")]
    public async Task A_view_that_cannot_be_read_is_refused_by_name(string query)
    {
        using var client = await OwnerAsync();

        var response = await client.GetAsync($"{RunsIn(_fixture.WorkspaceA)}{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunListInvalid, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("view", out _));
    }

    // ---- PERF-001 ----

    [Fact]
    public async Task A_page_runs_the_same_statements_however_many_runs_and_people_it_holds()
    {
        await SeedManyAsync(3);
        using var client = await OwnerAsync();
        await ListAsync(client, "?view=all&limit=100");

        _commands.Reset();
        Assert.Equal(3, Ids(await ListAsync(client, "?view=all&limit=100")).Count);
        var small = _commands.Count;

        await SeedManyAsync(37);
        _commands.Reset();
        Assert.Equal(40, Ids(await ListAsync(client, "?view=all&limit=100")).Count);
        var large = _commands.Count;

        Assert.Equal(small, large);

        // The step rows — where drafts and reasons live — are never read to build a list.
        Assert.DoesNotContain(_commands.Texts, text => text.Contains("WorkflowStepRuns", StringComparison.OrdinalIgnoreCase));

        // Every run here has its own owner, an assignee and links, so nothing above is a lucky constant.
        async Task SeedManyAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                await SeedAsync(
                    _fixture.WorkspaceA,
                    run => (run.CreatedByMembershipId, run.AssignedMembershipId) = (Guid.NewGuid(), Guid.NewGuid()),
                    (run, db) =>
                    {
                        db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now, stepKey: null));
                        db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now));
                    });
            }
        }
    }

    // ---- Who may, and isolation ----

    [Fact]
    public async Task A_viewer_may_read_the_list()
    {
        var runId = await SeedAsync(_fixture.WorkspaceA);
        using var viewer = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Viewer), MemberPassword, Ct);

        Assert.Equal([runId], Ids(await ListAsync(viewer, "?view=all")));
    }

    [Fact]
    public async Task No_view_of_one_workspace_holds_a_run_of_another()
    {
        var meA = await OwnerMembershipAsync(_fixture.WorkspaceA);
        var meB = await OwnerMembershipAsync(_fixture.WorkspaceB);

        // B's runs are every kind a view of A could hold, and each is B's owner's — and one claims A's owner too.
        var bRuns = new[]
        {
            await SeedAsync(_fixture.WorkspaceB, run => (run.CreatedByMembershipId, run.AssignedMembershipId) = (meB, meA)),
            await SeedAsync(_fixture.WorkspaceB, run => { Complete(run, meB); }),
            await SeedAsync(_fixture.WorkspaceB, run => (run.CreatedByMembershipId, run.UpdatedAt, run.DefinitionVersion) = (meA, Now.AddDays(-30), 1)),
            await SeedAsync(_fixture.WorkspaceB, run => Abandon(run, meB)),
        };
        var aRun = await SeedAsync(_fixture.WorkspaceA, run => (run.CreatedByMembershipId, run.UpdatedAt) = (meA, Now.AddDays(-30)));

        // A run here naming B's owner: a pointer this workspace cannot put a name to.
        var namingB = await SeedAsync(_fixture.WorkspaceA, run =>
            (run.CreatedByMembershipId, run.AssignedMembershipId, run.UpdatedAt) = (meB, meB, Now.AddDays(-31)));

        using var ownerA = await OwnerAsync();
        foreach (var view in new[] { "mine", "completed", "attention", "all" })
        {
            var ids = Ids(await ListAsync(ownerA, $"?view={view}"));
            Assert.DoesNotContain(ids, id => bRuns.Contains(id));
        }

        var all = await ListAsync(ownerA, "?view=all");
        Assert.Equal([aRun, namingB], Ids(all));
        Assert.Equal(JsonValueKind.Null, Item(all, namingB).GetProperty("owner").GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, Item(all, namingB).GetProperty("assignee").GetProperty("displayName").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, Item(all, aRun).GetProperty("owner").GetProperty("displayName").ValueKind);

        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        Assert.DoesNotContain(aRun, Ids(await ListAsync(ownerB, "?view=all", _fixture.WorkspaceB)));
        Assert.Equal(bRuns.Order(), Ids(await ListAsync(ownerB, "?view=all", _fixture.WorkspaceB)).Order());
    }

    // ---- Helpers ----

    private static string RunsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-runs";

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    private async Task<JsonElement> ListAsync(GatewayClient client, string query, SeededWorkspace? workspace = null)
    {
        var response = await client.GetAsync($"{RunsIn(workspace ?? _fixture.WorkspaceA)}{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        return await BodyOf(response);
    }

    private static async Task AssertCursorRefusedAsync(GatewayClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunListCursorInvalid, (await BodyOf(response)).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private static JsonElement Item(JsonElement page, Guid runId) =>
        page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == runId);

    private static List<string> Reasons(JsonElement page, Guid runId) =>
        [.. Item(page, runId).GetProperty("attentionReasons").EnumerateArray().Select(reason => reason.GetString()!)];

    private static void Complete(WorkflowRun run, Guid by)
    {
        run.CreatedByMembershipId = by;
        run.Status = WorkflowRunStatus.Completed;
        run.CompletedAt = run.UpdatedAt;
        run.CurrentStepKey = null;
    }

    private static void Abandon(WorkflowRun run, Guid by)
    {
        run.CreatedByMembershipId = by;
        run.Status = WorkflowRunStatus.Abandoned;
        run.AbandonedAt = run.UpdatedAt;
        run.AbandonedByMembershipId = by;
    }

    /// <summary>
    /// A run of <c>recipe.create</c> on its newest version here (2), on its first step, touched just now — shaped by
    /// <paramref name="shape"/> — written straight to the store, as no request could set its dates.
    /// </summary>
    private Task<Guid> SeedAsync(
        SeededWorkspace workspace,
        Action<WorkflowRun>? shape = null,
        Action<WorkflowRun, CreatorPantryDbContext>? links = null) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var run = WorkflowRunSeeds.Run(Now);
            run.DefinitionVersion = 2;
            run.StepCount = WorkflowCatalogue.RecipeCreateV1.Steps.Count;
            shape?.Invoke(run);
            db.WorkflowRuns.Add(run);
            links?.Invoke(run, db);
            await db.SaveChangesAsync(Ct);

            return run.Id;
        });

    private Task<Guid> OwnerMembershipAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.WorkspaceId == workspace.Id && membership.Role == WorkspaceRole.Owner)
            .Select(membership => membership.Id)
            .SingleAsync(Ct));

    private async Task<string> AddMemberAsync(WorkspaceRole role)
    {
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, MemberPassword);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);

        return email;
    }

    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>The order the database gives ids — SQLite compares them as the text EF stores, upper-case.</summary>
    private sealed class GuidOrder : IComparer<Guid>
    {
        public static readonly GuidOrder Instance = new();

        public int Compare(Guid x, Guid y) =>
            string.CompareOrdinal(x.ToString("D").ToUpperInvariant(), y.ToString("D").ToUpperInvariant());
    }

    /// <summary>The shipped catalogue plus a second version of <c>recipe.create</c> with its own goal and first step's title.</summary>
    private sealed class CatalogueWithASecondVersion : IWorkflowCatalogue
    {
        private static readonly WorkflowDefinition RecipeCreateV2 = WorkflowCatalogue.RecipeCreateV1 with
        {
            Version = 2,
            Goal = "Make a recipe",
            Steps =
            [
                WorkflowCatalogue.RecipeCreateV1.Steps[0] with { Title = "Begin your recipe" },
                .. WorkflowCatalogue.RecipeCreateV1.Steps.Skip(1),
            ],
        };

        public IReadOnlyList<WorkflowDefinition> All { get; } = [.. WorkflowCatalogue.All, RecipeCreateV2];

        public WorkflowDefinition? Find(WorkflowKey key, int version) =>
            All.FirstOrDefault(definition => definition.Key == key && definition.Version == version);

        public IReadOnlyList<WorkflowDefinition> Latest() =>
            [.. All.GroupBy(definition => definition.Key).Select(family => family.MaxBy(definition => definition.Version)!)];
    }

    /// <summary>Counts every SQL statement the API host runs.</summary>
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
}
