using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET/PUT/POST complete/DELETE .../brand-setup-session</c> through the real Gateway: per-user privacy,
/// workspace isolation, If-Match concurrency, the Editor policy, completion rules, audit and "start over".
/// </summary>
/// <remarks>
/// Tenancy scope: tenancy.md's wider two-workspace list (search, cache, background processing, AI retrieval) is
/// N/A here. This feature has no search, cache, job or AI path, and the background identity
/// (<c>system:background</c>) has no route to a setup session today; if one is ever added it needs its own
/// isolation tests. What applies is covered below: list/detail (GET), mutation (PUT, complete, DELETE), and the
/// per-user privacy rule inside one workspace.
///
/// The row version does not move under SQLite, so a stale token is simulated with a well-formed token that was
/// never this session's; the real race belongs to <c>BrandSetupSessionSqlServerTests</c>.
/// </remarks>
public sealed class BrandSetupSessionEndpointTests : IAsyncLifetime
{
    private const string DualEmail = "voice-dual@example.com";

    private const string Password = "correct horse battery";

    private static readonly string WrongToken = Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9]);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        // One person who is an Editor in both workspaces.
        var userId = await _fixture.Api.CreateUserAsync(DualEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        foreach (var workspace in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = userId,
                Role = WorkspaceRole.Editor,
                Status = WorkspaceMembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string Route(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-setup-session";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static object Save(
        string current = "style",
        string furthest = "style",
        string[]? completed = null,
        string[]? skipped = null,
        string draft = "{\"goal\":\"warm\"}") => new
        {
            currentStep = current,
            furthestStep = furthest,
            completedSteps = completed ?? ["goals"],
            skippedSteps = skipped ?? [],
            draftJson = draft,
        };

    private static Dictionary<string, string>? IfMatch(string? token) =>
        token is null ? null : new Dictionary<string, string> { ["If-Match"] = token };

    private Task<HttpResponseMessage> PutAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? ifMatch = null) =>
        client.SendAsync(HttpMethod.Put, Route(workspace), body, IfMatch(ifMatch), Ct);

    private Task<HttpResponseMessage> CompleteAsync(GatewayClient client, SeededWorkspace workspace, string? ifMatch) =>
        client.SendAsync(HttpMethod.Post, Route(workspace) + "/complete", null, IfMatch(ifMatch), Ct);

    private Task<HttpResponseMessage> DeleteAsync(GatewayClient client, SeededWorkspace workspace) =>
        client.SendAsync(HttpMethod.Delete, Route(workspace), null, null, Ct);

    private Task<GatewayClient> Owner(SeededWorkspace workspace) => _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private Task<GatewayClient> Member(SeededWorkspace workspace) => _fixture.SignInAsync(workspace.MemberEmail, cancellationToken: Ct);

    private Task<GatewayClient> Dual() => _fixture.SignInAsync(DualEmail, Password, Ct);

    private async Task<JsonElement> CreateAsync(GatewayClient client, SeededWorkspace workspace, object? body = null)
    {
        var response = await PutAsync(client, workspace, body ?? Save());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    // ---- Read ----

    [Fact]
    public async Task A_caller_with_no_session_gets_204()
    {
        using var client = await Owner(_fixture.WorkspaceA);

        var response = await client.GetAsync(Route(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Creating_returns_201_with_the_session_and_a_matching_etag()
    {
        using var client = await Owner(_fixture.WorkspaceA);

        var response = await PutAsync(client, _fixture.WorkspaceA, Save());
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("inProgress", body.GetProperty("status").GetString());
        Assert.Equal("style", body.GetProperty("currentStep").GetString());
        Assert.Equal("goals", body.GetProperty("completedSteps")[0].GetString());
        Assert.Equal("{\"goal\":\"warm\"}", body.GetProperty("draftJson").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("completedUtc").ValueKind);
        Assert.Equal($"\"{body.GetProperty("rowVersion").GetString()}\"", response.Headers.ETag!.Tag);
        Assert.False(body.TryGetProperty("workspaceId", out _));
        Assert.False(body.TryGetProperty("userId", out _));

        var read = await client.GetAsync(Route(_fixture.WorkspaceA), Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.GetProperty("rowVersion").GetString(), (await BodyOf(read)).GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task Updating_with_the_current_token_returns_200_and_replaces_the_progress()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PutAsync(
            client,
            _fixture.WorkspaceA,
            Save("examples", "examples", ["goals", "style"], [], "{\"v\":2}"),
            created.GetProperty("rowVersion").GetString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("examples", body.GetProperty("currentStep").GetString());
        Assert.Equal("{\"v\":2}", body.GetProperty("draftJson").GetString());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    [Fact]
    public async Task The_etag_form_of_if_match_is_accepted()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PutAsync(
            client, _fixture.WorkspaceA, Save(draft: "{\"v\":3}"), $"\"{created.GetProperty("rowVersion").GetString()}\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sets_are_stored_in_wizard_order()
    {
        using var client = await Owner(_fixture.WorkspaceA);

        var body = await CreateAsync(
            client, _fixture.WorkspaceA, Save("create", "create", ["style", "goals"], ["examples"]));

        Assert.Equal(["goals", "style"], body.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()));
    }

    // ---- Concurrency ----

    [Fact]
    public async Task An_update_without_if_match_when_a_session_exists_is_a_409_and_changes_nothing()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PutAsync(client, _fixture.WorkspaceA, Save("examples", "examples", draft: "{\"lost\":true}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("brand_setup_session_conflict", Code(problem));
        Assert.True(problem.TryGetProperty("traceId", out _));
        await AssertServerStateIsOriginalAsync();
    }

    [Fact]
    public async Task A_stale_if_match_is_a_409_and_changes_nothing()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PutAsync(
            client, _fixture.WorkspaceA, Save("examples", "examples", draft: "{\"lost\":true}"), WrongToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("brand_setup_session_conflict", Code(await BodyOf(response)));
        await AssertServerStateIsOriginalAsync();
    }

    [Fact]
    public async Task A_token_against_no_session_is_a_409()
    {
        using var client = await Owner(_fixture.WorkspaceA);

        var response = await PutAsync(client, _fixture.WorkspaceA, Save(), WrongToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    private async Task AssertServerStateIsOriginalAsync()
    {
        var row = Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.AsNoTracking().ToListAsync(Ct)));
        Assert.Equal("style", row.CurrentStep);
        Assert.Equal("{\"goal\":\"warm\"}", row.DraftJson);
    }

    // ---- Validation ----

    [Theory]
    [InlineData("bogus", "style", "{}")]
    [InlineData("create", "style", "{}")]
    [InlineData("style", "style", "[1]")]
    [InlineData("style", "style", "not json")]
    public async Task Shape_errors_are_a_400_and_write_nothing(string current, string furthest, string draft)
    {
        using var client = await Owner(_fixture.WorkspaceA);

        var response = await PutAsync(client, _fixture.WorkspaceA, Save(current, furthest, draft: draft));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("brand_setup_session_invalid_request", Code(await BodyOf(response)));
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    [Fact]
    public async Task An_oversize_draft_is_a_400()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var draft = "{\"a\":\"" + new string('x', 70_000) + "\"}";

        var response = await PutAsync(client, _fixture.WorkspaceA, Save(draft: draft));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_body_cannot_name_a_workspace_or_a_user()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var other = await InScopeAsync(_fixture.WorkspaceB, db => db.WorkspaceMemberships.Select(m => m.UserId).FirstAsync(Ct));

        var response = await client.SendAsync(
            HttpMethod.Put,
            Route(_fixture.WorkspaceA),
            new
            {
                currentStep = "goals",
                furthestStep = "goals",
                completedSteps = Array.Empty<string>(),
                skippedSteps = Array.Empty<string>(),
                draftJson = "{}",
                workspaceId = _fixture.WorkspaceB.Id,
                userId = other,
                status = "completed",
            },
            null,
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
        Assert.Equal(_fixture.WorkspaceA.Id, row.WorkspaceId);
        Assert.NotEqual(other, row.UserId);
        Assert.Equal(BrandSetupSessionStatus.InProgress, row.Status);
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    // ---- Complete ----

    [Fact]
    public async Task Complete_requires_the_finish_step()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await CompleteAsync(client, _fixture.WorkspaceA, created.GetProperty("rowVersion").GetString());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("brand_setup_session_not_finished", Code(await BodyOf(response)));
        Assert.Equal(
            BrandSetupSessionStatus.InProgress,
            (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.SingleAsync(Ct))).Status);
    }

    [Fact]
    public async Task Complete_marks_the_session_completed_audits_it_and_blocks_further_saves()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var created = await CreateAsync(
            client,
            _fixture.WorkspaceA,
            Save("finish", "finish", ["goals", "style", "examples", "review-text", "create", "edit"], []));
        var token = created.GetProperty("rowVersion").GetString();

        var done = await CompleteAsync(client, _fixture.WorkspaceA, token);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = await BodyOf(done);
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("completedUtc").ValueKind);

        var save = await PutAsync(client, _fixture.WorkspaceA, Save(), body.GetProperty("rowVersion").GetString());
        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode);
        Assert.Equal("brand_setup_session_completed", Code(await BodyOf(save)));

        var actions = await InScopeAsync(
            _fixture.WorkspaceA,
            db => db.AuditLogs.Where(a => a.ResourceType == BrandAuditActions.SetupSessionResourceType)
                .Select(a => a.Action).ToListAsync(Ct));
        Assert.Contains(BrandAuditActions.SetupSessionStarted, actions);
        Assert.Contains(BrandAuditActions.SetupSessionCompleted, actions);
    }

    [Fact]
    public async Task Complete_without_or_with_a_stale_if_match_is_a_409()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, Save("finish", "finish", ["goals"], []));

        var missing = await CompleteAsync(client, _fixture.WorkspaceA, null);
        var stale = await CompleteAsync(client, _fixture.WorkspaceA, WrongToken);

        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("brand_setup_session_conflict", Code(await BodyOf(missing)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            BrandSetupSessionStatus.InProgress,
            (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.SingleAsync(Ct))).Status);
    }

    [Fact]
    public async Task Complete_with_no_session_is_a_404()
    {
        using var client = await Owner(_fixture.WorkspaceA);

        var response = await CompleteAsync(client, _fixture.WorkspaceA, WrongToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("brand_setup_session_not_found", Code(await BodyOf(response)));
    }

    [Fact]
    public async Task Completing_activates_nothing_and_changes_no_guide_or_profile()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        var (guideId, profileId) = await SeedGuideAndProfileAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, Save("finish", "finish", ["goals"], []));

        var done = await CompleteAsync(client, _fixture.WorkspaceA, created.GetProperty("rowVersion").GetString());

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandStyleGuideDefaults.ToListAsync(Ct)));
        Assert.Equal(guideId, (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandStyleGuides.SingleAsync(Ct))).Id);
        Assert.Equal(profileId, (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfiles.SingleAsync(Ct))).Id);
    }

    // ---- Delete / start over ----

    [Fact]
    public async Task Delete_removes_only_the_callers_session_and_is_idempotent()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var first = await DeleteAsync(client, _fixture.WorkspaceA);
        var second = await DeleteAsync(client, _fixture.WorkspaceA);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Route(_fixture.WorkspaceA), Ct)).StatusCode);

        var actions = await InScopeAsync(
            _fixture.WorkspaceA,
            db => db.AuditLogs.Where(a => a.ResourceType == BrandAuditActions.SetupSessionResourceType)
                .Select(a => a.Action).ToListAsync(Ct));
        Assert.Single(actions, BrandAuditActions.SetupSessionReset);

        // Starting over lets the next save create a fresh session with no token.
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(client, _fixture.WorkspaceA, Save())).StatusCode);
    }

    [Fact]
    public async Task Delete_touches_no_style_guide_profile_or_other_users_session()
    {
        using var owner = await Owner(_fixture.WorkspaceA);
        using var member = await Member(_fixture.WorkspaceA);
        var (guideId, profileId) = await SeedGuideAndProfileAsync(_fixture.WorkspaceA);
        await CreateAsync(owner, _fixture.WorkspaceA);
        await CreateAsync(member, _fixture.WorkspaceA);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(owner, _fixture.WorkspaceA)).StatusCode);

        Assert.Equal(guideId, (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandStyleGuides.SingleAsync(Ct))).Id);
        Assert.Equal(profileId, (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfiles.SingleAsync(Ct))).Id);
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(Route(_fixture.WorkspaceA), Ct)).StatusCode);
    }

    private async Task<(Guid GuideId, Guid ProfileId)> SeedGuideAndProfileAsync(SeededWorkspace workspace)
    {
        var now = DateTimeOffset.UtcNow;
        var guide = new BrandStyleGuide
        {
            Id = Guid.NewGuid(),
            DisplayName = "Everyday voice",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        };
        var profile = new BrandProfile
        {
            Id = Guid.NewGuid(),
            BrandName = "Sam's Kitchen",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        };

        await InScopeAsync(workspace, async db =>
        {
            db.BrandStyleGuides.Add(guide);
            db.BrandProfiles.Add(profile);

            return await db.SaveChangesAsync(Ct);
        });

        return (guide.Id, profile.Id);
    }

    // ---- Privacy and isolation ----

    [Fact]
    public async Task One_user_cannot_see_or_change_another_users_session_in_the_same_workspace()
    {
        using var owner = await Owner(_fixture.WorkspaceA);
        using var member = await Member(_fixture.WorkspaceA);
        var ownerSession = await CreateAsync(owner, _fixture.WorkspaceA, Save(draft: "{\"mine\":1}"));

        // The member sees nothing, and creating their own is independent.
        Assert.Equal(HttpStatusCode.NoContent, (await member.GetAsync(Route(_fixture.WorkspaceA), Ct)).StatusCode);

        // Quoting the owner's token does not let the member reach the owner's row: there is no row for them.
        var attempt = await PutAsync(
            member, _fixture.WorkspaceA, Save(draft: "{\"theirs\":1}"), ownerSession.GetProperty("rowVersion").GetString());
        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);

        var own = await CreateAsync(member, _fixture.WorkspaceA, Save(draft: "{\"theirs\":1}"));
        Assert.Equal("{\"theirs\":1}", own.GetProperty("draftJson").GetString());

        // The member's delete and complete never reach the owner's session.
        await DeleteAsync(member, _fixture.WorkspaceA);

        var ownerRead = await BodyOf(await owner.GetAsync(Route(_fixture.WorkspaceA), Ct));
        Assert.Equal("{\"mine\":1}", ownerRead.GetProperty("draftJson").GetString());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Another_users_complete_with_the_owners_token_cannot_reach_the_owners_session()
    {
        using var owner = await Owner(_fixture.WorkspaceA);
        using var member = await Member(_fixture.WorkspaceA);
        var created = await CreateAsync(
            owner, _fixture.WorkspaceA, Save("finish", "finish", ["goals", "style"], []));

        var attempt = await CompleteAsync(member, _fixture.WorkspaceA, created.GetProperty("rowVersion").GetString());

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Equal("brand_setup_session_not_found", Code(await BodyOf(attempt)));
        var row = Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.AsNoTracking().ToListAsync(Ct)));
        Assert.Equal(BrandSetupSessionStatus.InProgress, row.Status);
        Assert.Null(row.CompletedUtc);
        Assert.Equal("finish", row.CurrentStep);
    }

    [Fact]
    public async Task The_same_user_has_independent_sessions_in_two_workspaces()
    {
        using var client = await Dual();

        await CreateAsync(client, _fixture.WorkspaceA, Save(draft: "{\"w\":\"a\"}"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Route(_fixture.WorkspaceB), Ct)).StatusCode);

        var b = await CreateAsync(client, _fixture.WorkspaceB, Save("examples", "examples", ["goals", "style"], [], "{\"w\":\"b\"}"));
        Assert.Equal("{\"w\":\"b\"}", b.GetProperty("draftJson").GetString());

        var a = await BodyOf(await client.GetAsync(Route(_fixture.WorkspaceA), Ct));
        Assert.Equal("{\"w\":\"a\"}", a.GetProperty("draftJson").GetString());
        Assert.Equal("style", a.GetProperty("currentStep").GetString());

        // Deleting in A leaves B.
        await DeleteAsync(client, _fixture.WorkspaceA);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Route(_fixture.WorkspaceA), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route(_fixture.WorkspaceB), Ct)).StatusCode);

        var rowA = await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct));
        var rowB = await InScopeAsync(_fixture.WorkspaceB, db => db.BrandSetupSessions.ToListAsync(Ct));
        Assert.Empty(rowA);
        Assert.Equal(_fixture.WorkspaceB.Id, Assert.Single(rowB).WorkspaceId);
    }

    [Fact]
    public async Task Another_workspaces_route_is_indistinguishable_from_an_unknown_one()
    {
        using var inB = await Owner(_fixture.WorkspaceB);
        var b = await CreateAsync(inB, _fixture.WorkspaceB);
        using var inA = await Owner(_fixture.WorkspaceA);
        var unknown = new SeededWorkspace(Guid.NewGuid(), "no-such-workspace", "x", "", "", WorkspaceRole.Viewer);
        var token = b.GetProperty("rowVersion").GetString();

        foreach (var target in new[] { _fixture.WorkspaceB, unknown })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await inA.GetAsync(Route(target), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(inA, target, Save(), token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(inA, target, Save())).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(inA, target, token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(inA, target)).StatusCode);
        }

        // B's session is untouched.
        var row = Assert.Single(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandSetupSessions.ToListAsync(Ct)));
        Assert.Equal("{\"goal\":\"warm\"}", row.DraftJson);
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    // ---- Policy ----

    [Fact]
    public async Task A_viewer_may_read_but_not_write()
    {
        using var viewer = await Member(_fixture.WorkspaceB);
        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);

        Assert.Equal(HttpStatusCode.NoContent, (await viewer.GetAsync(Route(_fixture.WorkspaceB), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(viewer, _fixture.WorkspaceB, Save())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CompleteAsync(viewer, _fixture.WorkspaceB, WrongToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(viewer, _fixture.WorkspaceB)).StatusCode);
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandSetupSessions.ToListAsync(Ct)));
    }

    // ---- Audit ----

    [Fact]
    public async Task Audit_rows_never_carry_the_draft()
    {
        using var client = await Owner(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, Save(draft: "{\"secret\":\"never-in-audit\"}"));
        await DeleteAsync(client, _fixture.WorkspaceA);

        var rows = await InScopeAsync(
            _fixture.WorkspaceA,
            db => db.AuditLogs.Where(a => a.ResourceType == BrandAuditActions.SetupSessionResourceType).ToListAsync(Ct));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.DoesNotContain("never-in-audit", JsonSerializer.Serialize(row)));
    }
}
