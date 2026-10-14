using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs</c> through the real Gateway (13A.5): starting from nothing and from a record, what
/// refuses a start, replay, the caller's role, and the boundary between two workspaces.
/// </summary>
public sealed class WorkflowRunStartEndpointTests : IAsyncLifetime
{
    private const string ContributorPassword = "correct horse battery staple";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-runs";

    private Task<GatewayClient> SignInAsync(string email, string? password = null) =>
        password is null ? _fixture.SignInAsync(email, cancellationToken: Ct) : _fixture.SignInAsync(email, password, Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    /// <summary>Starts a run. A key is generated unless one is given; pass an empty string to send none.</summary>
    private static Task<HttpResponseMessage> StartAsync(GatewayClient client, SeededWorkspace workspace, object body, string? key = null) =>
        key == string.Empty
            ? client.PostAsJsonAsync(RunsIn(workspace), body, Ct)
            : client.PostAsJsonAsync(RunsIn(workspace), body, key ?? Guid.NewGuid().ToString(), Ct);

    // ---- Starting ----

    [Fact]
    public async Task A_run_from_nothing_starts_on_the_newest_version_at_its_first_step_and_does_nothing_else()
    {
        var recipesBefore = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.Recipes.CountAsync(Ct));
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var run = await BodyOf(response);
        var id = run.GetProperty("id").GetGuid();
        Assert.EndsWith($"{RunsIn(_fixture.WorkspaceA)}/{id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        Assert.Equal("recipe.create", run.GetProperty("definitionKey").GetString());
        Assert.Equal(1, run.GetProperty("definitionVersion").GetInt32());
        Assert.Equal("Create a recipe", run.GetProperty("title").GetString());
        Assert.Equal("InProgress", run.GetProperty("status").GetString());
        Assert.Equal(5, run.GetProperty("stepCount").GetInt32());
        Assert.Equal(0, run.GetProperty("completedStepCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(run.GetProperty("concurrencyToken").GetString()));
        Assert.All(run.GetProperty("steps").EnumerateArray(), step => Assert.Equal("NotStarted", step.GetProperty("status").GetString()));
        Assert.Empty(run.GetProperty("links").EnumerateArray());

        var next = run.GetProperty("nextAction");
        Assert.Equal("start", next.GetProperty("stepKey").GetString());
        Assert.Empty(next.GetProperty("waitingFor").EnumerateArray());
        Assert.Equal(
            [("recipes.new", "recipes/new"), ("recipes.draft-with-help", "ai-recipe-studio")],
            next.GetProperty("actions").EnumerateArray()
                .Select(action => (action.GetProperty("capabilityKey").GetString(), action.GetProperty("route").GetString())));
        Assert.All(next.GetProperty("actions").EnumerateArray(), action => Assert.True(action.GetProperty("youCanDoIt").GetBoolean()));

        // Stored as one run with a row for every step, and nothing of the first step's work done for them.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var stored = await db.WorkflowRuns.SingleAsync(row => row.Id == id, Ct);
            Assert.Equal("start", stored.CurrentStepKey);
            Assert.Null(stored.AssignedMembershipId);
            Assert.Equal(5, await db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == id, Ct));
            Assert.Equal(0, await db.WorkflowRunLinks.CountAsync(link => link.WorkflowRunId == id, Ct));
            Assert.Equal(recipesBefore, await db.Recipes.CountAsync(Ct));

            return 0;
        });
    }

    [Fact]
    public async Task A_run_from_the_record_its_first_step_links_starts_with_that_step_done()
    {
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var run = await BodyOf(await StartAsync(client, _fixture.WorkspaceA, new
        {
            definitionKey = "recipe.test-and-approve",
            startingResource = new { kind = "Recipe", id = recipeId },
        }));

        Assert.Equal(1, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(
            ["Completed", "NotStarted", "NotStarted", "NotStarted", "NotStarted", "NotStarted"],
            run.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("status").GetString()));

        var link = Assert.Single(run.GetProperty("links").EnumerateArray());
        Assert.Equal("Recipe", link.GetProperty("kind").GetString());
        Assert.Equal(recipeId, link.GetProperty("resourceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, link.GetProperty("stepKey").ValueKind);

        // The next step's address is filled from the record the run holds.
        var next = run.GetProperty("nextAction");
        Assert.Equal("basics", next.GetProperty("stepKey").GetString());
        Assert.Equal($"recipes/{recipeId:D}", next.GetProperty("actions")[0].GetProperty("route").GetString());

        // A fresh run carries on where its next step's first action goes, and has nothing to warn about.
        Assert.Equal($"recipes/{recipeId:D}", run.GetProperty("resumeRoute").GetString());
        Assert.Empty(run.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_picture_or_a_card_starts_its_journey_the_same_way()
    {
        var assetId = await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var asset = SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var cardId = await CreateCardAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var organize = await BodyOf(await StartAsync(client, _fixture.WorkspaceA, new
        {
            definitionKey = "assets.organize",
            startingResource = new { kind = "MediaAsset", id = assetId },
        }));
        var publish = await BodyOf(await StartAsync(client, _fixture.WorkspaceA, new
        {
            definitionKey = "publishing.plan-and-publish",
            startingResource = new { kind = "ContentCard", id = cardId },
        }));

        Assert.Equal($"dam/{assetId:D}", organize.GetProperty("nextAction").GetProperty("actions")[0].GetProperty("route").GetString());
        Assert.Equal($"content-board/{cardId:D}", publish.GetProperty("nextAction").GetProperty("actions")[0].GetProperty("route").GetString());
        Assert.Equal(1, organize.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(1, publish.GetProperty("completedStepCount").GetInt32());
    }

    [Fact]
    public async Task A_record_the_first_step_does_not_link_is_held_without_ticking_anything_off()
    {
        // Pictures start from a recipe for the sake of its lead-picture step; their first step files a context.
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var run = await BodyOf(await StartAsync(client, _fixture.WorkspaceA, new
        {
            definitionKey = "images.create",
            startingResource = new { kind = "Recipe", id = recipeId },
        }));

        Assert.Equal(0, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal("about", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Equal(recipeId, Assert.Single(run.GetProperty("links").EnumerateArray()).GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task A_run_takes_the_creators_name_for_it_trimmed()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var run = await BodyOf(await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create", title = "  Spring focaccia  " }));

        Assert.Equal("Spring focaccia", run.GetProperty("title").GetString());
    }

    // ---- What refuses a start ----

    [Theory]
    [InlineData("{\"definitionKey\":\"recipe.nowhere\"}", "definitionKey")]
    [InlineData("{}", "definitionKey")]
    [InlineData("{\"definitionKey\":\"recipe.create\",\"title\":\"   \"}", "title")]
    [InlineData("{\"definitionKey\":\"recipe.create\",\"startingResource\":{\"kind\":\"Recipe\",\"id\":\"8f5a2b0e-0000-4000-8000-000000000001\"}}", "startingResource.kind")]
    [InlineData("{\"definitionKey\":\"recipe.improve\",\"startingResource\":{\"kind\":\"Recipe\",\"id\":\"00000000-0000-0000-0000-000000000000\"}}", "startingResource.id")]
    public async Task A_misshapen_start_names_the_field_at_fault(string json, string field)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(client, _fixture.WorkspaceA, JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"no error for {field}: {body}");
    }

    [Fact]
    public async Task A_name_longer_than_a_run_takes_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(client, _fixture.WorkspaceA, new
        {
            definitionKey = "recipe.create",
            title = new string('a', WorkflowRunPolicy.TitleMaxLength + 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_journey_that_cannot_be_taken_yet_says_why_and_starts_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "planning.day-and-week" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.DefinitionUnavailable, Code(body));
        Assert.Equal(WorkflowCatalogue.PlanDayAndWeekV1.UnavailableReason, body.GetProperty("reason").GetString());
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    /// <summary>
    /// Each kind goes through a different module's facade with its own idea of "there", so each is asked both
    /// ways — and the whole answer is compared, not just its code, so a field that ever echoed the id would show.
    /// </summary>
    [Theory]
    [InlineData("Recipe", "recipe.improve")]
    [InlineData("MediaAsset", "assets.organize")]
    [InlineData("ContentCard", "publishing.plan-and-publish")]
    public async Task Another_workspaces_record_answers_exactly_as_an_unknown_one_does(string kind, string definitionKey)
    {
        var theirs = await SeedRecordAsync(_fixture.WorkspaceB, kind);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var foreign = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey, startingResource = new { kind, id = theirs } });
        var unknown = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey, startingResource = new { kind, id = Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(unknown.StatusCode, foreign.StatusCode);

        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.StartingResourceUnprocessable, Code(foreignBody));
        AssertSameAnswer(await BodyOf(unknown), foreignBody);
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task Fields_a_start_does_not_take_are_ignored_rather_than_believed()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var run = await BodyOf(await StartAsync(client, _fixture.WorkspaceA, new
        {
            definitionKey = "recipe.create",
            workspaceId = _fixture.WorkspaceB.Id,
            createdByMembershipId = Guid.NewGuid(),
            assignedMembershipId = Guid.NewGuid(),
            definitionVersion = 7,
            status = "Completed",
        }));

        Assert.Equal(1, run.GetProperty("definitionVersion").GetInt32());
        Assert.Equal("InProgress", run.GetProperty("status").GetString());

        var id = run.GetProperty("id").GetGuid();
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var stored = await db.WorkflowRuns.SingleAsync(row => row.Id == id, Ct);
            var ownerMembership = await db.WorkspaceMemberships
                .Where(membership => membership.WorkspaceId == _fixture.WorkspaceA.Id && membership.Role == WorkspaceRole.Owner)
                .Select(membership => membership.Id)
                .SingleAsync(Ct);

            Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);
            Assert.Equal(ownerMembership, stored.CreatedByMembershipId);
            Assert.Null(stored.AssignedMembershipId);

            return 0;
        });
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task An_unknown_workspace_answers_as_one_the_caller_is_not_in()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var body = new { definitionKey = "recipe.create" };

        var inaccessible = await StartAsync(ownerA, _fixture.WorkspaceB, body);
        var unknown = await ownerA.PostAsJsonAsync("/api/v1/workspaces/no-such-workspace/workflow-runs", body, Guid.NewGuid().ToString(), Ct);

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertSameAnswer(await BodyOf(unknown), await BodyOf(inaccessible));
    }

    // ---- Replay ----

    [Fact]
    public async Task A_retry_with_the_same_key_returns_the_same_run_and_starts_no_second()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var body = new { definitionKey = "recipe.create", title = "Once" };

        var first = await StartAsync(client, _fixture.WorkspaceA, body, key);
        var second = await StartAsync(client, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader)));
        Assert.Equal((await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(second)).GetProperty("id").GetGuid());
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task The_same_key_for_a_different_start_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create" }, key);
        var reused = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "brand.voice" }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(reused)));
    }

    [Fact]
    public async Task A_start_without_a_key_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create" }, key: string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_key_belongs_to_one_member_in_one_workspace()
    {
        // The Owner of A is made an Editor of B too, so one person can use one key in both.
        await AddMemberAsync(_fixture.WorkspaceB, _fixture.WorkspaceA.OwnerEmail, WorkspaceRole.Editor);
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var editorA = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var key = Guid.NewGuid().ToString();
        var body = new { definitionKey = "recipe.create" };

        var inA = await StartAsync(ownerA, _fixture.WorkspaceA, body, key);
        var inB = await StartAsync(ownerA, _fixture.WorkspaceB, body, key);
        var byColleague = await StartAsync(editorA, _fixture.WorkspaceA, body, key);

        var ids = new List<Guid>();
        foreach (var response in new[] { inA, inB, byColleague })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.False(response.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
            ids.Add((await BodyOf(response)).GetProperty("id").GetGuid());
        }

        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(2, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkflowRuns.CountAsync(Ct)));
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_replay_by_someone_who_has_since_lost_the_role_is_refused_not_answered()
    {
        var email = await AddContributorAsync(_fixture.WorkspaceA);
        using var contributor = await SignInAsync(email, ContributorPassword);
        var key = Guid.NewGuid().ToString();
        var body = new { definitionKey = "recipe.create" };

        Assert.Equal(HttpStatusCode.Created, (await StartAsync(contributor, _fixture.WorkspaceA, body, key)).StatusCode);

        await SetRoleAsync(_fixture.WorkspaceA, email, WorkspaceRole.Viewer);
        var replay = await StartAsync(contributor, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.False(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
    }

    [Fact]
    public async Task A_malformed_key_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create" }, key: "has a space");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyInvalidCode, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_name_that_differs_only_by_spaces_is_the_same_start()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        var first = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create", title = "Once" }, key);
        var second = await StartAsync(client, _fixture.WorkspaceA, new { definitionKey = "recipe.create", title = "  Once " }, key);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader)));
        Assert.Equal((await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(second)).GetProperty("id").GetGuid());
    }

    // ---- Roles ----

    [Fact]
    public async Task A_viewer_cannot_start_a_journey()
    {
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await StartAsync(viewer, _fixture.WorkspaceB, new { definitionKey = "recipe.create" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Refused by the route's policy before the facade is reached, so with the edge's own code.
        Assert.Equal("forbidden", Code(await BodyOf(response)));
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_contributor_starts_what_they_can_do_and_is_told_who_can_start_the_rest()
    {
        var email = await AddContributorAsync(_fixture.WorkspaceA);
        using var contributor = await SignInAsync(email, ContributorPassword);

        // Creating a recipe is a Contributor's to do.
        Assert.Equal(
            HttpStatusCode.Created,
            (await StartAsync(contributor, _fixture.WorkspaceA, new { definitionKey = "recipe.create" })).StatusCode);

        // Setting up the brand voice starts with an Editor's action, and has no alternative.
        var refused = await StartAsync(contributor, _fixture.WorkspaceA, new { definitionKey = "brand.voice" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await BodyOf(refused);
        Assert.Equal(WorkflowErrorCodes.RoleInsufficient, Code(body));
        Assert.Equal("Editor", body.GetProperty("neededRole").GetString());
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_later_step_that_needs_more_than_the_caller_has_does_not_stop_a_start()
    {
        // The approval step is an Editor's; the Editor member may start, and a Contributor would be told at that step.
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await StartAsync(editor, _fixture.WorkspaceA, new
        {
            definitionKey = "recipe.test-and-approve",
            startingResource = new { kind = "Recipe", id = recipeId },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_run_started_in_one_workspace_is_not_in_the_other()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var mine = (await BodyOf(await StartAsync(ownerA, _fixture.WorkspaceA, new { definitionKey = "recipe.create" }))).GetProperty("id").GetGuid();
        var theirs = (await BodyOf(await StartAsync(ownerB, _fixture.WorkspaceB, new { definitionKey = "recipe.create" }))).GetProperty("id").GetGuid();

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            Assert.Equal([mine], await db.WorkflowRuns.Select(run => run.Id).ToListAsync(Ct));
            Assert.Equal(0, await db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == theirs, Ct));

            return 0;
        });
        await InWorkspaceAsync(_fixture.WorkspaceB, async db =>
        {
            Assert.Equal([theirs], await db.WorkflowRuns.Select(run => run.Id).ToListAsync(Ct));

            return 0;
        });
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_start_a_journey_in_the_other()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await StartAsync(ownerA, _fixture.WorkspaceB, new { definitionKey = "recipe.create" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.WorkflowRuns.CountAsync(Ct)));
    }

    // ---- Helpers ----

    private async Task<Guid> CreateRecipeAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title = "Soda bread" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private async Task<Guid> CreateCardAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/content-cards", new { title = "Soda bread week" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("card").GetProperty("id").GetGuid();
    }

    /// <summary>One record of each kind a run can start from, made in the given workspace.</summary>
    private async Task<Guid> SeedRecordAsync(SeededWorkspace workspace, string kind) => kind switch
    {
        "Recipe" => await CreateRecipeAsync(workspace),
        "ContentCard" => await CreateCardAsync(workspace),
        "MediaAsset" => await InWorkspaceAsync(workspace, async db =>
        {
            var asset = SeededMediaAsset.For(workspace.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Two refusals are the same answer when everything but the request's own trace says the same.</summary>
    private static void AssertSameAnswer(JsonElement expected, JsonElement actual)
    {
        static JsonNode Comparable(JsonElement body)
        {
            var node = JsonNode.Parse(body.GetRawText())!.AsObject();
            node.Remove("traceId");
            node.Remove("correlationId");

            return node;
        }

        var left = Comparable(expected);
        var right = Comparable(actual);
        Assert.True(JsonNode.DeepEquals(left, right), $"answers differ:\n{left.ToJsonString()}\n{right.ToJsonString()}");
    }

    private async Task AddMemberAsync(SeededWorkspace workspace, string email, WorkspaceRole role)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(Ct);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetRoleAsync(SeededWorkspace workspace, string email, WorkspaceRole role)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(Ct);
        var membership = await db.WorkspaceMemberships
            .SingleAsync(row => row.WorkspaceId == workspace.Id && row.UserId == userId, Ct);
        membership.Role = role;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A third member, a Contributor, which the two-workspace fixture does not seed.</summary>
    private async Task<string> AddContributorAsync(SeededWorkspace workspace)
    {
        var email = $"contributor-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, ContributorPassword);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
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
}
