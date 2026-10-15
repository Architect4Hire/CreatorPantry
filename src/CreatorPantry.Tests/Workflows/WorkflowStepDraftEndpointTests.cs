using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>PUT .../workflow-runs/{runId}/steps/{stepKey}/draft</c> through the real Gateway (13A.7): the first save, a
/// repeat, a save against a newer edit, what refuses one, who may make one, a save that fails, and two workspaces.
/// </summary>
/// <remarks>
/// SQLite does not move a row version on update, so here a stale token is one the run never held; that the token
/// really moves, and that a save racing another is refused by the database, is proved against SQL Server in
/// <see cref="WorkflowRunSqlServerTests"/>.
/// </remarks>
public sealed class WorkflowStepDraftEndpointTests : IAsyncLifetime
{
    private const string ContributorPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private readonly SaveFault _fault = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real data layer, behind a switch that makes its next save fail.
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IWorkflowRunDataLayer));
            services.Remove(real);
            services.Add(new ServiceDescriptor(
                typeof(IWorkflowRunDataLayer),
                provider => new FaultingDataLayer(
                    (IWorkflowRunDataLayer)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), _fault),
                real.Lifetime));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    // ---- Saving ----

    [Fact]
    public async Task The_first_draft_starts_the_step_and_says_when_it_was_saved()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var run = await BodyOf(response);
        var about = run.GetProperty("steps")[0];
        Assert.Equal("InProgress", about.GetProperty("status").GetString());
        Assert.Equal("CreativeContext", about.GetProperty("draftKind").GetString());
        Assert.Equal(context, about.GetProperty("draftResourceId").GetGuid());
        Assert.Equal(JsonValueKind.String, about.GetProperty("draftSavedAt").ValueKind);

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var row = await db.WorkflowStepRuns.AsNoTracking().SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == "about", Ct);
            var stored = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);
            Assert.Equal(WorkflowStepRunStatus.InProgress, row.Status);
            Assert.NotNull(row.StartedAt);
            Assert.Equal(context, row.DraftResourceId);
            Assert.Equal(row.DraftSavedAt, stored.UpdatedAt);

            // A pointer, not a result: nothing is linked and nothing is completed.
            Assert.Equal(0, await db.WorkflowRunLinks.CountAsync(link => link.WorkflowRunId == runId, Ct));
            Assert.Equal(0, stored.CompletedStepCount);

            return 0;
        });
    }

    [Fact]
    public async Task Saving_the_same_draft_again_writes_nothing_even_with_an_old_token()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await BodyOf(await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token));
        var before = await SnapshotAsync(runId);

        // A debounced client, or a retry after a timeout, repeating what already landed.
        var again = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, NeverHeld);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await BodyOf(again);
        Assert.Equal(first.GetProperty("concurrencyToken").GetString(), body.GetProperty("concurrencyToken").GetString());
        Assert.Equal(
            first.GetProperty("steps")[0].GetProperty("draftSavedAt").GetDateTimeOffset(),
            body.GetProperty("steps")[0].GetProperty("draftSavedAt").GetDateTimeOffset());
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    [Fact]
    public async Task A_different_draft_against_a_newer_edit_is_refused_with_what_the_run_holds_now()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var kept = await SeedContextAsync(_fixture.WorkspaceA);
        var other = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", kept, token);
        var before = await SnapshotAsync(runId);

        var response = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", other, NeverHeld);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.Equal(kept, body.GetProperty("draftResourceId").GetGuid());
        Assert.Equal("CreativeContext", body.GetProperty("draftKind").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("currentConcurrencyToken").GetString()));
        Assert.Equal(before, await SnapshotAsync(runId));
    }

    // ---- What refuses a draft ----

    [Fact]
    public async Task A_step_takes_only_the_kinds_its_actions_keep_work_in()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "recipe.create");
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // The start step keeps a recipe or a creative context; a long-form piece is neither.
        var wrongKind = await SaveAsync(client, _fixture.WorkspaceA, runId, "start", "LongFormDraft", Guid.NewGuid(), token);

        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        var body = await BodyOf(wrongKind);
        Assert.Equal(WorkflowErrorCodes.StepDraftInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task A_step_whose_work_is_already_on_the_run_has_no_draft_to_save()
    {
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "recipe.improve", new { kind = "Recipe", id = recipeId });
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, runId, "change", "Recipe", recipeId, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepDraftInvalid, Code(await BodyOf(response)));
    }

    /// <summary>Each kind a draft can be is checked by a different module, so each is asked both ways.</summary>
    [Theory]
    [InlineData("images.create", "about", "CreativeContext")]
    [InlineData("content.blog-and-seo", "about", "LongFormDraft")]
    [InlineData("recipe.create", "start", "Recipe")]
    public async Task Another_workspaces_record_answers_exactly_as_an_unknown_one_does(string definitionKey, string stepKey, string kind)
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, definitionKey);
        var theirs = await SeedRecordAsync(_fixture.WorkspaceB, kind);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var foreign = await SaveAsync(client, _fixture.WorkspaceA, runId, stepKey, kind, theirs, token);
        var unknown = await SaveAsync(client, _fixture.WorkspaceA, runId, stepKey, kind, Guid.NewGuid(), token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.StepDraftUnprocessable, Code(foreignBody));
        AssertSameAnswer(await BodyOf(unknown), foreignBody);
        Assert.Null(await DraftOfAsync(runId, stepKey));
    }

    [Fact]
    public async Task A_stale_token_is_answered_before_the_record_is_looked_at()
    {
        var (runId, _) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var theirs = await SeedContextAsync(_fixture.WorkspaceB);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // Another workspace's record and an old token: the token decides, so the answer says nothing of the record.
        var response = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", theirs, NeverHeld);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(await BodyOf(response)));
    }

    [Theory]
    [InlineData("moved on")]
    [InlineData("finished")]
    public async Task A_retry_of_a_save_that_landed_is_answered_as_one_after_the_run_has_moved_on(string what)
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token);

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            if (what == "moved on")
            {
                run.CurrentStepKey = "plan";
            }
            else
            {
                run.Status = WorkflowRunStatus.Abandoned;
                run.AbandonedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var retry = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, NeverHeld);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(context, (await BodyOf(retry)).GetProperty("steps")[0].GetProperty("draftResourceId").GetGuid());
    }

    [Theory]
    [InlineData("{\"id\":\"8f5a2b0e-0000-4000-8000-000000000001\",\"expectedConcurrencyToken\":\"AAAAAAAAAAA=\"}", "kind")]
    [InlineData("{\"kind\":\"CreativeContext\",\"expectedConcurrencyToken\":\"AAAAAAAAAAA=\"}", "id")]
    public async Task A_draft_names_its_kind_and_its_record(string json, string field)
    {
        var (runId, _) = await StartAsync(_fixture.WorkspaceA, "images.create");
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.PutAsJsonAsync(
            $"{RunsIn(_fixture.WorkspaceA)}/{runId}/steps/about/draft", JsonDocument.Parse(json).RootElement, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepDraftInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
    }

    [Fact]
    public async Task A_saved_draft_whose_record_has_gone_is_a_warning_when_the_run_is_read()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token);

        // The context goes after the draft was saved against it.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.CreativeContexts.Remove(await db.CreativeContexts.SingleAsync(item => item.Id == context, Ct));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var run = await BodyOf(await client.GetAsync($"{RunsIn(_fixture.WorkspaceA)}/{runId}", Ct));

        var warning = Assert.Single(run.GetProperty("warnings").EnumerateArray());
        Assert.Equal(WorkflowRunWarningCodes.DraftMissing, warning.GetProperty("code").GetString());
        Assert.Equal(context, warning.GetProperty("resourceId").GetGuid());
        Assert.Equal("about", warning.GetProperty("stepKey").GetString());
    }

    [Fact]
    public async Task Only_the_step_the_run_is_on_takes_a_draft()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var later = await SaveAsync(client, _fixture.WorkspaceA, runId, "make-and-keep", "CreativeContext", Guid.NewGuid(), token);
        var nowhere = await SaveAsync(client, _fixture.WorkspaceA, runId, "no-such-step", "CreativeContext", Guid.NewGuid(), token);

        Assert.Equal(HttpStatusCode.Conflict, later.StatusCode);
        var body = await BodyOf(later);
        Assert.Equal(WorkflowErrorCodes.StepNotCurrent, Code(body));
        Assert.Equal("about", body.GetProperty("currentStepKey").GetString());
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepNotFound, Code(await BodyOf(nowhere)));
    }

    [Fact]
    public async Task A_finished_run_takes_no_more_drafts()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == runId, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(response)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a token")]
    [InlineData("AAAA")]
    public async Task A_missing_or_malformed_token_is_a_bad_request_not_a_conflict(string? token)
    {
        var (runId, _) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.StepDraftInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- Who may ----

    [Fact]
    public async Task An_editor_may_save_on_someone_elses_run()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(
            HttpStatusCode.OK,
            (await SaveAsync(editor, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token)).StatusCode);
    }

    [Fact]
    public async Task A_contributor_may_save_on_a_run_that_is_theirs_or_is_for_them_and_on_no_other()
    {
        var (email, membershipId) = await AddContributorAsync(_fixture.WorkspaceA);
        var (othersRun, othersToken) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var contributor = await SignInAsync(email, ContributorPassword);

        var refused = await SaveAsync(contributor, _fixture.WorkspaceA, othersRun, "about", "CreativeContext", context, othersToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var refusal = await BodyOf(refused);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(refusal));

        // Someone who may not change the run is told nothing about its draft.
        Assert.False(refusal.TryGetProperty("draftResourceId", out _));
        Assert.False(refusal.TryGetProperty("currentConcurrencyToken", out _));
        Assert.Null(await DraftOfAsync(othersRun, "about"));

        // Assigned to them, the same run is theirs to carry on.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.WorkflowRuns.SingleAsync(item => item.Id == othersRun, Ct)).AssignedMembershipId = membershipId;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var token = await TokenOfAsync(othersRun);
        Assert.Equal(
            HttpStatusCode.OK,
            (await SaveAsync(contributor, _fixture.WorkspaceA, othersRun, "about", "CreativeContext", context, token)).StatusCode);

        // And one they started is theirs.
        var startResponse = await contributor.PostAsJsonAsync(RunsIn(_fixture.WorkspaceA), new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct);
        var own = await BodyOf(startResponse);
        Assert.Equal(
            HttpStatusCode.OK,
            (await SaveAsync(contributor, _fixture.WorkspaceA, own.GetProperty("id").GetGuid(), "about", "CreativeContext", context,
                own.GetProperty("concurrencyToken").GetString())).StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_save_a_draft()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceB, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceB);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await SaveAsync(viewer, _fixture.WorkspaceB, runId, "about", "CreativeContext", context, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- A save that fails ----

    [Fact]
    public async Task A_save_that_fails_is_not_reported_as_saved_and_leaves_the_run_as_it_was()
    {
        var (runId, token) = await StartAsync(_fixture.WorkspaceA, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        var before = await SnapshotAsync(runId);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        _fault.Arm();
        HttpResponseMessage response;
        try
        {
            response = await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token);
        }
        finally
        {
            _fault.Disarm();
        }

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(runId));

        // And the same save, once the store is back, lands.
        Assert.Equal(
            HttpStatusCode.OK,
            (await SaveAsync(client, _fixture.WorkspaceA, runId, "about", "CreativeContext", context, token)).StatusCode);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does()
    {
        var (theirs, token) = await StartAsync(_fixture.WorkspaceB, "images.create");
        var context = await SeedContextAsync(_fixture.WorkspaceA);
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var foreign = await SaveAsync(ownerA, _fixture.WorkspaceA, theirs, "about", "CreativeContext", context, token);
        var unknown = await SaveAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid(), "about", "CreativeContext", context, token);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        AssertSameAnswer(await BodyOf(unknown), foreignBody);

        await InWorkspaceAsync(_fixture.WorkspaceB, async db =>
        {
            Assert.Null((await db.WorkflowStepRuns.AsNoTracking()
                .SingleAsync(step => step.WorkflowRunId == theirs && step.StepKey == "about", Ct)).DraftResourceId);

            return 0;
        });
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(string email, string? password = null) =>
        password is null ? _fixture.SignInAsync(email, cancellationToken: Ct) : _fixture.SignInAsync(email, password, Ct);

    private static Task<HttpResponseMessage> SaveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid runId, string stepKey, string kind, Guid id, string? token) =>
        client.PutAsJsonAsync(
            $"{RunsIn(workspace)}/{runId}/steps/{stepKey}/draft",
            new { kind, id, expectedConcurrencyToken = token },
            Ct);

    private async Task<(Guid RunId, string Token)> StartAsync(SeededWorkspace workspace, string definitionKey, object? startingResource = null)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync(RunsIn(workspace), new { definitionKey, startingResource }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await BodyOf(response);

        return (run.GetProperty("id").GetGuid(), run.GetProperty("concurrencyToken").GetString()!);
    }

    private async Task<Guid> CreateRecipeAsync(SeededWorkspace workspace, string title)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>One record of each kind a step keeps a draft in, made in the given workspace.</summary>
    private async Task<Guid> SeedRecordAsync(SeededWorkspace workspace, string kind)
    {
        switch (kind)
        {
            case "CreativeContext":
                return await SeedContextAsync(workspace);
            case "Recipe":
                return await CreateRecipeAsync(workspace, "Neighbour's secret loaf");
            case "LongFormDraft":
                var context = await SeedContextAsync(workspace);
                return await InWorkspaceAsync(workspace, async db =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var draft = new Domain.Modules.Content.Data.Entities.LongFormDraft
                    {
                        Id = Guid.NewGuid(),
                        CreativeContextId = context,
                        Kind = Domain.Modules.Content.Managers.LongFormKind.BlogArticle,
                        Status = Domain.Modules.Content.Managers.LongFormDraftStatus.Draft,
                        CreatedByMembershipId = Guid.NewGuid(),
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    db.LongFormDrafts.Add(draft);
                    await db.SaveChangesAsync(Ct);

                    return draft.Id;
                });
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private Task<Guid> SeedContextAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var context = CreativeContextSeeds.NewContext(DateTimeOffset.UtcNow);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

    private Task<Guid?> DraftOfAsync(Guid runId, string stepKey) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db => (await db.WorkflowStepRuns.AsNoTracking()
            .SingleAsync(step => step.WorkflowRunId == runId && step.StepKey == stepKey, Ct)).DraftResourceId);

    private Task<string> TokenOfAsync(Guid runId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db => Convert.ToBase64String(
            (await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct)).RowVersion));

    private Task<string> SnapshotAsync(Guid runId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == runId, Ct);
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);

            return $"{Convert.ToBase64String(run.RowVersion)}|{run.UpdatedAt:O}|{run.CurrentStepKey}|"
                + string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.StartedAt:O}:{step.DraftResourceKind}:{step.DraftResourceId}:{step.DraftSavedAt:O}"));
        });

    private async Task<(string Email, Guid MembershipId)> AddContributorAsync(SeededWorkspace workspace)
    {
        var email = $"contributor-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, ContributorPassword);
        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        };

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync(Ct);

        return (email, membership.Id);
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

    /// <summary>A switch a test arms to make the next save fail.</summary>
    private sealed class SaveFault
    {
        private volatile bool _armed;

        public bool Armed => _armed;

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;
    }

    /// <summary>The real data layer, except that while the fault is armed a save throws before anything is written.</summary>
    private sealed class FaultingDataLayer(IWorkflowRunDataLayer inner, SaveFault fault) : IWorkflowRunDataLayer
    {
        public Task CreateAsync(
            WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps, IReadOnlyList<WorkflowRunLink> links, CancellationToken cancellationToken) =>
            inner.CreateAsync(run, steps, links, cancellationToken);

        public Task<WorkflowRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetAsync(runId, cancellationToken);

        public Task<WorkflowRunRecord?> GetForUpdateAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetForUpdateAsync(runId, cancellationToken);

        public Task<Domain.Modules.Workflows.Data.WorkflowRunWrite> RestartAsync(
            WorkflowRun oldRun,
            byte[] expectedRowVersion,
            WorkflowRun newRun,
            IReadOnlyList<WorkflowStepRun> newSteps,
            IReadOnlyList<WorkflowRunLink> newLinks,
            CancellationToken cancellationToken) =>
            inner.RestartAsync(oldRun, expectedRowVersion, newRun, newSteps, newLinks, cancellationToken);

        public Task<WorkflowRunWrite> AbandonAsync(
            WorkflowRun run, byte[] expectedRowVersion, AuditEntry audit, CancellationToken cancellationToken) =>
            inner.AbandonAsync(run, expectedRowVersion, audit, cancellationToken);

        public Task<WorkflowRunWrite> SaveAsync(
            WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken, IReadOnlyList<WorkflowRunLink>? newLinks = null) =>
            fault.Armed
                ? throw new InvalidOperationException("The store is unavailable.")
                : inner.SaveAsync(run, expectedRowVersion, cancellationToken, newLinks);
    }
}
