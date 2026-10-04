using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApplicationUser = CreatorPantry.Domain.Modules.Auth.Data.Entities.ApplicationUser;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET/PUT/DELETE .../brand-style-guides/{guideId}/edit-session</c> through the real Gateway: the editor's
/// autosave target. What a draft keeps, who can see it, what makes it stale, how two tabs are kept apart, and
/// that saving a version clears it.
/// </summary>
public sealed class BrandStyleGuideEditSessionEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "guide-draft-contributor-a@example.com";

    private const string SecondEditorEmail = "guide-draft-editor-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await JoinAsync(ContributorEmail, _fixture.WorkspaceA, WorkspaceRole.Contributor);
        await JoinAsync(SecondEditorEmail, _fixture.WorkspaceA, WorkspaceRole.Editor);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- What a draft keeps ----

    [Fact]
    public async Task A_creator_with_nothing_kept_has_no_draft()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);

        var response = await GetAsync(client, _fixture.WorkspaceA, guide);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_first_save_creates_the_draft_and_hands_back_the_token_the_next_one_quotes()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain."));
        var saved = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(guide, saved.GetProperty("guideId").GetGuid());
        Assert.Equal(1, saved.GetProperty("baselineVersionNumber").GetInt32());
        Assert.Equal(1, saved.GetProperty("workingVersionNumber").GetInt32());
        Assert.False(saved.GetProperty("isStale").GetBoolean());
        Assert.NotEmpty(saved.GetProperty("rowVersion").GetString()!);
        Assert.Equal($"\"{saved.GetProperty("rowVersion").GetString()}\"", response.Headers.ETag?.Tag);
    }

    /// <summary>Opaque means opaque: what comes back is the string that was sent, not a re-serialisation.</summary>
    [Fact]
    public async Task A_draft_is_stored_exactly_as_sent()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        const string draft = "{\"sections\":[{\"sectionKey\":\"Voice\",\"body\":\"  Plain.  \"}],\"ui\":{\"open\":\"Voice\"}}";

        await SaveAsync(client, _fixture.WorkspaceA, guide, new { baselineVersionNumber = 1, draftJson = draft });
        var read = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guide));

        Assert.Equal(draft, read.GetProperty("draftJson").GetString());
    }

    [Fact]
    public async Task A_later_save_quoting_the_token_replaces_the_draft()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        var first = await BodyOf(await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain.")));

        var response = await SaveAsync(
            client, _fixture.WorkspaceA, guide, Draft(1, "A friend who cooks."), first.GetProperty("rowVersion").GetString());
        var saved = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A friend who cooks.", saved.GetProperty("draftJson").GetString()!);
        // One row, replaced rather than added to: a draft is where the creator is, not a history of where
        // they have been. The token itself is not asserted to have moved — under SQLite a native row version
        // does not change on update (see SqliteModelCustomizer), so that belongs to the SQL Server tests.
        Assert.Equal(1, await DraftCountAsync(_fixture.WorkspaceA));
        _ = first;
    }

    // ---- Staleness ----

    /// <summary>
    /// A draft is never refused for being behind: the creator's words are kept and the reply says the guide
    /// moved, which is what lets the editor warn them before they try to save.
    /// </summary>
    [Fact]
    public async Task A_draft_whose_guide_has_gained_a_version_is_kept_and_reported_stale()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);

        // Version 2 arrives first, so what follows is a draft naming version 1 against a guide that has moved
        // past it — an editor opened before the save and typed into afterwards.
        await WriteVersionAsync(client, _fixture.WorkspaceA, guide, 1);

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain, still."));
        var saved = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(saved.GetProperty("isStale").GetBoolean());
        Assert.Equal(1, saved.GetProperty("baselineVersionNumber").GetInt32());
        Assert.Equal(2, saved.GetProperty("workingVersionNumber").GetInt32());

        // Kept in full, which is the point: a draft is never refused for being behind.
        Assert.Contains("Plain, still.", saved.GetProperty("draftJson").GetString()!);
        Assert.True((await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guide))).GetProperty("isStale").GetBoolean());
    }

    [Fact]
    public async Task A_draft_named_against_the_working_version_is_not_stale()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        await WriteVersionAsync(client, _fixture.WorkspaceA, guide, 1);

        var saved = await BodyOf(await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(2, "Plain.")));

        Assert.False(saved.GetProperty("isStale").GetBoolean());
        Assert.Equal(2, saved.GetProperty("workingVersionNumber").GetInt32());
    }

    // ---- Two tabs ----

    [Fact]
    public async Task A_save_with_no_token_once_a_draft_exists_is_refused_and_changes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain."));

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Clobbered."));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideEditSessionConflict, Code(await BodyOf(response)));

        var read = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guide));
        Assert.Contains("Plain.", read.GetProperty("draftJson").GetString()!);
    }

    /// <summary>
    /// A token that is not this draft's. The spent-token case — one a save really has moved past — needs a
    /// server that bumps a row version on update, so it is proved in <c>BrandGuideSqlServerTests</c>.
    /// </summary>
    [Fact]
    public async Task A_save_quoting_a_token_that_is_not_the_drafts_is_refused_and_changes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain."));

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Clobbered."), WrongToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideEditSessionConflict, Code(await BodyOf(response)));

        var read = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guide));
        Assert.Contains("Plain.", read.GetProperty("draftJson").GetString()!);
    }

    /// <summary>A token for a row that is gone: discarded in another tab, or cleared by a version write.</summary>
    [Fact]
    public async Task A_save_quoting_a_token_for_a_discarded_draft_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        var first = await BodyOf(await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain.")));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, guide)).StatusCode);

        var response = await SaveAsync(
            client, _fixture.WorkspaceA, guide, Draft(1, "Back again."), first.GetProperty("rowVersion").GetString());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await DraftCountAsync(_fixture.WorkspaceA));
    }

    // ---- Discarding ----

    [Fact]
    public async Task Discarding_removes_the_draft_and_discarding_nothing_is_still_a_success()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain."));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, guide)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(client, _fixture.WorkspaceA, guide)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, guide)).StatusCode);
    }

    /// <summary>Only the draft: the guide and every version it has are the creator's record and stay.</summary>
    [Fact]
    public async Task Discarding_a_draft_leaves_the_guide_and_its_versions_alone()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        await WriteVersionAsync(client, _fixture.WorkspaceA, guide, 1);
        await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(2, "Plain."));

        await DeleteAsync(client, _fixture.WorkspaceA, guide);

        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
        Assert.Equal(HttpStatusCode.OK, (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).StatusCode);
    }

    // ---- Saving a version clears it ----

    [Fact]
    public async Task Writing_a_version_clears_the_callers_own_draft()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain."));

        await WriteVersionAsync(client, _fixture.WorkspaceA, guide, 1);

        // "Saved as version 2" and "you have unsaved changes" must not both be true.
        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(client, _fixture.WorkspaceA, guide)).StatusCode);
    }

    /// <summary>
    /// Somebody else saving a version is not a reason to throw away a second creator's words: their draft
    /// stays, and the staleness they are told about is what the editor warns them with.
    /// </summary>
    [Fact]
    public async Task Writing_a_version_leaves_another_creators_draft_where_it_is()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceA);

        using var editor = await _fixture.SignInAsync(
            SecondEditorEmail, Password, TestContext.Current.CancellationToken);
        await SaveAsync(editor, _fixture.WorkspaceA, guide, Draft(1, "Mine."));
        await SaveAsync(owner, _fixture.WorkspaceA, guide, Draft(1, "Theirs."));

        await WriteVersionAsync(owner, _fixture.WorkspaceA, guide, 1);

        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(owner, _fixture.WorkspaceA, guide)).StatusCode);

        var theirs = await BodyOf(await GetAsync(editor, _fixture.WorkspaceA, guide));
        Assert.Contains("Mine.", theirs.GetProperty("draftJson").GetString()!);
        Assert.True(theirs.GetProperty("isStale").GetBoolean());
    }

    [Fact]
    public async Task Two_creators_editing_one_guide_keep_separate_drafts()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceA);

        using var editor = await _fixture.SignInAsync(
            SecondEditorEmail, Password, TestContext.Current.CancellationToken);

        await SaveAsync(owner, _fixture.WorkspaceA, guide, Draft(1, "Theirs."));
        await SaveAsync(editor, _fixture.WorkspaceA, guide, Draft(1, "Mine."));

        Assert.Contains("Theirs.", (await BodyOf(await GetAsync(owner, _fixture.WorkspaceA, guide)))
            .GetProperty("draftJson").GetString()!);
        Assert.Contains("Mine.", (await BodyOf(await GetAsync(editor, _fixture.WorkspaceA, guide)))
            .GetProperty("draftJson").GetString()!);
        Assert.Equal(2, await DraftCountAsync(_fixture.WorkspaceA));
    }

    // ---- Shape ----

    [Fact]
    public async Task Shape_failures_answer_400_and_keep_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        var tooBig = "{\"a\":\"" + new string('x', BrandStyleGuideEditSessionDraft.MaxBytes) + "\"}";

        var cases = new (string Field, object Body)[]
        {
            ("baselineVersionNumber", new { draftJson = "{}" }),
            ("baselineVersionNumber", new { baselineVersionNumber = 0, draftJson = "{}" }),
            ("draftJson", new { baselineVersionNumber = 1 }),
            ("draftJson", new { baselineVersionNumber = 1, draftJson = "not json" }),
            ("draftJson", new { baselineVersionNumber = 1, draftJson = "[]" }),
            ("draftJson", new { baselineVersionNumber = 1, draftJson = tooBig }),
        };

        foreach (var (field, body) in cases)
        {
            var response = await SaveAsync(client, _fixture.WorkspaceA, guide, body);
            var problem = await BodyOf(response);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(BrandErrorCodes.GuideInvalidRequest, Code(problem));
            Assert.True(
                problem.GetProperty("errors").EnumerateObject()
                    .Any(error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase)),
                $"no error for {field}: {problem.GetProperty("errors").GetRawText()}");
        }

        Assert.Equal(0, await DraftCountAsync(_fixture.WorkspaceA));
    }

    // ---- Nothing is audited ----

    /// <summary>
    /// Autosave writes a row every few seconds; a row per keystroke would bury the entries that record what
    /// the workspace actually decided. What a creator keeps is a version, and that write is audited.
    /// </summary>
    [Fact]
    public async Task Keeping_and_discarding_a_draft_write_no_audit_entries()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA);
        var before = await AuditCountAsync(_fixture.WorkspaceA);

        var first = await BodyOf(await SaveAsync(client, _fixture.WorkspaceA, guide, Draft(1, "Plain.")));
        await SaveAsync(
            client, _fixture.WorkspaceA, guide, Draft(1, "Again."), first.GetProperty("rowVersion").GetString());
        await DeleteAsync(client, _fixture.WorkspaceA, guide);

        Assert.Equal(before, await AuditCountAsync(_fixture.WorkspaceA));
    }

    // ---- Policy and isolation ----

    [Fact]
    public async Task A_contributor_can_neither_read_nor_keep_a_draft()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(
            ContributorEmail, Password, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(contributor, _fixture.WorkspaceA, guide)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SaveAsync(contributor, _fixture.WorkspaceA, guide, Draft(1, "Mine now."))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(contributor, _fixture.WorkspaceA, guide)).StatusCode);
        Assert.Equal(0, await DraftCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Another_workspaces_guide_has_no_draft_to_read_keep_or_discard()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(ownerA, _fixture.WorkspaceA);
        await SaveAsync(ownerA, _fixture.WorkspaceA, guide, Draft(1, "Plain."));

        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        foreach (var response in new[]
        {
            await GetAsync(ownerB, _fixture.WorkspaceB, guide),
            await SaveAsync(ownerB, _fixture.WorkspaceB, guide, Draft(1, "Ours now.")),
            await DeleteAsync(ownerB, _fixture.WorkspaceB, guide),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(BrandErrorCodes.GuideNotFound, Code(await BodyOf(response)));
        }

        // A's draft is untouched, and B has none.
        Assert.Equal(1, await DraftCountAsync(_fixture.WorkspaceA));
        Assert.Equal(0, await DraftCountAsync(_fixture.WorkspaceB));
    }

    /// <summary>
    /// One creator in two workspaces: the drafts are separate rows, and neither route can reach the other's.
    /// </summary>
    [Fact]
    public async Task A_draft_is_scoped_to_its_workspace_as_well_as_its_guide_and_creator()
    {
        await JoinAsync(_fixture.WorkspaceA.OwnerEmail, _fixture.WorkspaceB, WorkspaceRole.Editor);

        using var creator = await OwnerOf(_fixture.WorkspaceA);
        var here = await CreateGuideAsync(creator, _fixture.WorkspaceA);
        var there = await CreateGuideAsync(creator, _fixture.WorkspaceB);

        await SaveAsync(creator, _fixture.WorkspaceA, here, Draft(1, "Here."));
        await SaveAsync(creator, _fixture.WorkspaceB, there, Draft(1, "There."));

        Assert.Contains("Here.", (await BodyOf(await GetAsync(creator, _fixture.WorkspaceA, here)))
            .GetProperty("draftJson").GetString()!);
        Assert.Contains("There.", (await BodyOf(await GetAsync(creator, _fixture.WorkspaceB, there)))
            .GetProperty("draftJson").GetString()!);

        // B's guide named under A's slug is not A's guide, whoever asks.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(creator, _fixture.WorkspaceA, there)).StatusCode);
        Assert.Equal(1, await DraftCountAsync(_fixture.WorkspaceA));
        Assert.Equal(1, await DraftCountAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task An_unknown_guide_has_no_draft()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await GetAsync(client, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideNotFound, Code(await BodyOf(response)));
    }

    // ---- helpers ----

    private static string SessionIn(SeededWorkspace workspace, Guid guideId) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides/{guideId}/edit-session";

    private static string GuidesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>A well-formed token that belongs to no draft.</summary>
    private const string WrongToken = "AAAAAAAAB9E=";

    /// <summary>A draft naming one section, which is all these tests need it to carry.</summary>
    private static object Draft(int baselineVersionNumber, string body) => new
    {
        baselineVersionNumber,
        draftJson = $"{{\"sections\":[{{\"sectionKey\":\"Voice\",\"body\":\"{body}\"}}]}}",
    };

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> GetAsync(GatewayClient client, SeededWorkspace workspace, Guid guideId) =>
        client.GetAsync(SessionIn(workspace, guideId), TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> SaveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, object body, string? ifMatch = null) =>
        client.SendAsync(
            HttpMethod.Put,
            SessionIn(workspace, guideId),
            body,
            ifMatch is null ? null : new Dictionary<string, string> { ["If-Match"] = $"\"{ifMatch}\"" },
            TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> DeleteAsync(GatewayClient client, SeededWorkspace workspace, Guid guideId) =>
        client.SendAsync(
            HttpMethod.Delete, SessionIn(workspace, guideId), null, null, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ReadGuideAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId) =>
        client.GetAsync($"{GuidesIn(workspace)}/{guideId}", TestContext.Current.CancellationToken);

    private async Task<Guid> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace),
            new { displayName = "House voice", sections = new[] { new { sectionKey = "Voice", body = "Plain." } } },
            Key(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Writes a further version through the ordinary edit route, which is also what clears a draft.</summary>
    private async Task WriteVersionAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int expectedWorkingVersionNumber)
    {
        var response = await client.PostAsJsonAsync(
            $"{GuidesIn(workspace)}/{guideId}/versions",
            new
            {
                expectedWorkingVersionNumber,
                sections = new[] { new { sectionKey = "Tone", body = $"Warm {Guid.NewGuid():N}." } },
            },
            Key(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Gives an existing user a membership in a workspace, so roles and two-workspace cases are stated.</summary>
    private async Task JoinAsync(string email, SeededWorkspace workspace, WorkspaceRole role)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email) ?? await Created(email);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        async Task<ApplicationUser> Created(string address)
        {
            await _fixture.Api.CreateUserAsync(address, Password);

            return (await users.FindByEmailAsync(address))!;
        }
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private Task<int> DraftCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideEditSessions.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> VersionCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideVersions.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> AuditCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
}
