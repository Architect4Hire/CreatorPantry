using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>POST</c> and <c>PATCH .../brand-profile</c> through the real Gateway: submitted-field semantics,
/// validation, revision and audit records, concurrency, the Editor policy, idempotency and workspace isolation.
/// </summary>
/// <remarks>
/// The row version does not move under SQLite (<c>SqliteModelCustomizer</c>), so a stale token is simulated with
/// a well-formed token that was never this profile's. That proves the comparison and the 409; the race itself is
/// the data layer's two-guard logic, which needs a real database.
/// </remarks>
public sealed class BrandProfileWriteEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "brand-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        var userId = await _fixture.Api.CreateUserAsync(ContributorEmail, Password);
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
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string BrandIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-profile";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static readonly string WrongToken = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private async Task<JsonElement> CreateAsync(GatewayClient client, SeededWorkspace workspace, object? body = null)
    {
        var response = await client.PostAsJsonAsync(
            BrandIn(workspace),
            body ?? new { brandName = "Sam's Kitchen", shortDescription = "Weeknight cooking.", locale = "en-US" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private async Task<HttpResponseMessage> PatchAsync(
        GatewayClient client, SeededWorkspace workspace, JsonElement current, Dictionary<string, object?> fields)
    {
        fields["expectedConcurrencyToken"] = current.GetProperty("concurrencyToken").GetString();

        return await client.PatchAsJsonAsync(BrandIn(workspace), fields, TestContext.Current.CancellationToken);
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    // ---- Create ----

    [Fact]
    public async Task Creating_returns_the_profile_at_revision_one_with_a_location()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new
            {
                brandName = "  Sam's Kitchen  ",
                timeZoneId = "America/Chicago",
                channelDefaults = new[] { new { channelKey = "instagram" }, new { channelKey = "tiktok" } },
                links = new[] { new { kind = "Website", url = "https://example.com", label = "Home" } },
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith(BrandIn(_fixture.WorkspaceA), response.Headers.Location!.ToString());

        var body = await BodyOf(response);
        Assert.Equal("Sam's Kitchen", body.GetProperty("brandName").GetString());
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.Equal(2, body.GetProperty("channelDefaults").GetArrayLength());

        // And a read returns the same thing.
        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));
        Assert.Equal(body.GetProperty("id").GetGuid(), read.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Creating_writes_a_revision_and_an_audit_entry_owned_by_the_workspace()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var id = created.GetProperty("id").GetGuid();

        var revisions = await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfileRevisions.ToListAsync(cancellation));
        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.Revision);
        Assert.Equal(id, revision.BrandProfileId);
        Assert.Contains("Sam's Kitchen", revision.Document);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .SingleAsync(log => log.ResourceId == id.ToString("D"), cancellation);
        Assert.Equal(BrandAuditActions.Created, audit.Action);
        Assert.Equal(_fixture.WorkspaceA.Id, audit.WorkspaceId);
        Assert.Equal("1", audit.AfterReference);
    }

    [Fact]
    public async Task A_second_create_conflicts_and_leaves_the_first_alone()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var first = await CreateAsync(client, _fixture.WorkspaceA);

        var second = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), new { brandName = "Another" }, cancellation);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(BrandErrorCodes.AlreadyExistsConflict, Code(await BodyOf(second)));

        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));
        Assert.Equal(first.GetProperty("brandName").GetString(), read.GetProperty("brandName").GetString());
    }

    [Fact]
    public async Task Create_rejects_a_missing_name_with_a_field_error()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA), new { shortDescription = "No name." }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.InvalidRequest, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("brandName", out _));
    }

    [Fact]
    public async Task An_unknown_time_zone_is_refused_on_create_and_update()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var create = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA), new { brandName = "B", timeZoneId = "Mars/Olympus" }, cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.True((await BodyOf(create)).GetProperty("errors").TryGetProperty("timeZoneId", out _));

        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var update = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["timeZoneId"] = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Logo_links_are_refused_until_the_media_seam_can_verify_them()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var asset = new[] { new { mediaAssetId = Guid.NewGuid(), role = "PrimaryLogo" } };

        var create = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), new { brandName = "B", assets = asset }, cancellation);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal(BrandErrorCodes.AssetsUnprocessable, Code(await BodyOf(create)));

        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var update = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["assets"] = asset });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);

        // Nothing was stored: the profile and its history are exactly as created.
        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
        Assert.Equal(0, read.GetProperty("assets").GetArrayLength());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfileRevisions.ToListAsync(cancellation)));
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandAssetLinks.ToListAsync(cancellation)));

        // An explicit empty list is fine.
        var cleared = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["assets"] = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    // ---- Update: submitted-field semantics ----

    [Fact]
    public async Task A_patch_changes_only_what_it_mentions()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["defaultAudience"] = "Busy home cooks" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Busy home cooks", body.GetProperty("defaultAudience").GetString());
        Assert.Equal("Sam's Kitchen", body.GetProperty("brandName").GetString());
        Assert.Equal("Weeknight cooking.", body.GetProperty("shortDescription").GetString());
        Assert.Equal("en-US", body.GetProperty("locale").GetString());
        Assert.Equal(2, body.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task A_field_sent_as_null_is_cleared_and_one_left_out_is_not()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["shortDescription"] = null }));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("shortDescription").ValueKind);
        Assert.Equal("en-US", body.GetProperty("locale").GetString());
    }

    [Fact]
    public async Task The_name_cannot_be_cleared()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["brandName"] = null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("brandName", out _));
    }

    [Fact]
    public async Task Lists_replace_as_a_whole_in_the_order_sent_and_empty_clears()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            brandName = "B",
            channelDefaults = new[] { new { channelKey = "instagram" }, new { channelKey = "tiktok" } },
            links = new[] { new { kind = "Website", url = "https://example.com" } },
        });

        var replaceResponse = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["channelDefaults"] = new[] { new { channelKey = "tiktok" }, new { channelKey = "pinterest" } },
        });
        Assert.True(replaceResponse.IsSuccessStatusCode, await replaceResponse.Content.ReadAsStringAsync());
        var replaced = await BodyOf(replaceResponse);

        Assert.Equal(
            ["tiktok", "pinterest"],
            replaced.GetProperty("channelDefaults").EnumerateArray().Select(c => c.GetProperty("channelKey").GetString()));
        Assert.Equal(1, replaced.GetProperty("links").GetArrayLength());

        var cleared = await BodyOf(await PatchAsync(client, _fixture.WorkspaceA, replaced, new() { ["links"] = null }));
        Assert.Equal(0, cleared.GetProperty("links").GetArrayLength());
        Assert.Equal(2, cleared.GetProperty("channelDefaults").GetArrayLength());
    }

    [Fact]
    public async Task A_patch_that_changes_nothing_writes_no_revision_and_keeps_the_token()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["brandName"] = "  Sam's Kitchen  ",
            ["locale"] = "en-US",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.Equal(
            created.GetProperty("concurrencyToken").GetString(), body.GetProperty("concurrencyToken").GetString());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfileRevisions.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task Reordering_a_list_is_a_change_and_survives_the_unique_indexes()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            brandName = "B",
            channelDefaults = new[] { new { channelKey = "instagram" }, new { channelKey = "pinterest" } },
            links = new[]
            {
                new { kind = "Website", url = "https://example.com" },
                new { kind = "Reference", url = "https://example.com/about" },
            },
        });

        // The same values in the opposite order: every row's key and sort order collide with another's old one.
        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["channelDefaults"] = new[] { new { channelKey = "pinterest" }, new { channelKey = "instagram" } },
            ["links"] = new[]
            {
                new { kind = "Reference", url = "https://example.com/about" },
                new { kind = "Website", url = "https://example.com" },
            },
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellation));
        var body = await BodyOf(response);
        Assert.Equal(2, body.GetProperty("revision").GetInt32());
        Assert.Equal(
            ["pinterest", "instagram"],
            body.GetProperty("channelDefaults").EnumerateArray().Select(c => c.GetProperty("channelKey").GetString()));
        Assert.Equal(
            ["Reference", "Website"],
            body.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_list_that_differs_only_in_whitespace_is_not_a_change()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            brandName = "B",
            channelDefaults = new[] { new { channelKey = "instagram" } },
        });

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["channelDefaults"] = new[] { new { channelKey = "  instagram  " } },
        });

        Assert.Equal(1, (await BodyOf(response)).GetProperty("revision").GetInt32());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfileRevisions.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task A_patch_that_changes_nothing_writes_no_audit_entry()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var id = created.GetProperty("id").GetGuid();

        await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["locale"] = "en-US" });

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var actions = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .Where(log => log.ResourceId == id.ToString("D"))
            .Select(log => log.Action)
            .ToListAsync(cancellation);

        Assert.Equal([BrandAuditActions.Created], actions);
    }

    [Fact]
    public async Task Whitespace_only_text_clears_an_optional_field()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["shortDescription"] = " \t  " }));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("shortDescription").ValueKind);
    }

    [Fact]
    public async Task Replaying_an_update_key_with_a_different_token_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { expectedConcurrencyToken = created.GetProperty("concurrencyToken").GetString(), locale = "fr-FR" },
            "brand-update-2",
            cancellation);
        var other = await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { expectedConcurrencyToken = WrongToken, locale = "fr-FR" },
            "brand-update-2",
            cancellation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
    }

    // ---- Revision and audit ----

    [Fact]
    public async Task Each_effective_edit_writes_one_immutable_revision_carrying_its_reason()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var edited = await BodyOf(await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["brandName"] = "Sam's Table", ["reason"] = "Rebrand" }));
        Assert.Equal(2, edited.GetProperty("revision").GetInt32());

        var revisions = await InScopeAsync(
            _fixture.WorkspaceA, db => db.BrandProfileRevisions.OrderBy(r => r.Revision).ToListAsync(cancellation));
        Assert.Equal([1, 2], revisions.Select(r => r.Revision));
        Assert.Null(revisions[0].Reason);
        Assert.Equal("Rebrand", revisions[1].Reason);
        Assert.Contains("Sam's Kitchen", revisions[0].Document);
        Assert.Contains("Sam's Table", revisions[1].Document);

        // Write-once: the stored history cannot be edited afterwards.
        await Assert.ThrowsAnyAsync<Exception>(() => InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var first = await db.BrandProfileRevisions.FirstAsync(r => r.Revision == 1, cancellation);
            first.Document = "{}";
            return await db.SaveChangesAsync(cancellation);
        }));
    }

    [Fact]
    public async Task An_update_audit_entry_names_revisions_and_never_the_reason()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var id = created.GetProperty("id").GetGuid();

        await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["locale"] = "fr-FR", ["reason"] = "private words" });

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var entries = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .Where(log => log.ResourceId == id.ToString("D") && log.Action == BrandAuditActions.Updated)
            .ToListAsync(cancellation);

        var entry = Assert.Single(entries);
        Assert.Equal("1", entry.BeforeReference);
        Assert.Equal("2", entry.AfterReference);
        Assert.DoesNotContain("private words", entry.Summary);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task A_token_the_profile_does_not_hold_is_a_conflict_and_changes_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { expectedConcurrencyToken = WrongToken, brandName = "Lost update" },
            cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.Conflict, Code(await BodyOf(response)));

        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));
        Assert.Equal("Sam's Kitchen", read.GetProperty("brandName").GetString());
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task A_missing_or_malformed_token_is_a_bad_request_not_a_conflict(string? token)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { expectedConcurrencyToken = token, brandName = "X" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patching_a_workspace_with_no_profile_is_not_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { expectedConcurrencyToken = WrongToken, brandName = "X" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.ProfileNotFound, Code(await BodyOf(response)));
    }

    // ---- Policy ----

    [Fact]
    public async Task Viewers_and_contributors_may_read_but_not_write()
    {
        var cancellation = TestContext.Current.CancellationToken;

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);

        foreach (var (client, workspace) in new[] { (viewer, _fixture.WorkspaceB), (contributor, _fixture.WorkspaceA) })
        {
            var post = await client.PostAsJsonAsync(BrandIn(workspace), new { brandName = "B" }, cancellation);
            var patch = await client.PatchAsJsonAsync(
                BrandIn(workspace), new { expectedConcurrencyToken = WrongToken, brandName = "B" }, cancellation);

            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        }

        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfiles.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task An_editor_may_create_and_update()
    {
        var cancellation = TestContext.Current.CancellationToken;
        Assert.True(_fixture.WorkspaceA.MemberRole >= WorkspaceRole.Editor);
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: cancellation);

        var created = await CreateAsync(editor, _fixture.WorkspaceA);
        var updated = await PatchAsync(editor, _fixture.WorkspaceA, created, new() { ["locale"] = "de-DE" });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    }

    // ---- Idempotency ----

    [Fact]
    public async Task Replaying_a_create_with_the_same_key_returns_the_original_and_creates_nothing_more()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var body = new { brandName = "Sam's Kitchen" };

        var first = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), body, "brand-create-1", cancellation);
        var replay = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), body, "brand-create-1", cancellation);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(replay)).GetProperty("id").GetGuid());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfiles.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task Replaying_an_update_returns_the_original_answer_and_writes_no_second_revision()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var fields = new Dictionary<string, object?>
        {
            ["expectedConcurrencyToken"] = created.GetProperty("concurrencyToken").GetString(),
            ["defaultAudience"] = "Busy home cooks",
        };

        var first = await client.PatchAsJsonAsync(BrandIn(_fixture.WorkspaceA), fields, "brand-update-1", cancellation);
        var replay = await client.PatchAsJsonAsync(BrandIn(_fixture.WorkspaceA), fields, "brand-update-1", cancellation);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(2, (await BodyOf(replay)).GetProperty("revision").GetInt32());
        Assert.Equal(2, (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfileRevisions.ToListAsync(cancellation))).Count);
    }

    [Fact]
    public async Task An_idempotency_key_does_not_replay_across_workspaces()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await OwnerOf(_fixture.WorkspaceA);
        using var inB = await OwnerOf(_fixture.WorkspaceB);

        await inA.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), new { brandName = "Kitchen A" }, "shared-key", cancellation);
        var inBResponse = await inB.PostAsJsonAsync(BrandIn(_fixture.WorkspaceB), new { brandName = "Kitchen A" }, "shared-key", cancellation);

        Assert.Equal(HttpStatusCode.Created, inBResponse.StatusCode);
        Assert.False(inBResponse.Headers.Contains("Idempotent-Replayed"));
        Assert.Single(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandProfiles.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_body_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), new { brandName = "One" }, "brand-create-2", cancellation);
        var reused = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), new { brandName = "Two" }, "brand-create-2", cancellation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Each_workspace_creates_and_edits_only_its_own_profile()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await OwnerOf(_fixture.WorkspaceA);
        using var inB = await OwnerOf(_fixture.WorkspaceB);

        var a = await CreateAsync(inA, _fixture.WorkspaceA, new { brandName = "Kitchen A" });
        var b = await CreateAsync(inB, _fixture.WorkspaceB, new { brandName = "Kitchen B" });

        await PatchAsync(inA, _fixture.WorkspaceA, a, new() { ["brandName"] = "Kitchen A2" });

        var readB = await BodyOf(await inB.GetAsync(BrandIn(_fixture.WorkspaceB), cancellation));
        Assert.Equal("Kitchen B", readB.GetProperty("brandName").GetString());
        Assert.Equal(b.GetProperty("id").GetGuid(), readB.GetProperty("id").GetGuid());
        Assert.Equal(1, readB.GetProperty("revision").GetInt32());

        // Revisions and audit rows belong to the workspace that wrote them.
        Assert.Equal(2, (await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfileRevisions.ToListAsync(cancellation))).Count);
        Assert.Single(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandProfileRevisions.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task Writing_to_another_workspaces_route_is_indistinguishable_from_an_unknown_one()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inB = await OwnerOf(_fixture.WorkspaceB);
        var b = await CreateAsync(inB, _fixture.WorkspaceB, new { brandName = "Kitchen B" });
        using var inA = await OwnerOf(_fixture.WorkspaceA);
        var body = new { expectedConcurrencyToken = b.GetProperty("concurrencyToken").GetString(), brandName = "Hijack" };

        var foreign = await inA.PatchAsJsonAsync(BrandIn(_fixture.WorkspaceB), body, cancellation);
        var unknown = await inA.PatchAsJsonAsync("/api/v1/workspaces/no-such-workspace/brand-profile", body, cancellation);
        var foreignPost = await inA.PostAsJsonAsync(BrandIn(_fixture.WorkspaceB), new { brandName = "Hijack" }, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignPost.StatusCode);
        Assert.Equal(Code(await BodyOf(unknown)), Code(await BodyOf(foreign)));

        // B's profile is untouched, and B still has exactly one.
        var readB = await BodyOf(await inB.GetAsync(BrandIn(_fixture.WorkspaceB), cancellation));
        Assert.Equal("Kitchen B", readB.GetProperty("brandName").GetString());
        Assert.Single(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandProfiles.ToListAsync(cancellation)));
    }

    [Fact]
    public async Task The_body_cannot_name_a_workspace_or_an_author()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new
            {
                brandName = "B",
                workspaceId = _fixture.WorkspaceB.Id,
                createdByMembershipId = Guid.NewGuid(),
                revision = 99,
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var inA = await InScopeAsync(_fixture.WorkspaceA, db => db.BrandProfiles.ToListAsync(cancellation));
        Assert.Equal(_fixture.WorkspaceA.Id, Assert.Single(inA).WorkspaceId);
        Assert.Equal(1, inA[0].Revision);
        Assert.Empty(await InScopeAsync(_fixture.WorkspaceB, db => db.BrandProfiles.ToListAsync(cancellation)));
    }
}
