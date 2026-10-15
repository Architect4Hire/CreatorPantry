using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>POST .../workflow-runs/{runId}/restart</c> through the real Gateway (13A.9b): the old run stopped and kept
/// whole, a fresh run in its place carrying what it should, nothing done without confirmation, never a third run,
/// the refusals, who may, and two workspaces.
/// </summary>
public sealed class WorkflowRunRestartEndpointTests : IAsyncLifetime
{
    private const string ContributorPassword = "correct horse battery staple";

    private static readonly string NeverHeld = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

    private readonly RaceSwitch _race = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real data layer, behind a switch that makes the next Start over lose a race the way the real one
            // does: with the change tracker cleared under the whole request, idempotency record and all.
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IWorkflowRunDataLayer));
            services.Remove(real);
            services.Add(new ServiceDescriptor(
                typeof(IWorkflowRunDataLayer),
                provider => new RacingDataLayer(
                    (IWorkflowRunDataLayer)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!),
                    provider.GetRequiredService<CreatorPantryDbContext>(),
                    _race),
                real.Lifetime));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RunsIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    [Fact]
    public async Task Starting_over_keeps_the_old_run_whole_and_starts_a_fresh_one_with_what_it_should_carry()
    {
        using var client = await OwnerAsync();
        var recipeId = await CreateRecipeAsync(client);
        var oldRunId = await StartAsync(client, "recipe.test-and-approve", new { kind = "Recipe", id = recipeId });

        // The old run part-way, assigned to the Editor, with a draft saved on a step.
        var editorMembership = await MembershipOfAsync(_fixture.WorkspaceA.MemberEmail);
        var draftPointer = Guid.NewGuid();
        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == oldRunId, Ct);
            run.AssignedMembershipId = editorMembership;
            var basics = await db.WorkflowStepRuns.SingleAsync(step => step.WorkflowRunId == oldRunId && step.StepKey == "basics", Ct);
            basics.Status = WorkflowStepRunStatus.InProgress;
            basics.StartedAt = DateTimeOffset.UtcNow;
            basics.DraftResourceKind = WorkflowResourceKind.Recipe;
            basics.DraftResourceId = draftPointer;
            basics.DraftSavedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        var oldBefore = await StepsAndLinksAsync(oldRunId);

        var response = await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fresh = await BodyOf(response);
        var newRunId = fresh.GetProperty("id").GetGuid();
        Assert.NotEqual(oldRunId, newRunId);
        Assert.EndsWith($"/workflow-runs/{newRunId}", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The new run: fresh, on the same journey, starting from the same recipe, with its first step done by it.
        Assert.Equal("recipe.test-and-approve", fresh.GetProperty("definitionKey").GetString());
        Assert.Equal(1, fresh.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(recipeId, Assert.Single(fresh.GetProperty("links").EnumerateArray()).GetProperty("resourceId").GetGuid());
        Assert.All(fresh.GetProperty("steps").EnumerateArray().Skip(1), step => Assert.Equal("NotStarted", step.GetProperty("status").GetString()));
        Assert.All(fresh.GetProperty("steps").EnumerateArray(), step => Assert.Equal(JsonValueKind.Null, step.GetProperty("draftResourceId").ValueKind));

        // The old run: stopped, pointing at the new one, and otherwise exactly as it was.
        var old = await ReadAsync(client, oldRunId);
        Assert.Equal("Abandoned", old.GetProperty("status").GetString());
        Assert.Equal(newRunId, old.GetProperty("replacedByRunId").GetGuid());
        Assert.Equal(oldBefore, await StepsAndLinksAsync(oldRunId));

        // Same owner and same person it is for; the recipe itself untouched.
        await InWorkspaceAsync(async db =>
        {
            var oldRow = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == oldRunId, Ct);
            var newRow = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == newRunId, Ct);
            Assert.Equal(oldRow.CreatedByMembershipId, newRow.CreatedByMembershipId);
            Assert.Equal(editorMembership, newRow.AssignedMembershipId);
            Assert.Equal(oldRow.Title, newRow.Title);
            Assert.True(await db.Recipes.AnyAsync(recipe => recipe.Id == recipeId, Ct));

            return 0;
        });
    }

    [Fact]
    public async Task An_editor_starting_someone_elses_run_over_does_not_take_it_over()
    {
        using var owner = await OwnerAsync();
        var oldRunId = await StartAsync(owner, "images.create");
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);

        var fresh = await BodyOf(await RestartAsync(editor, oldRunId, await TokenAsync(owner, oldRunId), title: "Take two"));

        Assert.Equal("Take two", fresh.GetProperty("title").GetString());
        await InWorkspaceAsync(async db =>
        {
            var oldRow = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == oldRunId, Ct);
            var newRow = await db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == fresh.GetProperty("id").GetGuid(), Ct);
            Assert.Equal(oldRow.CreatedByMembershipId, newRow.CreatedByMembershipId);

            return 0;
        });
    }

    [Fact]
    public async Task A_starting_record_that_has_gone_is_not_carried_over()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        await InWorkspaceAsync(async db =>
        {
            db.WorkflowRunLinks.Add(new WorkflowRunLink
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = oldRunId,
                ResourceKind = WorkflowResourceKind.Recipe,
                ResourceId = Guid.NewGuid(),
                StepKey = null,
                LinkedAt = DateTimeOffset.UtcNow,
                LinkedByMembershipId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var fresh = await BodyOf(await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId)));

        Assert.Empty(fresh.GetProperty("links").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Without_confirmation_nothing_happens(bool? confirmed)
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        var runsBefore = await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct));
        var before = await StepsAndLinksAsync(oldRunId);

        var response = await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId), confirmed: confirmed);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunRestartInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("confirmed", out _));
        Assert.Equal(runsBefore, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
        Assert.Equal("InProgress", (await ReadAsync(client, oldRunId)).GetProperty("status").GetString());
        Assert.Equal(before, await StepsAndLinksAsync(oldRunId));
    }

    [Fact]
    public async Task A_retry_with_the_same_key_returns_the_same_new_run()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, oldRunId);
        var key = Guid.NewGuid().ToString();

        var first = await RestartAsync(client, oldRunId, token, key: key);
        var second = await RestartAsync(client, oldRunId, token, key: key);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader)));
        Assert.Equal((await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(second)).GetProperty("id").GetGuid());
        Assert.Equal(2, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_run_already_started_over_answers_with_its_replacement_and_never_makes_a_third()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        var replacement = (await BodyOf(await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId)))).GetProperty("id").GetGuid();

        // A new key and a token the run never held: still the same replacement, and nothing written.
        var again = await RestartAsync(client, oldRunId, NeverHeld);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(replacement, (await BodyOf(again)).GetProperty("id").GetGuid());
        Assert.Equal(2, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_start_over_that_loses_to_a_duplicate_answers_with_the_winner_and_so_does_its_retry()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, oldRunId);
        var key = Guid.NewGuid().ToString();

        _race.Arm(RaceOutcome.LostToAnotherStartOver);
        HttpResponseMessage lost;
        try
        {
            lost = await RestartAsync(client, oldRunId, token, key: key);
        }
        finally
        {
            _race.Disarm();
        }

        Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        var winner = (await BodyOf(lost)).GetProperty("id").GetGuid();
        Assert.Equal(winner, (await ReadAsync(client, oldRunId)).GetProperty("replacedByRunId").GetGuid());

        // The retry a timed-out client sends: the same answer, not an empty one.
        var retry = await RestartAsync(client, oldRunId, token, key: key);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues(IdempotencyPolicy.ReplayedHeader)));
        Assert.Equal(winner, (await BodyOf(retry)).GetProperty("id").GetGuid());
        Assert.Equal(2, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_start_over_that_loses_to_some_other_change_is_refused_with_the_token_to_look_again_from()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");

        _race.Arm(RaceOutcome.LostToAnotherChange);
        HttpResponseMessage response;
        try
        {
            response = await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId));
        }
        finally
        {
            _race.Disarm();
        }

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(body));
        Assert.Equal(await TokenAsync(client, oldRunId), body.GetProperty("currentConcurrencyToken").GetString());
        Assert.Equal(1, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_start_over_without_a_token_is_refused_even_once_the_run_was_replaced()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId));

        var response = await RestartAsync(client, oldRunId, token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunRestartInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    [Fact]
    public async Task A_key_reused_for_a_different_name_is_refused()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, oldRunId);
        var key = Guid.NewGuid().ToString();
        await RestartAsync(client, oldRunId, token, title: "Take two", key: key);

        var response = await RestartAsync(client, oldRunId, token, title: "Take three", key: key);

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(response)));
        Assert.Equal(2, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_finished_run_cannot_be_started_over()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");
        var token = await TokenAsync(client, oldRunId);
        await InWorkspaceAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(item => item.Id == oldRunId, Ct);
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var response = await RestartAsync(client, oldRunId, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunNotInProgress, Code(await BodyOf(response)));
        Assert.Equal(1, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_start_over_against_a_run_that_has_changed_is_refused()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");

        var response = await RestartAsync(client, oldRunId, NeverHeld);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunConflict, Code(await BodyOf(response)));
        Assert.Equal(1, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_start_over_without_a_key_is_refused()
    {
        using var client = await OwnerAsync();
        var oldRunId = await StartAsync(client, "images.create");

        var response = await RestartAsync(client, oldRunId, await TokenAsync(client, oldRunId), key: string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_contributor_cannot_start_someone_elses_run_over()
    {
        using var owner = await OwnerAsync();
        var oldRunId = await StartAsync(owner, "images.create");
        var email = await AddContributorAsync();
        using var contributor = await _fixture.SignInAsync(email, ContributorPassword, Ct);

        var response = await RestartAsync(contributor, oldRunId, await TokenAsync(owner, oldRunId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(await BodyOf(response)));
        Assert.Equal(1, await InWorkspaceAsync(db => db.WorkflowRuns.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_contributor_who_may_not_change_a_run_learns_nothing_of_its_replacement()
    {
        using var owner = await OwnerAsync();
        var oldRunId = await StartAsync(owner, "images.create");
        await RestartAsync(owner, oldRunId, await TokenAsync(owner, oldRunId));
        var email = await AddContributorAsync();
        using var contributor = await _fixture.SignInAsync(email, ContributorPassword, Ct);

        var response = await RestartAsync(contributor, oldRunId, NeverHeld);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(WorkflowErrorCodes.RunForbidden, Code(body));
        Assert.False(body.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Another_workspaces_run_answers_exactly_as_an_unknown_one_does()
    {
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = (await BodyOf(await ownerB.PostAsJsonAsync(
                $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/workflow-runs", new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct)))
            .GetProperty("id").GetGuid();
        using var client = await OwnerAsync();

        var foreign = await RestartAsync(client, theirs, NeverHeld);
        var unknown = await RestartAsync(client, Guid.NewGuid(), NeverHeld);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(WorkflowErrorCodes.RunNotFound, Code(foreignBody));
        Assert.True(JsonNode.DeepEquals(Comparable(await BodyOf(unknown)), Comparable(foreignBody)));

        // And their run is exactly as it was.
        var still = await BodyOf(await ownerB.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/workflow-runs/{theirs}", Ct));
        Assert.Equal("InProgress", still.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, still.GetProperty("replacedByRunId").ValueKind);
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerAsync() => _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

    /// <summary>Starts a run over. A key is generated unless one is given; pass an empty string to send none.</summary>
    private Task<HttpResponseMessage> RestartAsync(
        GatewayClient client, Guid runId, string? token, bool? confirmed = true, string? title = null, string? key = null)
    {
        var body = new { confirmed, title, expectedConcurrencyToken = token };
        var path = $"{RunsIn}/{runId}/restart";

        return key == string.Empty
            ? client.PostAsJsonAsync(path, body, Ct)
            : client.PostAsJsonAsync(path, body, key ?? Guid.NewGuid().ToString(), Ct);
    }

    private async Task<Guid> StartAsync(GatewayClient client, string definitionKey, object? startingResource = null)
    {
        var response = await client.PostAsJsonAsync(RunsIn, new { definitionKey, startingResource }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateRecipeAsync(GatewayClient client)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes", new { title = "Soda bread" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private async Task<JsonElement> ReadAsync(GatewayClient client, Guid runId) =>
        await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct));

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    /// <summary>Everything about a run's steps and links, to show a start over left them exactly as they were.</summary>
    private Task<string> StepsAndLinksAsync(Guid runId) =>
        InWorkspaceAsync(async db =>
        {
            var steps = await db.WorkflowStepRuns.AsNoTracking().Where(step => step.WorkflowRunId == runId)
                .OrderBy(step => step.StepKey).ToListAsync(Ct);
            var links = await db.WorkflowRunLinks.AsNoTracking().Where(link => link.WorkflowRunId == runId)
                .OrderBy(link => link.ResourceId).ToListAsync(Ct);

            return string.Join(",", steps.Select(step => $"{step.StepKey}:{step.Status}:{step.CompletedAt:O}:{step.DraftResourceKind}:{step.DraftResourceId}"))
                + "|" + string.Join(",", links.Select(link => $"{link.ResourceKind}:{link.ResourceId}:{link.StepKey}"));
        });

    private Task<Guid> MembershipOfAsync(string email) =>
        InWorkspaceAsync(async db =>
        {
            var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(Ct);

            return await db.WorkspaceMemberships
                .Where(membership => membership.WorkspaceId == _fixture.WorkspaceA.Id && membership.UserId == userId)
                .Select(membership => membership.Id)
                .SingleAsync(Ct);
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

    private static JsonNode Comparable(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");
        node.Remove("correlationId");

        return node;
    }

    private async Task<T> InWorkspaceAsync<T>(Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private enum RaceOutcome
    {
        LostToAnotherStartOver,
        LostToAnotherChange,
    }

    /// <summary>A switch a test arms to make the next Start over lose a race, and say to what.</summary>
    private sealed class RaceSwitch
    {
        private readonly Lock _gate = new();
        private RaceOutcome? _armed;

        public RaceOutcome? Armed
        {
            get
            {
                lock (_gate)
                {
                    return _armed;
                }
            }
        }

        public void Arm(RaceOutcome outcome)
        {
            lock (_gate)
            {
                _armed = outcome;
            }
        }

        public void Disarm()
        {
            lock (_gate)
            {
                _armed = null;
            }
        }
    }

    private sealed class RacingDataLayer(IWorkflowRunDataLayer inner, CreatorPantryDbContext context, RaceSwitch race) : IWorkflowRunDataLayer
    {
        public Task CreateAsync(
            WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps, IReadOnlyList<WorkflowRunLink> links, CancellationToken cancellationToken) =>
            inner.CreateAsync(run, steps, links, cancellationToken);

        public Task<WorkflowRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetAsync(runId, cancellationToken);

        public Task<WorkflowRunRecord?> GetForUpdateAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.GetForUpdateAsync(runId, cancellationToken);

        /// <remarks>
        /// Lost to another Start over: this one's write stands in for the winner's — it lands — and then reports
        /// stale with the tracker cleared, as the real conflict path leaves it. Lost to some other change: nothing
        /// is written at all.
        /// </remarks>
        public async Task<WorkflowRunWrite> RestartAsync(
            WorkflowRun oldRun,
            byte[] expectedRowVersion,
            WorkflowRun newRun,
            IReadOnlyList<WorkflowStepRun> newSteps,
            IReadOnlyList<WorkflowRunLink> newLinks,
            CancellationToken cancellationToken)
        {
            switch (race.Armed)
            {
                case null:
                    return await inner.RestartAsync(oldRun, expectedRowVersion, newRun, newSteps, newLinks, cancellationToken);
                case RaceOutcome.LostToAnotherStartOver:
                    await inner.RestartAsync(oldRun, expectedRowVersion, newRun, newSteps, newLinks, cancellationToken);
                    context.ChangeTracker.Clear();
                    return WorkflowRunWrite.Stale;
                default:
                    context.ChangeTracker.Clear();
                    return WorkflowRunWrite.Stale;
            }
        }

        public Task<WorkflowRunWrite> AbandonAsync(
            WorkflowRun run, byte[] expectedRowVersion, AuditEntry audit, CancellationToken cancellationToken) =>
            inner.AbandonAsync(run, expectedRowVersion, audit, cancellationToken);

        public Task<WorkflowRunWrite> SaveAsync(
            WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken, IReadOnlyList<WorkflowRunLink>? newLinks = null) =>
            inner.SaveAsync(run, expectedRowVersion, cancellationToken, newLinks);
    }
}
