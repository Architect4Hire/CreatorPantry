using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs/{runId}/steps/{stepKey}/completion</c> through the real Gateway (13A.8): steps completed
/// on the owning modules' own facts — a record made, readiness met, a recipe saved, a status reached, a piece moved
/// on — and refused when those facts say no; replay, the token, results, the last step, and two workspaces. The
/// rules that would take another module's whole journey to satisfy here are <see cref="WorkflowFactsTests"/>.
/// </summary>
public sealed class WorkflowStepCompletionEndpointTests : IAsyncLifetime
{
    private const string ContributorPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private readonly RaceSwitch _race = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real data layer, behind a switch that makes the next save land and then report that it lost —
            // which is exactly what a request sees when a duplicate of it committed first.
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(Domain.Modules.Workflows.Data.IWorkflowRunDataLayer));
            services.Remove(real);
            services.Add(new ServiceDescriptor(
                typeof(Domain.Modules.Workflows.Data.IWorkflowRunDataLayer),
                provider => new RacingDataLayer(
                    (Domain.Modules.Workflows.Data.IWorkflowRunDataLayer)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), _race),
                real.Lifetime));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    // ---- A record made ----

    [Fact]
    public async Task A_step_that_makes_a_record_completes_from_its_draft_and_moves_the_run_on()
    {
        using var client = await OwnerAAsync();
        var recipeId = await CreateRecipeAsync(client);
        var runId = await StartAsync(client, "recipe.create");
        await SaveDraftAsync(client, runId, "start", "Recipe", recipeId);

        var response = await CompleteAsync(client, runId, "start", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await BodyOf(response);
        Assert.Equal("Completed", run.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal(1, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal("ingredients", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Equal($"recipes/{recipeId:D}", run.GetProperty("nextAction").GetProperty("actions")[0].GetProperty("route").GetString());

        var link = Assert.Single(run.GetProperty("links").EnumerateArray());
        Assert.Equal(recipeId, link.GetProperty("resourceId").GetGuid());
        Assert.Equal("start", link.GetProperty("stepKey").GetString());
        Assert.False(run.GetProperty("readyToComplete").GetBoolean());
    }

    [Fact]
    public async Task A_step_that_makes_several_records_takes_them_as_results_and_links_each_once()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "content.blog-and-seo");
        var (contextId, draftId) = await SeedLongFormAsync(_fixture.WorkspaceA, LongFormDraftStatus.Draft);

        // The context is already on the run, as an earlier step might have put it; completing names it again.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.WorkflowRunLinks.Add(Link(runId, WorkflowResourceKind.CreativeContext, contextId));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var response = await CompleteAsync(client, runId, "about", await TokenAsync(client, runId),
            [new { kind = "CreativeContext", id = contextId }, new { kind = "LongFormDraft", id = draftId }]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var links = (await BodyOf(response)).GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count);
        Assert.Single(links, link => link.GetProperty("resourceId").GetGuid() == contextId);
        Assert.Single(links, link => link.GetProperty("resourceId").GetGuid() == draftId);
    }

    [Fact]
    public async Task A_step_that_makes_a_record_is_not_done_until_it_names_one()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "recipe.create");
        var before = await SnapshotAsync(runId);

        var response = await CompleteAsync(client, runId, "start", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepIncomplete, Code(body));
        Assert.Equal(["Say which recipe this step made."], body.GetProperty("missing").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    // ---- The recipe module's own facts ----

    [Fact]
    public async Task A_readiness_step_is_done_when_the_recipe_module_says_so_and_not_before()
    {
        using var client = await OwnerAAsync();
        var recipeId = await CreateRecipeAsync(client);
        var runId = await StartAsync(client, "recipe.create");
        await SaveDraftAsync(client, runId, "start", "Recipe", recipeId);
        await CompleteAsync(client, runId, "start", await TokenAsync(client, runId));

        var early = await CompleteAsync(client, runId, "ingredients", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        var body = await BodyOf(early);
        Assert.Equal(WorkflowErrorCodes.StepIncomplete, Code(body));
        Assert.NotEmpty(body.GetProperty("missing").EnumerateArray());

        await EditRecipeAsync(client, recipeId, new { ingredientGroups = new[] { new { ingredients = new[] { new { displayText = "2 cups flour" } } } } });
        var done = await CompleteAsync(client, runId, "ingredients", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("method", (await BodyOf(done)).GetProperty("nextAction").GetProperty("stepKey").GetString());
    }

    [Fact]
    public async Task Making_changes_is_done_once_the_recipe_has_been_saved_since_the_run_began()
    {
        using var client = await OwnerAAsync();
        var recipeId = await CreateRecipeAsync(client);
        var runId = await StartAsync(client, "recipe.improve", new { kind = "Recipe", id = recipeId });

        var early = await CompleteAsync(client, runId, "change", await TokenAsync(client, runId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal(["Save a change to the recipe first."], (await BodyOf(early)).GetProperty("missing").EnumerateArray().Select(item => item.GetString()));

        await EditRecipeAsync(client, recipeId, new { title = "Soda bread, better" });
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "change", await TokenAsync(client, runId))).StatusCode);
    }

    [Fact]
    public async Task A_status_step_reads_the_recipes_status_and_never_moves_it()
    {
        using var client = await OwnerAAsync();
        var recipeId = await CreateRecipeAsync(client);
        var runId = await StartAsync(client, "recipe.test-and-approve", new { kind = "Recipe", id = recipeId });
        await PutRunOnAsync(runId, "send-for-review", "basics", "cook", "fix");

        var early = await CompleteAsync(client, runId, "send-for-review", await TokenAsync(client, runId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal(["The recipe is still a draft."], (await BodyOf(early)).GetProperty("missing").EnumerateArray().Select(item => item.GetString()));

        // Reached through the recipe's own transitions in real use; set here, because the subject is the reading.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.Recipes.SingleAsync(recipe => recipe.Id == recipeId, Ct)).Status = RecipeStatus.ReadyForReview;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "send-for-review", await TokenAsync(client, runId))).StatusCode);
        Assert.Equal(
            RecipeStatus.ReadyForReview,
            await InWorkspaceAsync(_fixture.WorkspaceA, db => db.Recipes.Where(recipe => recipe.Id == recipeId).Select(recipe => recipe.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_piece_of_writing_step_is_done_when_the_piece_has_moved_on()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "content.blog-and-seo");
        var (contextId, draftId) = await SeedLongFormAsync(_fixture.WorkspaceA, LongFormDraftStatus.Draft);
        await CompleteAsync(client, runId, "about", await TokenAsync(client, runId),
            [new { kind = "CreativeContext", id = contextId }, new { kind = "LongFormDraft", id = draftId }]);

        var early = await CompleteAsync(client, runId, "outline", await TokenAsync(client, runId));
        Assert.Equal(["The piece is still being planned."], (await BodyOf(early)).GetProperty("missing").EnumerateArray().Select(item => item.GetString()));

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.LongFormDrafts.SingleAsync(draft => draft.Id == draftId, Ct)).Status = LongFormDraftStatus.Outlined;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var done = await CompleteAsync(client, runId, "outline", await TokenAsync(client, runId));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("write", (await BodyOf(done)).GetProperty("nextAction").GetProperty("stepKey").GetString());
    }

    // ---- The last step ----

    [Fact]
    public async Task After_the_last_step_the_run_waits_on_it_ready_to_be_finished()
    {
        using var client = await OwnerAAsync();
        var assetId = await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var asset = SeededMediaAsset.For(_fixture.WorkspaceA.Id);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });
        var runId = await StartAsync(client, "assets.organize", new { kind = "MediaAsset", id = assetId });

        foreach (var step in new[] { "describe", "tag", "note-use" })
        {
            Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, step, await TokenAsync(client, runId))).StatusCode);
        }

        var run = await ReadAsync(client, runId);
        Assert.Equal("InProgress", run.GetProperty("status").GetString());
        Assert.Equal(4, run.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("nextAction").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resumeRoute").ValueKind);
        Assert.True(run.GetProperty("readyToComplete").GetBoolean());
        Assert.Equal(
            "note-use",
            await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkflowRuns.Where(item => item.Id == runId).Select(item => item.CurrentStepKey).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Ready_to_finish_once_every_required_step_is_done_while_an_optional_one_is_still_offered()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync(_fixture.WorkspaceA);
        var assetId = await SeedAsync(_fixture.WorkspaceA, "MediaAsset");

        await CompleteAsync(client, runId, "about", await TokenAsync(client, runId), [new { kind = "CreativeContext", id = contextId }]);
        await CompleteAsync(client, runId, "plan", await TokenAsync(client, runId));
        var response = await CompleteAsync(client, runId, "make-and-keep", await TokenAsync(client, runId), [new { kind = "MediaAsset", id = assetId }]);

        var run = await BodyOf(response);
        Assert.True(run.GetProperty("readyToComplete").GetBoolean());
        Assert.Equal("lead-picture", run.GetProperty("nextAction").GetProperty("stepKey").GetString());
    }

    // ---- Replay and the token ----

    [Fact]
    public async Task Completing_a_step_that_is_already_complete_writes_nothing_and_answers_as_the_run_stands()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync(_fixture.WorkspaceA);
        await CompleteAsync(client, runId, "about", await TokenAsync(client, runId), [new { kind = "CreativeContext", id = contextId }]);
        await CompleteAsync(client, runId, "plan", await TokenAsync(client, runId));
        var before = await SnapshotAsync(runId);

        // A retry of the first, after the run has moved on, with a token it never held and another workspace's record.
        var replay = await CompleteAsync(client, runId, "about", NeverHeld, [new { kind = "CreativeContext", id = await SeedContextAsync(_fixture.WorkspaceB) }]);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("make-and-keep", (await BodyOf(replay)).GetProperty("nextAction").GetProperty("stepKey").GetString());
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_retry_must_still_be_well_formed()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync(_fixture.WorkspaceA);
        await CompleteAsync(client, runId, "about", await TokenAsync(client, runId), [new { kind = "CreativeContext", id = contextId }]);

        var response = await CompleteAsync(client, runId, "about", token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepCompletionInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    [Fact]
    public async Task A_duplicate_that_loses_the_race_to_the_first_still_lands()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync(_fixture.WorkspaceA);

        _race.Arm();
        HttpResponseMessage response;
        try
        {
            response = await CompleteAsync(client, runId, "about", await TokenAsync(client, runId), [new { kind = "CreativeContext", id = contextId }]);
        }
        finally
        {
            _race.Disarm();
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Completed", (await BodyOf(response)).GetProperty("steps")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_completion_against_a_run_that_has_changed_is_refused_with_its_current_token()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var contextId = await SeedContextAsync(_fixture.WorkspaceA);
        var before = await SnapshotAsync(runId);

        var response = await CompleteAsync(client, runId, "about", NeverHeld, [new { kind = "CreativeContext", id = contextId }]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("currentConcurrencyToken").GetString()));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    // ---- Results ----

    /// <summary>Each kind a step makes is checked by a different module, so each is asked both ways.</summary>
    [Theory]
    [InlineData("Recipe")]
    [InlineData("CreativeContext")]
    [InlineData("LongFormDraft")]
    [InlineData("MediaAsset")]
    [InlineData("ContentCard")]
    public async Task Another_workspaces_record_as_a_result_answers_exactly_as_an_unknown_one_does(string kind)
    {
        using var client = await OwnerAAsync();
        var (runId, stepKey, own) = await RunOnAStepThatMakesAsync(client, kind);
        var theirs = await SeedAsync(_fixture.WorkspaceB, kind);
        var token = await TokenAsync(client, runId);
        var before = await SnapshotAsync(runId);

        var foreign = await CompleteAsync(client, runId, stepKey, token, [.. own, new { kind, id = theirs }]);
        var unknown = await CompleteAsync(client, runId, stepKey, token, [.. own, new { kind, id = Guid.NewGuid() }]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.StepResultUnprocessable, Code(foreignBody));
        AssertSameAnswer(await BodyOf(unknown), foreignBody);
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_draft_pointing_at_another_workspaces_record_is_refused_and_says_it_was_the_draft()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var theirs = await SeedContextAsync(_fixture.WorkspaceB);

        // Only reachable by a pointer written before the record left — set here directly.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var row = await db.WorkflowStepRuns.SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == "about", Ct);
            row.Status = WorkflowStepRunStatus.InProgress;
            row.StartedAt = DateTimeOffset.UtcNow;
            row.DraftResourceKind = WorkflowResourceKind.CreativeContext;
            row.DraftResourceId = theirs;
            row.DraftSavedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var before = await SnapshotAsync(runId);

        var response = await CompleteAsync(client, runId, "about", await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepResultUnprocessable, Code(body));
        Assert.Contains("draft", body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Theory]
    [InlineData("recipe")]
    [InlineData("piece")]
    public async Task A_stored_link_to_another_workspaces_record_reads_as_gone_and_never_as_met(string what)
    {
        using var client = await OwnerAAsync();
        Guid runId;
        string stepKey;

        if (what == "recipe")
        {
            // B's recipe, already where the rule wants it — and still not met here.
            var theirs = await SeedAsync(_fixture.WorkspaceB, "Recipe");
            await InWorkspaceAsync(_fixture.WorkspaceB, async db =>
            {
                (await db.Recipes.SingleAsync(recipe => recipe.Id == theirs, Ct)).Status = RecipeStatus.ReadyForReview;
                await db.SaveChangesAsync(Ct);

                return 0;
            });
            runId = await StartAsync(client, "recipe.test-and-approve", new { kind = "Recipe", id = await CreateRecipeAsync(client) });
            await PutRunOnAsync(runId, "send-for-review", "basics", "cook", "fix");
            await AddLinkAsync(runId, WorkflowResourceKind.Recipe, theirs, minutesFromNow: 1);
            stepKey = "send-for-review";
        }
        else
        {
            var (_, theirDraft) = await SeedLongFormAsync(_fixture.WorkspaceB, LongFormDraftStatus.Outlined);
            runId = await StartAsync(client, "content.blog-and-seo");
            await PutRunOnAsync(runId, "outline", "about");
            await AddLinkAsync(runId, WorkflowResourceKind.LongFormDraft, theirDraft, minutesFromNow: 1);
            stepKey = "outline";
        }

        var response = await CompleteAsync(client, runId, stepKey, await TokenAsync(client, runId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var missing = Assert.Single((await BodyOf(response)).GetProperty("missing").EnumerateArray()).GetString();
        Assert.Contains("can't be found", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Naming_a_record_the_run_already_holds_makes_it_the_one_the_next_step_reads()
    {
        using var client = await OwnerAAsync();
        var named = await CreateRecipeAsync(client);
        var other = await CreateRecipeAsync(client);
        await EditRecipeAsync(client, named, new { ingredientGroups = new[] { new { ingredients = new[] { new { displayText = "2 cups flour" } } } } });
        var runId = await StartAsync(client, "recipe.create");

        // Both on the run already, the one this step names linked first.
        await AddLinkAsync(runId, WorkflowResourceKind.Recipe, named, minutesFromNow: -10);
        await AddLinkAsync(runId, WorkflowResourceKind.Recipe, other, minutesFromNow: -5);

        await CompleteAsync(client, runId, "start", await TokenAsync(client, runId), [new { kind = "Recipe", id = named }]);

        // The ingredients step reads the named recipe, which has them — not the other, which does not.
        var ingredients = await CompleteAsync(client, runId, "ingredients", await TokenAsync(client, runId));
        Assert.Equal(HttpStatusCode.OK, ingredients.StatusCode);
        Assert.Equal($"recipes/{named:D}", (await BodyOf(ingredients)).GetProperty("nextAction").GetProperty("actions")[0].GetProperty("route").GetString());
    }

    [Fact]
    public async Task A_step_done_by_its_work_is_not_done_by_naming_a_record()
    {
        using var client = await OwnerAAsync();
        var recipeId = await CreateRecipeAsync(client);
        var runId = await StartAsync(client, "recipe.improve", new { kind = "Recipe", id = recipeId });

        var response = await CompleteAsync(client, runId, "change", await TokenAsync(client, runId), [new { kind = "Recipe", id = recipeId }]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepCompletionInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("results", out _));
    }

    // ---- Who, which step, which run ----

    [Fact]
    public async Task Only_the_current_step_of_a_run_in_progress_completes()
    {
        using var client = await OwnerAAsync();
        var runId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, runId);

        var later = await CompleteAsync(client, runId, "plan", token);
        Assert.Equal(HttpStatusCode.Conflict, later.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepNotCurrent, Code(await BodyOf(later)));

        var nowhere = await CompleteAsync(client, runId, "no-such-step", token);
        Assert.Equal(WorkflowErrorCodes.StepNotFound, Code(await BodyOf(nowhere)));

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var finished = await CompleteAsync(client, runId, "about", token);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(finished)));
    }

    [Fact]
    public async Task A_contributor_cannot_complete_a_step_on_someone_elses_run()
    {
        using var owner = await OwnerAAsync();
        var runId = await StartAsync(owner, "images.create");
        var contextId = await SeedContextAsync(_fixture.WorkspaceA);
        var email = await AddContributorAsync();
        using var contributor = await _fixture.SignInAsync(email, ContributorPassword, Ct);

        var response = await CompleteAsync(contributor, runId, "about", await TokenAsync(owner, runId), [new { kind = "CreativeContext", id = contextId }]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does()
    {
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = (await BodyOf(await ownerB.PostAsJsonAsync(RunsIn(_fixture.WorkspaceB), new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct)))
            .GetProperty("id").GetGuid();
        using var client = await OwnerAAsync();

        var foreign = await CompleteAsync(client, theirs, "about", NeverHeld);
        var unknown = await CompleteAsync(client, Guid.NewGuid(), "about", NeverHeld);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        AssertSameAnswer(await BodyOf(unknown), foreignBody);
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerAAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    /// <summary>Completes a step of a run in workspace A — whichever workspace the run itself belongs to.</summary>
    private Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string stepKey, string? token, object[]? results = null) =>
        client.PostAsJsonAsync(
            $"{RunsIn(_fixture.WorkspaceA)}/{runId}/steps/{stepKey}/completion",
            new { results, expectedConcurrencyToken = token },
            Ct);

    private async Task<Guid> StartAsync(GatewayClient client, string definitionKey, object? startingResource = null)
    {
        var response = await client.PostAsJsonAsync(RunsIn(_fixture.WorkspaceA), new { definitionKey, startingResource }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    private async Task<JsonElement> ReadAsync(GatewayClient client, Guid runId) =>
        await BodyOf(await client.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{runId}", Ct));

    private async Task SaveDraftAsync(GatewayClient client, Guid runId, string stepKey, string kind, Guid id)
    {
        var response = await client.PutAsJsonAsync(
            $"{RunsIn(_fixture.WorkspaceA)}/{runId}/steps/{stepKey}/draft",
            new { kind, id, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateRecipeAsync(GatewayClient client)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes", new { title = "Soda bread" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>Edits a recipe through its own route, so the recipe module records the change as it always does.</summary>
    private async Task EditRecipeAsync(GatewayClient client, Guid recipeId, object fields)
    {
        var path = $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/{recipeId}";
        var token = (await BodyOf(await client.GetAsync(path, Ct))).GetProperty("concurrencyToken").GetString();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        body["expectedConcurrencyToken"] = JsonSerializer.SerializeToElement(token);

        var response = await client.PatchAsJsonAsync(path, body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Moves a run onto a later step with the steps before it done, as completing them would have.</summary>
    private Task PutRunOnAsync(Guid runId, string current, params string[] done) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            foreach (var step in await db.WorkflowStepRuns.Where(item => item.WorkflowRunId == runId && done.Contains(item.StepKey)).ToListAsync(Ct))
            {
                step.Status = WorkflowStepRunStatus.Completed;
                step.CompletedAt = DateTimeOffset.UtcNow;
                step.CompletedByMembershipId = run.CreatedByMembershipId;
            }

            run.CompletedStepCount += done.Length;
            run.CurrentStepKey = current;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    /// <summary>A run on a step that makes a record of the kind, and the other records that step also needs.</summary>
    private async Task<(Guid RunId, string StepKey, object[] Own)> RunOnAStepThatMakesAsync(GatewayClient client, string kind)
    {
        switch (kind)
        {
            case "Recipe":
                return (await StartAsync(client, "recipe.create"), "start", []);
            case "CreativeContext":
                return (await StartAsync(client, "images.create"), "about", []);
            case "LongFormDraft":
                return (await StartAsync(client, "content.blog-and-seo"), "about",
                    [new { kind = "CreativeContext", id = await SeedContextAsync(_fixture.WorkspaceA) }]);
            case "MediaAsset":
                var images = await StartAsync(client, "images.create");
                await PutRunOnAsync(images, "make-and-keep", "about", "plan");
                return (images, "make-and-keep", []);
            case "ContentCard":
                return (await StartAsync(client, "publishing.plan-and-publish"), "choose", []);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>One record of the kind, made in the given workspace.</summary>
    private async Task<Guid> SeedAsync(SeededWorkspace workspace, string kind)
    {
        switch (kind)
        {
            case "Recipe":
                using (var owner = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct))
                {
                    var response = await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title = "Neighbour's loaf" }, Ct);
                    return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
                }

            case "ContentCard":
                using (var owner = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct))
                {
                    var response = await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/content-cards", new { title = "Neighbour's card" }, Ct);
                    return (await BodyOf(response)).GetProperty("card").GetProperty("id").GetGuid();
                }

            case "CreativeContext":
                return await SeedContextAsync(workspace);
            case "LongFormDraft":
                return (await SeedLongFormAsync(workspace, LongFormDraftStatus.Draft)).DraftId;
            case "MediaAsset":
                return await InWorkspaceAsync(workspace, async db =>
                {
                    var asset = SeededMediaAsset.For(workspace.Id);
                    db.MediaAssets.Add(asset);
                    db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
                    await db.SaveChangesAsync(Ct);

                    return asset.Id;
                });
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>Puts a link on a run directly, dated this many minutes from now.</summary>
    private Task AddLinkAsync(Guid runId, WorkflowResourceKind kind, Guid id, int minutesFromNow) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var link = Link(runId, kind, id);
            link.LinkedAt = DateTimeOffset.UtcNow.AddMinutes(minutesFromNow);
            db.WorkflowRunLinks.Add(link);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private Task<Guid> SeedContextAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var context = CreativeContextSeeds.NewContext(DateTimeOffset.UtcNow);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

    private async Task<(Guid ContextId, Guid DraftId)> SeedLongFormAsync(SeededWorkspace workspace, LongFormDraftStatus status)
    {
        var contextId = await SeedContextAsync(workspace);
        var draftId = await InWorkspaceAsync(workspace, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var draft = new Domain.Modules.Content.Data.Entities.LongFormDraft
            {
                Id = Guid.NewGuid(),
                CreativeContextId = contextId,
                Kind = LongFormKind.BlogArticle,
                Status = status,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.LongFormDrafts.Add(draft);
            await db.SaveChangesAsync(Ct);

            return draft.Id;
        });

        return (contextId, draftId);
    }

    private static WorkflowRunLink Link(Guid runId, WorkflowResourceKind kind, Guid id) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowRunId = runId,
        ResourceKind = kind,
        ResourceId = id,
        StepKey = null,
        LinkedAt = DateTimeOffset.UtcNow,
        LinkedByMembershipId = Guid.NewGuid(),
    };

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);
            var links = await db.WorkflowRunLinks.AsNoTracking().CountAsync(link => link.WorkflowRunId == runId, Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.UpdatedAt:O}|{run.CurrentStepKey}|{run.CompletedStepCount}|{links}|"
                + string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.CompletedAt:O}"));
        });

    private async Task<string> AddContributorAsync()
    {
        var email = $"contributor-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, ContributorPassword);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);

        return email;
    }

    private static void AssertSameAnswer(JsonElement expected, JsonElement actual)
    {
        static JsonNode Comparable(JsonElement body)
        {
            var node = JsonNode.Parse(body.GetRawText())!.AsObject();
            node.Remove("traceId");
            node.Remove("correlationId");

            return node;
        }

        Assert.True(JsonNode.DeepEquals(Comparable(expected), Comparable(actual)), $"answers differ:\n{expected}\n{actual}");
    }

    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>A switch a test arms to make the next save land and then report that it lost a race.</summary>
    private sealed class RaceSwitch
    {
        private volatile bool _armed;

        public bool Armed => _armed;

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;
    }

    private sealed class RacingDataLayer(Domain.Modules.Workflows.Data.IWorkflowRunDataLayer inner, RaceSwitch race)
        : Domain.Modules.Workflows.Data.IWorkflowRunDataLayer
    {
        public Task CreateAsync(
            WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps, IReadOnlyList<WorkflowRunLink> links, CancellationToken cancellationToken) =>
            inner.CreateAsync(run, steps, links, cancellationToken);

        public Task<Domain.Modules.Workflows.Data.WorkflowRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetAsync(runId, cancellationToken);

        public Task<Domain.Modules.Workflows.Data.WorkflowRunRecord?> GetForUpdateAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetForUpdateAsync(runId, cancellationToken);

        public Task<Domain.Modules.Workflows.Data.WorkflowRunWrite> RestartAsync(
            WorkflowRun oldRun,
            byte[] expectedRowVersion,
            WorkflowRun newRun,
            IReadOnlyList<WorkflowStepRun> newSteps,
            IReadOnlyList<WorkflowRunLink> newLinks,
            CancellationToken cancellationToken) =>
            inner.RestartAsync(oldRun, expectedRowVersion, newRun, newSteps, newLinks, cancellationToken);

        public Task<Domain.Modules.Workflows.Data.WorkflowRunWrite> AbandonAsync(
            WorkflowRun run, byte[] expectedRowVersion, Domain.Managers.Audit.AuditEntry audit, CancellationToken cancellationToken) =>
            inner.AbandonAsync(run, expectedRowVersion, audit, cancellationToken);

        public async Task<Domain.Modules.Workflows.Data.WorkflowRunWrite> SaveAsync(
            WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken, IReadOnlyList<WorkflowRunLink>? newLinks = null)
        {
            var written = await inner.SaveAsync(run, expectedRowVersion, cancellationToken, newLinks);

            return race.Armed ? Domain.Modules.Workflows.Data.WorkflowRunWrite.Stale : written;
        }
    }
}
