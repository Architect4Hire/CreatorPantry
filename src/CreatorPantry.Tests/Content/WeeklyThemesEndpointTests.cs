using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// <c>GET</c>, <c>PUT</c> and <c>DELETE .../weekly-themes</c> through the real Gateway: the contract, the two
/// role bars, idempotency, and the boundary between two workspaces.
/// </summary>
public sealed class WeeklyThemesEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ThemesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/weekly-themes";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static JsonElement[] Themes(JsonElement body) => [.. body.GetProperty("themes").EnumerateArray()];

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static object Monday(string key = "meat-free-monday", string name = "Meat-free Monday") =>
        new { day = "Monday", key, displayName = name };

    private static Task<HttpResponseMessage> PutAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? idempotencyKey = null) =>
        client.SendAsync(
            HttpMethod.Put,
            ThemesIn(workspace),
            body,
            idempotencyKey is null ? null : new Dictionary<string, string> { [IdempotencyPolicy.KeyHeader] = idempotencyKey },
            Ct);

    private static Task<HttpResponseMessage> DeleteAsync(
        GatewayClient client, SeededWorkspace workspace, string key) =>
        client.SendAsync(HttpMethod.Delete, $"{ThemesIn(workspace)}/{key}", body: null, headers: null, Ct);

    [Fact]
    public async Task A_workspace_with_no_themes_answers_an_empty_week()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(ThemesIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Empty(Themes(await BodyOf(response)));
    }

    [Fact]
    public async Task An_editor_replaces_the_week_and_a_read_returns_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PutAsync(client, _fixture.WorkspaceA, new
        {
            themes = new object[]
            {
                new { day = "Friday", key = "fakeaway-friday", displayName = "Fakeaway Friday" },
                new { day = "Monday", key = "meat-free-monday", displayName = "  Meat-free Monday  ", description = "No meat." },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Themes(await BodyOf(response));

        // Monday first, and the creator's words trimmed but otherwise untouched.
        Assert.Equal(["meat-free-monday", "fakeaway-friday"], written.Select(theme => theme.GetProperty("key").GetString()));
        Assert.Equal("Meat-free Monday", written[0].GetProperty("displayName").GetString());
        Assert.Equal("No meat.", written[0].GetProperty("description").GetString());
        Assert.Equal("Monday", written[0].GetProperty("day").GetString());
        Assert.Equal(JsonValueKind.Null, written[0].GetProperty("retiredAt").ValueKind);

        var read = Themes(await BodyOf(await client.GetAsync(ThemesIn(_fixture.WorkspaceA), Ct)));
        Assert.Equal(2, read.Length);
    }

    [Fact]
    public async Task Leaving_a_theme_out_retires_it_rather_than_deleting_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await PutAsync(client, _fixture.WorkspaceA, new
        {
            themes = new object[] { Monday(), new { day = "Tuesday", key = "taco-tuesday", displayName = "Taco Tuesday" } },
        });

        var body = await BodyOf(await PutAsync(client, _fixture.WorkspaceA, new { themes = new[] { Monday() } }));

        var themes = Themes(body);
        Assert.Equal(["meat-free-monday", "taco-tuesday"], themes.Select(theme => theme.GetProperty("key").GetString()));
        Assert.Equal(JsonValueKind.Null, themes[0].GetProperty("retiredAt").ValueKind);
        Assert.Equal(JsonValueKind.String, themes[1].GetProperty("retiredAt").ValueKind);

        // Still the creator's theme, still named, still on its day — which is what a stored key resolves to.
        Assert.Equal("Taco Tuesday", themes[1].GetProperty("displayName").GetString());
        Assert.Equal("Tuesday", themes[1].GetProperty("day").GetString());
    }

    [Fact]
    public async Task An_empty_list_clears_the_week_without_losing_a_key()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await PutAsync(client, _fixture.WorkspaceA, new { themes = new[] { Monday() } });

        var body = await BodyOf(await PutAsync(client, _fixture.WorkspaceA, new { themes = Array.Empty<object>() }));

        var theme = Assert.Single(Themes(body));
        Assert.Equal("meat-free-monday", theme.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.String, theme.GetProperty("retiredAt").ValueKind);
    }

    [Fact]
    public async Task A_malformed_week_is_refused_with_field_errors()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PutAsync(client, _fixture.WorkspaceA, new
        {
            themes = new object[]
            {
                new { day = "Monday", key = "Meat Free Monday", displayName = "" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.WeeklyThemesInvalid, Code(body));
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("themes[0].Key", out _));
        Assert.True(errors.TryGetProperty("themes[0].DisplayName", out _));
    }

    [Fact]
    public async Task A_day_that_is_not_a_day_never_reaches_the_facade()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PutAsync(client, _fixture.WorkspaceA, new
        {
            themes = new object[] { new { day = "Someday", key = "someday", displayName = "Someday" } },
        });

        // The enum converter fails the body before binding completes, so this is the malformed-request shape
        // rather than a field error — the same answer any unparseable JSON gets.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_change_the_week()
    {
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await PutAsync(viewer, _fixture.WorkspaceB, new { themes = new[] { Monday() } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Themes(await BodyOf(await viewer.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct))));
    }

    [Fact]
    public async Task An_editor_can_replace_but_not_delete()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await PutAsync(owner, _fixture.WorkspaceA, new { themes = new[] { Monday() } });

        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var refused = await DeleteAsync(editor, _fixture.WorkspaceA, "meat-free-monday");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Single(Themes(await BodyOf(await editor.GetAsync(ThemesIn(_fixture.WorkspaceA), Ct))));
    }

    [Fact]
    public async Task An_owner_deletes_a_theme_and_its_key_stops_resolving()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await PutAsync(client, _fixture.WorkspaceA, new
        {
            themes = new object[] { Monday(), new { day = "Tuesday", key = "taco-tuesday", displayName = "Taco Tuesday" } },
        });

        var response = await DeleteAsync(client, _fixture.WorkspaceA, "taco-tuesday");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var remaining = Assert.Single(Themes(await BodyOf(response)));
        Assert.Equal("meat-free-monday", remaining.GetProperty("key").GetString());

        // Gone, so a second delete is the same answer a key that never existed gets.
        var again = await DeleteAsync(client, _fixture.WorkspaceA, "taco-tuesday");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, Code(await BodyOf(again)));
    }

    [Fact]
    public async Task Deleting_a_theme_this_workspace_does_not_have_is_not_found()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await PutAsync(ownerB, _fixture.WorkspaceB, new
        {
            themes = new object[] { new { day = "Tuesday", key = "taco-tuesday", displayName = "B's Taco Tuesday" } },
        });

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(ownerA, _fixture.WorkspaceA, "taco-tuesday");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, Code(await BodyOf(response)));

        // And B still has it: one workspace's 404 is not the other's deletion.
        Assert.Single(Themes(await BodyOf(await ownerB.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct))));
    }

    [Fact]
    public async Task Each_workspace_keeps_its_own_week_under_the_same_keys()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        await PutAsync(ownerA, _fixture.WorkspaceA, new { themes = new[] { Monday(name: "A's Monday") } });
        await PutAsync(ownerB, _fixture.WorkspaceB, new { themes = new[] { Monday(name: "B's Monday") } });

        Assert.Equal(
            "A's Monday",
            Assert.Single(Themes(await BodyOf(await ownerA.GetAsync(ThemesIn(_fixture.WorkspaceA), Ct))))
                .GetProperty("displayName").GetString());
        Assert.Equal(
            "B's Monday",
            Assert.Single(Themes(await BodyOf(await ownerB.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct))))
                .GetProperty("displayName").GetString());

        // A member of A cannot read B's week at all: the tenancy 404 lands before the action runs.
        var crossing = await ownerA.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct);
        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_write_to_the_others_route()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await PutAsync(ownerB, _fixture.WorkspaceB, new { themes = new[] { Monday(name: "B's Monday") } });

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // The tenancy 404 lands before the action runs, so a write never reaches B's week.
        var written = await PutAsync(ownerA, _fixture.WorkspaceB, new { themes = new[] { Monday(name: "A's edit") } });
        var deleted = await DeleteAsync(ownerA, _fixture.WorkspaceB, "meat-free-monday");

        Assert.Equal(HttpStatusCode.NotFound, written.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        var theme = Assert.Single(Themes(await BodyOf(await ownerB.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct))));
        Assert.Equal("B's Monday", theme.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, theme.GetProperty("retiredAt").ValueKind);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_indistinguishable_from_one_the_caller_cannot_reach()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var inaccessible = await ownerA.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct);
        var nonexistent = await ownerA.GetAsync("/api/v1/workspaces/no-such-workspace/weekly-themes", Ct);

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
        Assert.Equal(Code(await BodyOf(inaccessible)), Code(await BodyOf(nonexistent)));
    }

    [Fact]
    public async Task Deleting_a_key_both_workspaces_hold_touches_only_the_callers()
    {
        // The sharpest isolation case in this feature: the repository looks a theme up by key alone, so only the
        // global query filter stands between a delete in A and the identically keyed theme in B.
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await PutAsync(ownerA, _fixture.WorkspaceA, new { themes = new[] { Monday(name: "A's Monday") } });
        await PutAsync(ownerB, _fixture.WorkspaceB, new { themes = new[] { Monday(name: "B's Monday") } });

        var response = await DeleteAsync(ownerA, _fixture.WorkspaceA, "meat-free-monday");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Themes(await BodyOf(response)));

        var survivor = Assert.Single(Themes(await BodyOf(await ownerB.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct))));
        Assert.Equal("B's Monday", survivor.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, survivor.GetProperty("retiredAt").ValueKind);
    }

    [Fact]
    public async Task A_workspace_id_in_the_body_is_ignored()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.SendAsync(
            HttpMethod.Put,
            ThemesIn(_fixture.WorkspaceA),
            new { workspaceId = _fixture.WorkspaceB.Id, themes = new[] { Monday() } },
            new Dictionary<string, string> { ["X-Workspace-Id"] = _fixture.WorkspaceB.Id.ToString("D") },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Written to A, where the route said, and B is untouched.
        Assert.Single(Themes(await BodyOf(await client.GetAsync(ThemesIn(_fixture.WorkspaceA), Ct))));

        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        Assert.Empty(Themes(await BodyOf(await ownerB.GetAsync(ThemesIn(_fixture.WorkspaceB), Ct))));
    }

    [Fact]
    public async Task An_idempotency_key_is_scoped_to_one_workspace()
    {
        // The same user cannot exist in both fixtures' workspaces, so this is the same *key* rather than the same
        // caller — which is the part worth proving: the scope carries the workspace, so A's stored result can
        // never be replayed as B's.
        var key = Guid.NewGuid().ToString("N");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var first = await PutAsync(ownerA, _fixture.WorkspaceA, new { themes = new[] { Monday(name: "A's Monday") } }, key);
        var second = await PutAsync(ownerB, _fixture.WorkspaceB, new { themes = new[] { Monday(name: "B's Monday") } }, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(
            "B's Monday",
            Assert.Single(Themes(await BodyOf(second))).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_repeated_replace_with_the_same_key_is_replayed_rather_than_reapplied()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString("N");
        var body = new { themes = new[] { Monday() } };

        var first = await PutAsync(client, _fixture.WorkspaceA, body, key);
        var second = await PutAsync(client, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        var theme = Assert.Single(Themes(await BodyOf(second)));
        Assert.Equal(1, theme.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task A_replace_is_audited_against_the_workspace_that_made_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await PutAsync(client, _fixture.WorkspaceA, new { themes = new[] { Monday() } });

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var entry = await db.AuditLogs.SingleAsync(log => log.Action == ContentAuditActions.WeeklyThemesReplaced, Ct);
        Assert.Equal(_fixture.WorkspaceA.Id, entry.WorkspaceId);
        Assert.Equal(_fixture.WorkspaceA.Id.ToString("D"), entry.ResourceId);
        Assert.Equal("1", entry.AfterReference);
    }

    [Fact]
    public async Task One_workspaces_audit_trail_does_not_show_the_others_replace()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await PutAsync(ownerA, _fixture.WorkspaceA, new { themes = new[] { Monday() } });
        await PutAsync(ownerB, _fixture.WorkspaceB, new { themes = new[] { Monday() } });

        // A refused cross-workspace delete writes nothing at all, so B's trail stays at its own one entry.
        await DeleteAsync(ownerA, _fixture.WorkspaceB, "meat-free-monday");

        foreach (var workspace in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
        {
            await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
                workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

            var entry = Assert.Single(await db.AuditLogs
                .Where(log => log.Action == ContentAuditActions.WeeklyThemesReplaced)
                .ToListAsync(Ct));
            Assert.Equal(workspace.Id, entry.WorkspaceId);
            Assert.Empty(await db.AuditLogs
                .Where(log => log.Action == ContentAuditActions.WeeklyThemeDeleted)
                .ToListAsync(Ct));
        }
    }
}
