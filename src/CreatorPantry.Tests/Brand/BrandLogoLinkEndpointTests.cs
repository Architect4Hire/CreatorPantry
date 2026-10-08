using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// Logo links on <c>POST</c> and <c>PATCH .../brand-profile</c> through the real Gateway (12.10k): linking,
/// replacing and unlinking library assets, the one refusal for an asset this workspace cannot link, replay, and
/// what one workspace can do with another's assets — which is nothing.
/// </summary>
public sealed class BrandLogoLinkEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static string BrandIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-profile";

    // ---- Linking ----

    [Fact]
    public async Task A_profile_can_be_created_with_a_primary_logo_and_alternates_in_the_order_given()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var primary = await SeedAssetAsync(_fixture.WorkspaceA);
        var mark = await SeedAssetAsync(_fixture.WorkspaceA);
        var mono = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { brandName = "Sam's Kitchen", assets = new[] { Logo(mark, "AlternateLogo"), Logo(primary, "PrimaryLogo"), Logo(mono, "AlternateLogo") } },
            Ct);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((Guid[])[mark, primary, mono], AssetIds(body));
        Assert.Equal((string[])["AlternateLogo", "PrimaryLogo", "AlternateLogo"], Roles(body));

        // And a read returns the same, so they were stored and not only echoed.
        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), Ct));
        Assert.Equal((Guid[])[mark, primary, mono], AssetIds(read));
    }

    [Fact]
    public async Task An_edit_links_a_logo_writes_a_revision_and_leaves_the_other_fields_alone()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["assets"] = new[] { Logo(logo, "PrimaryLogo") } });
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((Guid[])[logo], AssetIds(body));
        Assert.Equal(2, body.GetProperty("revision").GetInt32());
        Assert.Equal("Sam's Kitchen", body.GetProperty("brandName").GetString());
    }

    [Fact]
    public async Task Re_submitting_the_same_logos_changes_nothing_and_writes_no_revision()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { brandName = "B", assets = new[] { Logo(logo, "PrimaryLogo") } });

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["assets"] = new[] { Logo(logo, "PrimaryLogo") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await BodyOf(response)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Leaving_assets_out_of_an_edit_leaves_the_logos_alone()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { brandName = "B", assets = new[] { Logo(logo, "PrimaryLogo") } });

        var body = await BodyOf(await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["locale"] = "fr-FR" }));

        Assert.Equal((Guid[])[logo], AssetIds(body));
        Assert.Equal("fr-FR", body.GetProperty("locale").GetString());
    }

    // ---- One primary logo ----

    [Fact]
    public async Task Two_primary_logos_and_the_same_asset_twice_are_refused_as_malformed()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var first = await SeedAssetAsync(_fixture.WorkspaceA);
        var second = await SeedAssetAsync(_fixture.WorkspaceA);

        var twoPrimaries = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { brandName = "B", assets = new[] { Logo(first, "PrimaryLogo"), Logo(second, "PrimaryLogo") } },
            Ct);
        var repeated = await client.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new { brandName = "B", assets = new[] { Logo(first, "PrimaryLogo"), Logo(first, "AlternateLogo") } },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, twoPrimaries.StatusCode);
        Assert.Equal(BrandErrorCodes.InvalidRequest, Code(await BodyOf(twoPrimaries)));
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);

        // Nothing was created by either.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BrandIn(_fixture.WorkspaceA), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_primary_logo_can_be_changed_for_another_in_one_edit()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var old = await SeedAssetAsync(_fixture.WorkspaceA);
        var next = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { brandName = "B", assets = new[] { Logo(old, "PrimaryLogo") } });

        // The old primary demoted and a new one named, in one list: the unique index must not trip on the way.
        var response = await PatchAsync(
            client, _fixture.WorkspaceA, created,
            new() { ["assets"] = new[] { Logo(next, "PrimaryLogo"), Logo(old, "AlternateLogo") } });
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((Guid[])[next, old], AssetIds(body));
        Assert.Equal((string[])["PrimaryLogo", "AlternateLogo"], Roles(body));
    }

    // ---- The one refusal ----

    /// <summary>
    /// The restriction: unknown, another workspace's and removed are one answer. Compared whole — status, code,
    /// title and every field message — so nothing about the three can be told apart.
    /// </summary>
    [Fact]
    public async Task An_unknown_asset_another_workspaces_and_a_removed_one_are_refused_identically()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var neighbours = await SeedAssetAsync(_fixture.WorkspaceB);
        var removed = await SeedAssetAsync(_fixture.WorkspaceA, deleted: true);

        async Task<string> Refused(Guid assetId)
        {
            var response = await client.PostAsJsonAsync(
                BrandIn(_fixture.WorkspaceA), new { brandName = "B", assets = new[] { Logo(assetId, "PrimaryLogo") } }, Ct);

            return await Refusal(response);
        }

        var forUnknown = await Refused(Guid.NewGuid());

        Assert.StartsWith($"422|{BrandErrorCodes.AssetsUnprocessable}|", forUnknown);
        Assert.Contains("assets[0].MediaAssetId=", forUnknown);
        Assert.Equal(forUnknown, await Refused(neighbours));
        Assert.Equal(forUnknown, await Refused(removed));

        // And nothing was created by any of them.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BrandIn(_fixture.WorkspaceA), Ct)).StatusCode);
    }

    /// <summary>
    /// The same three, on an edit — the path that has the "already linked" exemption, and so the one where a
    /// neighbour's id slipping into the exempt set would show.
    /// </summary>
    [Fact]
    public async Task On_an_edit_the_three_are_refused_identically_too()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var neighbours = await SeedAssetAsync(_fixture.WorkspaceB);
        var removed = await SeedAssetAsync(_fixture.WorkspaceA, deleted: true);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        async Task<string> Refused(Guid assetId) =>
            await Refusal(await PatchAsync(
                client, _fixture.WorkspaceA, created, new() { ["assets"] = new[] { Logo(assetId, "PrimaryLogo") } }));

        var forUnknown = await Refused(Guid.NewGuid());

        Assert.StartsWith($"422|{BrandErrorCodes.AssetsUnprocessable}|", forUnknown);
        Assert.Equal(forUnknown, await Refused(neighbours));
        Assert.Equal(forUnknown, await Refused(removed));
        Assert.Equal(0, await LinkCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// The exemption is exactly as wide as the profile's own links: a logo already held whose asset has left
    /// the library passes, and an unknown one submitted beside it is the only one named.
    /// </summary>
    [Fact]
    public async Task Only_the_logo_not_already_linked_is_named_when_both_are_submitted()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var held = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { brandName = "B", assets = new[] { Logo(held, "PrimaryLogo") } });
        await RemoveFromLibraryAsync(_fixture.WorkspaceA, held);

        var response = await PatchAsync(
            client, _fixture.WorkspaceA, created,
            new() { ["assets"] = new[] { Logo(held, "PrimaryLogo"), Logo(Guid.NewGuid(), "AlternateLogo") } });
        var errors = (await BodyOf(response)).GetProperty("errors");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal((string[])["assets[1].MediaAssetId"], errors.EnumerateObject().Select(field => field.Name));
    }

    [Fact]
    public async Task A_refused_logo_is_named_by_its_position_and_nothing_is_stored()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var good = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await PatchAsync(
            client, _fixture.WorkspaceA, created,
            new() { ["assets"] = new[] { Logo(good, "PrimaryLogo"), Logo(Guid.NewGuid(), "AlternateLogo") }, ["locale"] = "fr-FR" });
        var errors = (await BodyOf(response)).GetProperty("errors");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(errors.TryGetProperty("assets[1].MediaAssetId", out _));
        Assert.False(errors.TryGetProperty("assets[0].MediaAssetId", out _));

        // The whole edit was refused: neither the good logo nor the unrelated field was written.
        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), Ct));
        Assert.Equal(0, read.GetProperty("assets").GetArrayLength());
        Assert.Equal("en-US", read.GetProperty("locale").GetString());
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
    }

    /// <summary>
    /// A logo already linked is kept when re-submitted after its asset has left the library, so an unrelated
    /// edit is never blocked by it — but it cannot be newly linked, and once dropped it cannot come back.
    /// </summary>
    [Fact]
    public async Task A_logo_already_linked_survives_an_edit_after_its_asset_leaves_the_library()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { brandName = "B", assets = new[] { Logo(logo, "PrimaryLogo") } });

        await RemoveFromLibraryAsync(_fixture.WorkspaceA, logo);

        var kept = await PatchAsync(
            client, _fixture.WorkspaceA, created,
            new() { ["assets"] = new[] { Logo(logo, "PrimaryLogo") }, ["locale"] = "fr-FR" });
        var keptBody = await BodyOf(kept);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal((Guid[])[logo], AssetIds(keptBody));
        Assert.Equal("fr-FR", keptBody.GetProperty("locale").GetString());

        // Unlinked, then named again: now it is a new link to something not in the library.
        var unlinked = await BodyOf(await PatchAsync(client, _fixture.WorkspaceA, keptBody, new() { ["assets"] = Array.Empty<object>() }));
        var again = await PatchAsync(client, _fixture.WorkspaceA, unlinked, new() { ["assets"] = new[] { Logo(logo, "PrimaryLogo") } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(BrandErrorCodes.AssetsUnprocessable, Code(await BodyOf(again)));
    }

    // ---- Unlinking ----

    /// <summary>The restriction, on the rows themselves: unlinking removes links and nothing about the asset.</summary>
    [Fact]
    public async Task Unlinking_removes_the_link_and_nothing_about_the_asset()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var primary = await SeedAssetAsync(_fixture.WorkspaceA, versions: 2, uses: 3);
        var alternate = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(
            client, _fixture.WorkspaceA,
            new { brandName = "B", assets = new[] { Logo(primary, "PrimaryLogo"), Logo(alternate, "AlternateLogo") } });

        // One unlinked, the other kept.
        var one = await BodyOf(await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["assets"] = new[] { Logo(alternate, "AlternateLogo") } }));
        Assert.Equal((Guid[])[alternate], AssetIds(one));

        // Then all of them.
        var none = await PatchAsync(client, _fixture.WorkspaceA, one, new() { ["assets"] = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Empty(AssetIds(await BodyOf(none)));

        var after = await AssetFactsAsync(_fixture.WorkspaceA, primary);
        Assert.True(after.Exists);
        Assert.Null(after.DeletedAt);
        Assert.Equal(2, after.Versions);
        Assert.Equal(3, after.Uses);

        // Still readable in the library, by its own route, and no longer counted as used by a brand.
        var read = await BodyOf(await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets/{primary}", Ct));
        Assert.Equal(3, read.GetProperty("utilizationCount").GetInt32());
        Assert.Equal(0, read.GetProperty("brandProfileCount").GetInt32());
    }

    [Fact]
    public async Task A_linked_logo_is_counted_on_the_assets_own_page()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, new { brandName = "B", assets = new[] { Logo(logo, "PrimaryLogo") } });

        var read = await BodyOf(await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets/{logo}", Ct));

        Assert.Equal(1, read.GetProperty("brandProfileCount").GetInt32());
    }

    // ---- Replay, concurrency, roles ----

    [Fact]
    public async Task Replaying_a_logo_edit_with_its_key_returns_the_first_answer_and_writes_one_revision()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA);
        var body = new Dictionary<string, object?>
        {
            ["assets"] = new[] { Logo(logo, "PrimaryLogo") },
            ["expectedConcurrencyToken"] = created.GetProperty("concurrencyToken").GetString(),
        };
        var key = new Dictionary<string, string> { [IdempotencyPolicy.KeyHeader] = "logo-key-1" };

        var first = await client.SendAsync(HttpMethod.Patch, BrandIn(_fixture.WorkspaceA), body, key, Ct);
        var replay = await client.SendAsync(HttpMethod.Patch, BrandIn(_fixture.WorkspaceA), body, key, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));

        var read = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), Ct));
        Assert.Equal(2, read.GetProperty("revision").GetInt32());
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// A replay returns what the first call answered even if the asset has since left the library: the key
    /// names a decision already made, not a new one to be judged against today's library.
    /// </summary>
    [Fact]
    public async Task A_replay_still_answers_after_the_asset_has_left_the_library()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        var request = new { brandName = "B", assets = new[] { Logo(logo, "PrimaryLogo") } };

        var first = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), request, "logo-create-key", Ct);
        await RemoveFromLibraryAsync(_fixture.WorkspaceA, logo);
        var replay = await client.PostAsJsonAsync(BrandIn(_fixture.WorkspaceA), request, "logo-create-key", Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
    }

    [Fact]
    public async Task A_logo_edit_from_a_stale_token_is_a_conflict_and_links_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var logo = await SeedAssetAsync(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new Dictionary<string, object?>
            {
                ["assets"] = new[] { Logo(logo, "PrimaryLogo") },
                ["expectedConcurrencyToken"] = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]),
            },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await LinkCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// The token is judged before the logo, so a stale caller is told to re-read rather than being told about
    /// an asset — the answer that leads them to the profile as it now stands.
    /// </summary>
    [Fact]
    public async Task A_stale_token_is_answered_before_an_unlinkable_logo()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new Dictionary<string, object?>
            {
                ["assets"] = new[] { Logo(Guid.NewGuid(), "PrimaryLogo") },
                ["expectedConcurrencyToken"] = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]),
            },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_link_a_logo()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var created = await CreateAsync(owner, _fixture.WorkspaceB);
        var logo = await SeedAssetAsync(_fixture.WorkspaceB);

        // Workspace B's seeded member is a Viewer.
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);
        var response = await PatchAsync(viewer, _fixture.WorkspaceB, created, new() { ["assets"] = new[] { Logo(logo, "PrimaryLogo") } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await LinkCountAsync(_fixture.WorkspaceB));
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspaces_logos_are_not_the_others()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var logoA = await SeedAssetAsync(_fixture.WorkspaceA);
        var logoB = await SeedAssetAsync(_fixture.WorkspaceB);

        await CreateAsync(ownerA, _fixture.WorkspaceA, new { brandName = "A", assets = new[] { Logo(logoA, "PrimaryLogo") } });
        var createdB = await CreateAsync(ownerB, _fixture.WorkspaceB, new { brandName = "B", assets = new[] { Logo(logoB, "PrimaryLogo") } });

        // Each reads its own, and only its own.
        Assert.Equal((Guid[])[logoA], AssetIds(await BodyOf(await ownerA.GetAsync(BrandIn(_fixture.WorkspaceA), Ct))));
        Assert.Equal((Guid[])[logoB], AssetIds(createdB));

        // B cannot take A's logo, by an edit to its own profile...
        var taken = await PatchAsync(ownerB, _fixture.WorkspaceB, createdB, new() { ["assets"] = new[] { Logo(logoA, "PrimaryLogo") } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);

        // ...and cannot reach A's profile at all.
        var reached = await ownerB.PatchAsJsonAsync(
            BrandIn(_fixture.WorkspaceA),
            new Dictionary<string, object?>
            {
                ["assets"] = Array.Empty<object>(),
                ["expectedConcurrencyToken"] = createdB.GetProperty("concurrencyToken").GetString(),
            },
            Ct);
        Assert.Equal(HttpStatusCode.NotFound, reached.StatusCode);

        Assert.Equal((Guid[])[logoA], AssetIds(await BodyOf(await ownerA.GetAsync(BrandIn(_fixture.WorkspaceA), Ct))));
        Assert.Equal((Guid[])[logoB], AssetIds(await BodyOf(await ownerB.GetAsync(BrandIn(_fixture.WorkspaceB), Ct))));
    }

    [Fact]
    public async Task One_workspaces_idempotency_key_replays_nothing_in_the_other()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var logoA = await SeedAssetAsync(_fixture.WorkspaceA);
        var logoB = await SeedAssetAsync(_fixture.WorkspaceB);

        var inA = await ownerA.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceA), new { brandName = "A", assets = new[] { Logo(logoA, "PrimaryLogo") } }, "shared-logo-key", Ct);
        var inB = await ownerB.PostAsJsonAsync(
            BrandIn(_fixture.WorkspaceB), new { brandName = "B", assets = new[] { Logo(logoB, "PrimaryLogo") } }, "shared-logo-key", Ct);

        Assert.Equal(HttpStatusCode.Created, inA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);
        Assert.False(inB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal((Guid[])[logoB], AssetIds(await BodyOf(inB)));
    }

    /// <summary>
    /// The count of brand profiles using an asset is the asset's own workspace's business: B cannot read A's
    /// asset at all, linked or not.
    /// </summary>
    [Fact]
    public async Task Another_workspace_cannot_learn_that_an_asset_is_a_logo()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var logoA = await SeedAssetAsync(_fixture.WorkspaceA);
        await CreateAsync(ownerA, _fixture.WorkspaceA, new { brandName = "A", assets = new[] { Logo(logoA, "PrimaryLogo") } });

        var linked = await ownerB.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/dam-assets/{logoA}", Ct);
        var unknown = await ownerB.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/dam-assets/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, linked.StatusCode);
        Assert.Equal(await Refusal(unknown), await Refusal(linked));
    }

    // ---- Helpers ----

    private static object Logo(Guid mediaAssetId, string role) => new { mediaAssetId, role };

    private static Guid[] AssetIds(JsonElement profile) =>
        [.. profile.GetProperty("assets").EnumerateArray().Select(asset => asset.GetProperty("mediaAssetId").GetGuid())];

    private static string[] Roles(JsonElement profile) =>
        [.. profile.GetProperty("assets").EnumerateArray().Select(asset => asset.GetProperty("role").GetString()!)];

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static async Task<JsonElement> CreateAsync(GatewayClient client, SeededWorkspace workspace, object? body = null)
    {
        var response = await client.PostAsJsonAsync(
            BrandIn(workspace), body ?? new { brandName = "Sam's Kitchen", locale = "en-US" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private static Task<HttpResponseMessage> PatchAsync(
        GatewayClient client, SeededWorkspace workspace, JsonElement current, Dictionary<string, object?> fields)
    {
        fields["expectedConcurrencyToken"] = current.GetProperty("concurrencyToken").GetString();

        return client.PatchAsJsonAsync(BrandIn(workspace), fields, Ct);
    }

    /// <summary>
    /// Seeds an asset straight into the database, with the workspace context resolved first so the ownership
    /// interceptor stamps <c>WorkspaceId</c> exactly as a request would.
    /// </summary>
    private async Task<Guid> SeedAssetAsync(SeededWorkspace workspace, int versions = 1, int uses = 0, bool deleted = false)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = "Wordmark",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = versions,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            DeletedAt = deleted ? Now : null,
            DeletedByMembershipId = deleted ? actor : null,
        };

        for (var number = 1; number <= versions; number++)
        {
            asset.Versions.Add(new MediaAssetVersion
            {
                Id = Guid.NewGuid(),
                MediaAssetId = asset.Id,
                VersionNumber = number,
                MediaType = "image/png",
                SizeBytes = 20_480,
                Width = 800,
                Height = 400,
                ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                ObjectKey = $"assets/{asset.Id:D}/{number}.png",
                Source = MediaAssetVersionSource.Upload,
                CreatedByMembershipId = actor,
                CreatedAt = Now,
            });
        }

        db.MediaAssets.Add(asset);

        for (var index = 0; index < uses; index++)
        {
            db.MediaAssetUtilizations.Add(new MediaAssetUtilization
            {
                Id = Guid.NewGuid(),
                MediaAssetId = asset.Id,
                PlatformKey = "newsletter",
                UtilizedOn = DateOnly.FromDateTime(Now.UtcDateTime),
                UtilizedDay = Now.DayOfWeek,
                LoggedByMembershipId = actor,
                CreatedAt = Now,
            });
        }

        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    /// <summary>Soft-deletes the asset as the library's own removal does, leaving every row in place.</summary>
    private async Task RemoveFromLibraryAsync(SeededWorkspace workspace, Guid assetId)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var asset = await db.MediaAssets.SingleAsync(candidate => candidate.Id == assetId, Ct);
        asset.DeletedAt = Now;
        asset.DeletedByMembershipId = Guid.NewGuid();

        await db.SaveChangesAsync(Ct);
    }

    private async Task<(bool Exists, DateTimeOffset? DeletedAt, int Versions, int Uses)> AssetFactsAsync(
        SeededWorkspace workspace, Guid assetId)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == assetId, Ct);

        return (
            asset is not null,
            asset?.DeletedAt,
            await db.MediaAssetVersions.CountAsync(version => version.MediaAssetId == assetId, Ct),
            await db.MediaAssetUtilizations.CountAsync(use => use.MediaAssetId == assetId, Ct));
    }

    private async Task<int> LinkCountAsync(SeededWorkspace workspace)
    {
        await using var scope = ScopeFor(workspace);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandAssetLinks.CountAsync(Ct);
    }

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    /// <summary>What a refusal says, whole: its status, code, title and every field message.</summary>
    private static async Task<string> Refusal(HttpResponseMessage response)
    {
        var body = await BodyOf(response);
        var fields = body.TryGetProperty("errors", out var errors)
            ? string.Join(",", errors.EnumerateObject().Select(field => $"{field.Name}={field.Value}"))
            : string.Empty;

        return $"{(int)response.StatusCode}|{body.GetProperty("code").GetString()}|{body.GetProperty("title").GetString()}|{fields}";
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
