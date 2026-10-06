using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/dam-assets</c> through the real Gateway — real cookie session,
/// real gateway-signed internal token, real API — for the published page shape, the filters as a client
/// actually spells them in a URL, the stable refusal codes, and what a cursor cannot reach.
/// </summary>
/// <remarks>
/// The query semantics themselves are proved against SQL Server by
/// <see cref="MediaAssetSearchSqlServerTests"/>. What is only answerable here is the contract: the names a
/// client binds to, that an enum arrives as a name rather than an integer, which status a refusal gets, and
/// that a member of one workspace is told nothing about the other.
/// </remarks>
public sealed class MediaAssetSearchEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string AssetsIn(SeededWorkspace workspace, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets{query}";

    // ---- The published page ----

    [Fact]
    public async Task An_empty_library_is_an_empty_page_rather_than_a_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct);
        var body = await ReadAsync(response);

        // A member asking about their own empty library is not a 404. There is nothing here whose existence
        // needs hiding — their membership of this workspace is not in doubt by the time the action runs.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// The whole published item, named field by field. Enums arrive as the names the schema publishes rather
    /// than integers a client would have to map privately, and the media facts come with the row so a grid can
    /// lay out without fetching every asset.
    /// </summary>
    [Fact]
    public async Task An_asset_appears_with_its_metadata_and_its_current_versions_media_facts()
    {
        await SeedAsync(
            _fixture.WorkspaceA,
            "Soda bread hero",
            description: "Overhead on linen.",
            altText: "A round loaf, slashed across the top.",
            channelKey: "instagram",
            platformKey: "reels",
            day: DayOfWeek.Wednesday,
            styleKey: "overhead-linen");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var item = (await ReadAsync(await client.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct)))
            .GetProperty("items").EnumerateArray().Single();

        Assert.Equal("Soda bread hero", item.GetProperty("title").GetString());
        Assert.Equal("Overhead on linen.", item.GetProperty("description").GetString());
        Assert.Equal("A round loaf, slashed across the top.", item.GetProperty("altText").GetString());
        Assert.Equal("Original", item.GetProperty("kind").GetString());
        Assert.Equal("instagram", item.GetProperty("channelKey").GetString());
        Assert.Equal("reels", item.GetProperty("platformKey").GetString());
        Assert.Equal("Wednesday", item.GetProperty("day").GetString());
        Assert.Equal("overhead-linen", item.GetProperty("styleKey").GetString());
        Assert.Equal("image/jpeg", item.GetProperty("mediaType").GetString());
        Assert.Equal(1600, item.GetProperty("width").GetInt32());
        Assert.Equal(1200, item.GetProperty("height").GetInt32());
        Assert.Equal(204_800L, item.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1, item.GetProperty("currentVersionNumber").GetInt32());
    }

    /// <summary>
    /// Nothing a library page publishes is an address or a byte, and nothing names who uploaded it. The object
    /// key and the checksum exist on the version row and stay on the server (media.md).
    /// </summary>
    [Fact]
    public async Task An_item_carries_no_address_no_bytes_and_no_authorship()
    {
        await SeedAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var item = (await ReadAsync(await client.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct)))
            .GetProperty("items").EnumerateArray().Single();

        foreach (var absent in (string[])
            ["objectKey", "url", "contentChecksum", "workspaceId", "createdByMembershipId", "rowVersion"])
        {
            Assert.False(item.TryGetProperty(absent, out _), $"a library card must not publish {absent}");
        }
    }

    /// <summary>
    /// A creator's private index of their own work must not sit in a shared proxy or in a browser cache after
    /// they sign out.
    /// </summary>
    [Fact]
    public async Task A_library_page_is_not_cached()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct);

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_search()
    {
        await SeedAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await member.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    // ---- Filters as a client spells them ----

    [Fact]
    public async Task The_filters_narrow_the_library_as_a_client_spells_them_in_a_url()
    {
        var tag = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var otherTag = await SeedTagAsync(_fixture.WorkspaceA, "Freezer");

        await SeedAsync(
            _fixture.WorkspaceA, "Wanted",
            channelKey: "instagram", platformKey: "reels", day: DayOfWeek.Wednesday,
            styleKey: "overhead-linen", tagIds: [tag]);
        await SeedAsync(
            _fixture.WorkspaceA, "Excluded",
            channelKey: "blog", platformKey: "web", day: DayOfWeek.Sunday,
            styleKey: "closeup", tagIds: [otherTag]);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(["Wanted"], await TitlesAsync(client, "?channel=instagram"));
        Assert.Equal(["Wanted"], await TitlesAsync(client, "?platform=reels"));
        Assert.Equal(["Wanted"], await TitlesAsync(client, "?day=Wednesday"));
        Assert.Equal(["Wanted"], await TitlesAsync(client, "?style=overhead-linen"));
        Assert.Equal(["Wanted"], await TitlesAsync(client, "?search=wanted"));
        Assert.Equal(["Wanted"], await TitlesAsync(client, $"?tag={tag:D}"));

        // All of them at once, which is the combination a filter panel actually sends.
        Assert.Equal(
            ["Wanted"],
            await TitlesAsync(
                client,
                $"?channel=instagram&platform=reels&day=Wednesday&style=overhead-linen&tag={tag:D}&search=want"));
    }

    /// <summary>A comma-separated list means "any of these", which is the only reading a tag panel has.</summary>
    [Fact]
    public async Task A_comma_separated_tag_list_matches_any_of_them()
    {
        var one = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var two = await SeedTagAsync(_fixture.WorkspaceA, "Freezer");

        await SeedAsync(_fixture.WorkspaceA, "One", tagIds: [one]);
        await SeedAsync(_fixture.WorkspaceA, "Two", tagIds: [two]);
        await SeedAsync(_fixture.WorkspaceA, "Neither");

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(["One", "Two"], (await TitlesAsync(client, $"?tag={one:D},{two:D}")).Order());
    }

    [Fact]
    public async Task The_sort_parameter_accepts_only_the_named_orderings()
    {
        await SeedAsync(_fixture.WorkspaceA, "Zucchini fritters", createdAt: Now);
        await SeedAsync(_fixture.WorkspaceA, "Apple galette", createdAt: Now.AddDays(-1));

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(["Zucchini fritters", "Apple galette"], await TitlesAsync(client, string.Empty));
        Assert.Equal(["Apple galette", "Zucchini fritters"], await TitlesAsync(client, "?sort=Title"));

        var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA, "?sort=Oldest"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetSearchInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_malformed_id_in_a_list_is_refused_naming_that_parameter()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA, "?tag=weeknight"), Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MediaErrorCodes.AssetSearchInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("tag", out _));
    }

    /// <summary>
    /// An inverted range matches nothing, which is a legal answer but almost never the intended one — and "no
    /// assets" is an answer a creator cannot debug. Refused by name instead.
    /// </summary>
    [Fact]
    public async Task An_inverted_date_range_is_refused_rather_than_silently_empty()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            AssetsIn(_fixture.WorkspaceA, "?createdFrom=2026-05-01T00:00:00Z&createdBefore=2026-04-01T00:00:00Z"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("createdBefore", out _));
    }

    [Fact]
    public async Task A_search_term_longer_than_the_published_limit_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            AssetsIn(_fixture.WorkspaceA, $"?search={new string('a', MediaAssetSearchPolicy.SearchMaxLength + 1)}"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("search", out _));
    }

    [Fact]
    public async Task A_vocabulary_key_longer_than_the_column_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var tooLong = new string('a', MediaPolicy.VocabularyKeyMaxLength + 1);

        foreach (var field in (string[])["channel", "platform", "style"])
        {
            var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA, $"?{field}={tooLong}"), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
        }
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_walks_the_whole_library_once()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedAsync(_fixture.WorkspaceA, $"Asset {index}", createdAt: Now.AddMinutes(-index));
        }

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seen = new List<Guid>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var body = await ReadAsync(await client.GetAsync(AssetsIn(_fixture.WorkspaceA, query), Ct));

            seen.AddRange(Ids(body));
            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());

            cursor = body.GetProperty("nextCursor").ValueKind is JsonValueKind.Null
                ? null
                : body.GetProperty("nextCursor").GetString();

            if (cursor is null)
            {
                break;
            }
        }

        Assert.Null(cursor);
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task An_oversized_page_is_clamped_rather_than_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA, "?limit=10000"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_total_can_be_declined()
    {
        await SeedAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(
            await client.GetAsync(AssetsIn(_fixture.WorkspaceA, "?includeTotal=false"), Ct));

        Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalCount").ValueKind);
    }

    [Fact]
    public async Task A_cursor_is_refused_once_the_filters_change_under_it()
    {
        await SeedAsync(_fixture.WorkspaceA, "One", createdAt: Now);
        await SeedAsync(_fixture.WorkspaceA, "Two", createdAt: Now.AddMinutes(-1));

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await ReadAsync(await client.GetAsync(AssetsIn(_fixture.WorkspaceA, "?limit=1"), Ct));
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var response = await client.GetAsync(
            AssetsIn(_fixture.WorkspaceA, $"?limit=1&channel=instagram&cursor={Uri.EscapeDataString(cursor)}"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetCursorInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_cursor_this_server_did_not_issue_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(AssetsIn(_fixture.WorkspaceA, "?cursor=not-a-cursor"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("cursor", out _));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Each_workspace_lists_and_counts_only_its_own_assets()
    {
        await SeedAsync(_fixture.WorkspaceA, "A one");
        await SeedAsync(_fixture.WorkspaceB, "B one");
        await SeedAsync(_fixture.WorkspaceB, "B two");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var fromA = await ReadAsync(await ownerA.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct));
        var fromB = await ReadAsync(await ownerB.GetAsync(AssetsIn(_fixture.WorkspaceB), Ct));

        Assert.Equal(1, fromA.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, fromB.GetProperty("totalCount").GetInt32());
        Assert.Empty(Ids(fromA).Intersect(Ids(fromB)));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_list_the_others_assets()
    {
        await SeedAsync(_fixture.WorkspaceA, "A one");

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var response = await ownerB.GetAsync(AssetsIn(_fixture.WorkspaceA), Ct);

        // B's owner names A's slug: 404, not 403, so the reply does not confirm that Workspace A exists
        // (tenancy.md).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A cursor minted in one workspace, replayed against the other by a member of it. The workspace is folded
    /// into the scope, so it is refused as a cursor rather than quietly paging the wrong library.
    /// </summary>
    [Fact]
    public async Task A_cursor_minted_in_one_workspace_is_refused_by_the_other()
    {
        await SeedAsync(_fixture.WorkspaceA, "A one", createdAt: Now);
        await SeedAsync(_fixture.WorkspaceA, "A two", createdAt: Now.AddMinutes(-1));
        await SeedAsync(_fixture.WorkspaceB, "B one", createdAt: Now);
        await SeedAsync(_fixture.WorkspaceB, "B two", createdAt: Now.AddMinutes(-1));

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var fromA = await ReadAsync(await ownerA.GetAsync(AssetsIn(_fixture.WorkspaceA, "?limit=1"), Ct));
        var cursor = fromA.GetProperty("nextCursor").GetString()!;

        var response = await ownerB.GetAsync(
            AssetsIn(_fixture.WorkspaceB, $"?limit=1&cursor={Uri.EscapeDataString(cursor)}"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetCursorInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A tag id belonging to the other workspace finds nothing rather than that workspace's assets — the filter
    /// is an id a client supplies, so it is the one that could be aimed across the boundary on purpose.
    /// </summary>
    [Fact]
    public async Task A_tag_filter_cannot_be_aimed_at_the_other_workspaces_tag()
    {
        var tagInB = await SeedTagAsync(_fixture.WorkspaceB, "Weeknight");

        await SeedAsync(_fixture.WorkspaceB, "Tagged in B", tagIds: [tagInB]);
        await SeedAsync(_fixture.WorkspaceA, "A's own");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        Assert.Empty(await TitlesAsync(ownerA, $"?tag={tagInB:D}"));
    }

    [Fact]
    public async Task An_unknown_workspace_is_a_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync("/api/v1/workspaces/no-such-workspace/dam-assets", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: Ct);

    /// <summary>
    /// Seeds one asset and its first version straight into the database.
    /// </summary>
    /// <remarks>
    /// Through the write route would be truer, but creating an asset is a multipart upload of real image bytes
    /// through a malware scanner and an object store — which <c>MediaAssetCreateTests</c> covers and which
    /// would make every test here depend on that seam. The workspace context is resolved first, so the
    /// ownership interceptor stamps <c>WorkspaceId</c> exactly as a request would rather than the test
    /// assigning it.
    /// </remarks>
    private async Task SeedAsync(
        SeededWorkspace workspace,
        string title,
        string? description = null,
        string? altText = null,
        string? channelKey = null,
        string? platformKey = null,
        DayOfWeek? day = null,
        string? styleKey = null,
        IReadOnlyList<Guid>? tagIds = null,
        DateTimeOffset? createdAt = null)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();
        var at = createdAt ?? Now;

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            AltText = altText,
            Kind = MediaAssetKind.Original,
            ChannelKey = channelKey,
            PlatformKey = platformKey,
            Day = day,
            StyleKey = styleKey,
            CurrentVersionNumber = 1,
            CreatedAt = at,
            UpdatedAt = at,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        asset.Versions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            MediaAssetId = asset.Id,
            VersionNumber = 1,
            MediaType = "image/jpeg",
            SizeBytes = 204_800,
            Width = 1600,
            Height = 1200,
            ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            ObjectKey = $"assets/{asset.Id:D}/1.jpg",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = at,
        });

        foreach (var tagId in tagIds ?? [])
        {
            asset.Tags.Add(new MediaAssetTag { MediaAssetId = asset.Id, WorkspaceTagId = tagId });
        }

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedTagAsync(SeededWorkspace workspace, string name)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var tag = new Domain.Modules.Recipes.Data.Entities.WorkspaceTag
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            CreatedAt = Now,
        };

        db.WorkspaceTags.Add(tag);
        await db.SaveChangesAsync(Ct);

        return tag.Id;
    }

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    private async Task<IReadOnlyList<string>> TitlesAsync(GatewayClient client, string query)
    {
        var body = await ReadAsync(await client.GetAsync(AssetsIn(_fixture.WorkspaceA, query), Ct));

        return [.. body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("title").GetString()!)];
    }

    private static IEnumerable<Guid> Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid());

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
