using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}/content</c> (DAM-006): the bytes, the headers
/// that describe them, the conditional request, and what cannot be rendered.
/// </summary>
/// <remarks>
/// <para>
/// The headers are the contract here as much as the body, so most of these tests assert on them: the media type
/// comes from what was stored rather than what anyone claimed, the cache policy is the one approved for this route,
/// and a <c>Range</c> header is ignored rather than honoured.
/// </para>
/// <para>
/// Over a real in-memory object store, which also counts open reads — so the lease the facade hands back can be
/// shown to be released on the 200 path and the 304 path alike. A stream that nobody closed is the characteristic
/// failure of a proxied download and is invisible to a test that only reads the body.
/// </para>
/// </remarks>
public sealed class MediaAssetContentEndpointTests : IAsyncLifetime
{
    private readonly InMemoryPrivateObjectStore _store = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A small but real JPEG-shaped payload. Only its bytes matter, not its decodability.</summary>
    private static readonly byte[] Bytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03, 0xFF, 0xD9];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ContentIn(SeededWorkspace workspace, Guid assetId) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}/content";

    private static string DownloadIn(SeededWorkspace workspace, Guid assetId) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}/download";

    private static string VersionIn(SeededWorkspace workspace, Guid assetId, int versionNumber) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}"
        + $"/versions/{versionNumber}/download";

    // ---- Rendering ----

    [Fact]
    public async Task The_current_versions_bytes_come_back_inline_with_their_stored_media_type()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Bytes, body);

        // Established by reading the bytes when they were stored, never from what a client declared.
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);

        // Nothing may be sniffed into another type.
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    /// <summary>
    /// The one header that must never carry a creator's words on this route: 12.9g owns the filename, because that
    /// is where a creator is saving a file rather than looking at one.
    /// </summary>
    [Fact]
    public async Task A_render_names_no_file()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);

        Assert.Null(response.Content.Headers.ContentDisposition?.FileName);
        Assert.Null(response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    /// <summary>
    /// The bytes are proxied, so there is no redirect and nothing in the response that names where they live.
    /// </summary>
    [Fact]
    public async Task A_render_is_not_a_redirect_and_leaks_no_object_key()
    {
        var id = await SeededAsync();
        var key = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 1);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);

        var headers = string.Join(
            '\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));

        Assert.DoesNotContain(key, headers, StringComparison.Ordinal);
        Assert.DoesNotContain("media-assets", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("workspaces/", headers, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cache policy approved for this route: revalidate every time, never sit in a shared proxy, and no
    /// <c>max-age</c> — because the URL serves whichever version is current, so a stale window would show a creator
    /// the wrong image with no way to tell why.
    /// </summary>
    [Fact]
    public async Task A_render_is_private_and_revalidated_rather_than_cached_for_a_while()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var cache = (await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct)).Headers.CacheControl;

        Assert.NotNull(cache);
        Assert.True(cache.Private);
        Assert.True(cache.NoCache);
        Assert.Null(cache.MaxAge);
        Assert.False(cache.Public);
        Assert.False(cache.NoStore);
    }

    [Fact]
    public async Task A_render_offers_no_ranges_and_ignores_a_range_header()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var plain = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(["none"], plain.Headers.AcceptRanges);

        var ranged = await client.SendAsync(
            HttpMethod.Get,
            ContentIn(_fixture.WorkspaceA, id),
            body: null,
            new Dictionary<string, string> { ["Range"] = "bytes=0-3" },
            Ct);

        // The whole image, not a 206 and not a 416: this contract has no ranges to honour or to refuse.
        Assert.Equal(HttpStatusCode.OK, ranged.StatusCode);
        Assert.Equal(Bytes, await ranged.Content.ReadAsByteArrayAsync(Ct));
    }

    // ---- Conditional requests ----

    [Fact]
    public async Task A_matching_entity_tag_answers_not_modified_with_no_body()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);
        var tag = first.Headers.ETag;

        Assert.NotNull(tag);
        Assert.False(tag.IsWeak);

        var second = await client.SendAsync(
            HttpMethod.Get,
            ContentIn(_fixture.WorkspaceA, id),
            body: null,
            new Dictionary<string, string> { ["If-None-Match"] = tag.ToString() },
            Ct);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(Ct));

        // The tag still comes back, so a client revalidating twice does not lose it.
        Assert.Equal(tag, second.Headers.ETag);
    }

    [Fact]
    public async Task A_stale_entity_tag_answers_the_bytes()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.SendAsync(
            HttpMethod.Get,
            ContentIn(_fixture.WorkspaceA, id),
            body: null,
            new Dictionary<string, string> { ["If-None-Match"] = "\"sha256:something-else\"" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// Two assets with different bytes have different tags, so a client caching one cannot be served the other on a
    /// revalidation. The tag is the checksum, which is what makes that true by construction.
    /// </summary>
    [Fact]
    public async Task Different_bytes_have_different_entity_tags()
    {
        var one = await SeededAsync();
        var two = await SeededAsync(bytes: [.. Bytes, 0x00]);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = (await client.GetAsync(ContentIn(_fixture.WorkspaceA, one), Ct)).Headers.ETag;
        var second = (await client.GetAsync(ContentIn(_fixture.WorkspaceA, two), Ct)).Headers.ETag;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Adding a version changes what this URL serves and therefore its tag — which is the whole reason the route
    /// revalidates rather than carrying a <c>max-age</c>. A client holding the old tag gets the new bytes.
    /// </summary>
    [Fact]
    public async Task A_new_current_version_changes_the_bytes_and_the_tag()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var before = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);
        var oldTag = before.Headers.ETag!;

        byte[] replacement = [.. Bytes, 0x7F, 0x7F];
        await AddVersionAsync(id, versionNumber: 2, replacement);

        var after = await client.SendAsync(
            HttpMethod.Get,
            ContentIn(_fixture.WorkspaceA, id),
            body: null,
            new Dictionary<string, string> { ["If-None-Match"] = oldTag.ToString() },
            Ct);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(replacement, await after.Content.ReadAsByteArrayAsync(Ct));
        Assert.NotEqual(oldTag, after.Headers.ETag);
    }

    /// <summary>
    /// The current version is what the asset's counter names, not the highest row — so an asset still pointing at
    /// version 1 renders version 1 even once a second row exists.
    /// </summary>
    [Fact]
    public async Task A_version_row_the_asset_does_not_point_at_is_not_rendered()
    {
        var id = await SeededAsync();
        await AddVersionAsync(id, versionNumber: 2, [.. Bytes, 0x11], makeCurrent: false);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(Bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// The open read is released on both paths. A leaked stream is the characteristic failure of a proxied download
    /// and a test that only checks the body cannot see it.
    /// </summary>
    [Fact]
    public async Task The_open_read_is_released_on_both_the_bytes_and_the_not_modified_path()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);
        await first.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(0, _store.OpenReads);

        var notModified = await client.SendAsync(
            HttpMethod.Get,
            ContentIn(_fixture.WorkspaceA, id),
            body: null,
            new Dictionary<string, string> { ["If-None-Match"] = first.Headers.ETag!.ToString() },
            Ct);

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(0, _store.OpenReads);
    }

    // ---- Nothing to render ----

    /// <summary>
    /// Four causes, one answer. A caller cannot tell an unknown asset from a neighbour's, from a removed one, from
    /// one whose version row is missing — so none of them discloses that an asset exists elsewhere.
    /// </summary>
    [Fact]
    public async Task Every_reason_there_is_nothing_to_render_is_the_same_not_found()
    {
        var unknown = Guid.NewGuid();
        var deleted = await SeededAsync(deletedAt: Now);
        var versionless = await SeededAsync(withVersion: false);
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var codes = new List<string?>();

        foreach (var assetId in (Guid[])[unknown, deleted, versionless, inB])
        {
            var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, assetId), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add((await ReadAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal([MediaErrorCodes.AssetNotFound], codes.Distinct());
    }

    /// <summary>
    /// A row that says the bytes are there, with no object behind it. The gateway answers absent, which is a 404 and
    /// not a 503: there is nothing to retry for.
    /// </summary>
    [Fact]
    public async Task A_version_whose_object_is_missing_is_a_not_found()
    {
        var id = await SeededAsync(storeBytes: false);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Storage unreachable is deliberately <em>not</em> a 404: the asset exists, so telling a creator it is gone
    /// would be wrong, and retrying is the remedy rather than going back to a list.
    /// </summary>
    [Fact]
    public async Task Storage_being_unreachable_is_a_service_unavailable_rather_than_a_not_found()
    {
        var id = await SeededAsync();
        _store.Unavailable = true;

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetStorageUnavailable,
            (await ReadAsync(response)).GetProperty("code").GetString());

        // And nothing was left open by the failed attempt.
        Assert.Equal(0, _store.OpenReads);
    }

    /// <summary>A soft-deleted asset stops rendering the moment it is removed, with its bytes still in the store.</summary>
    [Fact]
    public async Task Removing_an_asset_stops_it_rendering_while_its_bytes_remain()
    {
        var id = await SeededAsync();
        var key = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 1);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct)).StatusCode);

        await SoftDeleteAsync(id);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct)).StatusCode);

        // The object is untouched, which is what makes the deletion reversible in principle (12.9e).
        Assert.Contains(key, _store.Keys);
    }

    // ---- Membership and isolation ----

    [Fact]
    public async Task Every_member_including_a_viewer_may_render()
    {
        var id = await SeededAsync();

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);

        Assert.Equal(
            HttpStatusCode.OK,
            (await member.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct)).StatusCode);
    }

    /// <summary>
    /// A's asset rendered from B's own route, and from A's slug by B's owner: 404 both times, and B's own asset
    /// still renders — so the refusal is isolation rather than a broken route.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_render_the_others_asset()
    {
        var inA = await SeededAsync();
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var route in (string[])
            [ContentIn(_fixture.WorkspaceB, inA), ContentIn(_fixture.WorkspaceA, inA)])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync(route, Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await ownerB.GetAsync(ContentIn(_fixture.WorkspaceB, inB), Ct)).StatusCode);
    }

    /// <summary>
    /// The gateway re-checks the key's workspace, so even an object written under A's prefix cannot be reached from
    /// B — the second guard behind the query filter that produced the key.
    /// </summary>
    [Fact]
    public async Task An_asset_row_pointing_at_the_other_workspaces_object_renders_nothing()
    {
        // B's asset, with its version row pointing at a key under A's prefix. Only reachable by writing the row
        // directly, which is the point: if it ever happened, the gateway refuses rather than serving A's bytes.
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB, storeBytes: false);
        var foreignKey = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, Guid.NewGuid(), 1);

        await PutAsync(foreignKey, Bytes);
        await RepointVersionAsync(_fixture.WorkspaceB, inB, foreignKey);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var response = await ownerB.GetAsync(ContentIn(_fixture.WorkspaceB, inB), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Download ----

    /// <summary>
    /// The same bytes as the render, with two headers different: an attachment disposition and a named file.
    /// </summary>
    [Fact]
    public async Task A_download_is_an_attachment_named_from_the_title_version_and_type()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Bytes, await response.Content.ReadAsByteArrayAsync(Ct));

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("attachment", disposition?.DispositionType);
        Assert.Equal("soda-bread-hero-v1.jpg", disposition?.FileName);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The name follows the current version, so downloading after a new version lands gives a differently named
    /// file — which is what keeps two saves from colliding.
    /// </summary>
    [Fact]
    public async Task A_download_names_the_version_it_actually_served()
    {
        var id = await SeededAsync();
        await AddVersionAsync(id, versionNumber: 2, [.. Bytes, 0x2A]);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal("soda-bread-hero-v2.jpg", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal([.. Bytes, 0x2A], await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// The extension describes the stored bytes, not the uploaded filename. The seed claims a .jpg upload name while
    /// the bytes are stored as a PNG, and the download is named .png.
    /// </summary>
    [Fact]
    public async Task The_extension_follows_the_stored_type_not_the_uploaded_name()
    {
        var id = await SeededAsync(mediaType: "image/png");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal("soda-bread-hero-v1.png", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A title carrying a traversal, a quote or a header separator produces a name with none of them. The unit tests
    /// pin the function; this proves the name reaches the header intact and parseable.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "etc-passwd-v1.jpg")]
    [InlineData("soda; filename=other.jpg", "soda-filename-other-jpg-v1.jpg")]
    [InlineData("!!!", "image-v1.jpg")]
    public async Task A_dangerous_title_is_safe_by_the_time_it_reaches_the_header(string title, string expected)
    {
        var id = await SeededAsync(title: title);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        // Parsed by the client's own header parser, so a name that broke the header would not arrive at all.
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(expected, response.Content.Headers.ContentDisposition?.FileName);
        Assert.False(response.Headers.Contains("X-Injected"));
    }

    /// <summary>
    /// Caching, the entity tag, the range policy and sniffing protection are the render's, because the two actions
    /// share one sender. Asserted rather than assumed, so a change reaching only one would fail here.
    /// </summary>
    [Fact]
    public async Task A_download_carries_the_same_cache_tag_and_range_policy_as_the_render()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var render = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);
        var download = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(render.Headers.ETag, download.Headers.ETag);
        Assert.Equal(render.Headers.CacheControl?.ToString(), download.Headers.CacheControl?.ToString());
        Assert.Equal(render.Headers.AcceptRanges, download.Headers.AcceptRanges);
        Assert.Equal(
            Assert.Single(render.Headers.GetValues("X-Content-Type-Options")),
            Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task A_download_answers_not_modified_for_a_matching_tag()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        var second = await client.SendAsync(
            HttpMethod.Get,
            DownloadIn(_fixture.WorkspaceA, id),
            body: null,
            new Dictionary<string, string> { ["If-None-Match"] = first.Headers.ETag!.ToString() },
            Ct);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(0, _store.OpenReads);
    }

    /// <summary>
    /// The download refuses everything the render refuses, with the same code — neither route is a softer way in.
    /// </summary>
    [Fact]
    public async Task Every_reason_there_is_nothing_to_render_refuses_a_download_too()
    {
        var deleted = await SeededAsync(deletedAt: Now);
        var versionless = await SeededAsync(withVersion: false);
        var objectless = await SeededAsync(storeBytes: false);
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var assetId in (Guid[])[Guid.NewGuid(), deleted, versionless, objectless, inB])
        {
            var response = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, assetId), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                MediaErrorCodes.AssetNotFound,
                (await ReadAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task A_download_cannot_reach_the_other_workspaces_asset()
    {
        var inA = await SeededAsync();
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var route in (string[])
            [DownloadIn(_fixture.WorkspaceB, inA), DownloadIn(_fixture.WorkspaceA, inA)])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync(route, Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await ownerB.GetAsync(DownloadIn(_fixture.WorkspaceB, inB), Ct)).StatusCode);
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_download()
    {
        var id = await SeededAsync();

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);

        Assert.Equal(
            HttpStatusCode.OK,
            (await member.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_download_leaks_no_object_key_and_is_not_a_redirect()
    {
        var id = await SeededAsync();
        var key = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 1);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct);

        Assert.Null(response.Headers.Location);

        var headers = string.Join(
            '\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));

        Assert.DoesNotContain(key, headers, StringComparison.Ordinal);
        Assert.DoesNotContain("media-assets", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("workspaces/", headers, StringComparison.Ordinal);
    }

    // ---- Named-version download ----

    /// <summary>
    /// Each version downloads its own bytes under its own name, so a creator can retrieve the one they asked for.
    /// </summary>
    [Fact]
    public async Task A_named_version_downloads_its_own_bytes_and_name()
    {
        var id = await SeededAsync();
        byte[] second = [.. Bytes, 0x5A, 0x5B];
        await AddVersionAsync(id, versionNumber: 2, second);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var one = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct);
        var two = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 2), Ct);

        Assert.Equal(Bytes, await one.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("soda-bread-hero-v1.jpg", one.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal(second, await two.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("soda-bread-hero-v2.jpg", two.Content.Headers.ContentDisposition?.FileName);

        // Two files, two names: the -v{n} suffix earning its place.
        Assert.NotEqual(
            one.Content.Headers.ContentDisposition?.FileName,
            two.Content.Headers.ContentDisposition?.FileName);
    }

    /// <summary>
    /// An older version stays downloadable after a newer one becomes current — which is what a history download is
    /// for, and what makes the immutable version rows worth keeping.
    /// </summary>
    [Fact]
    public async Task An_older_version_stays_downloadable_once_a_newer_one_is_current()
    {
        var id = await SeededAsync();
        byte[] second = [.. Bytes, 0x6C];
        await AddVersionAsync(id, versionNumber: 2, second);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        // The current-version routes now serve version 2.
        Assert.Equal(second, await (await client.GetAsync(DownloadIn(_fixture.WorkspaceA, id), Ct))
            .Content.ReadAsByteArrayAsync(Ct));

        // And version 1 is still reachable by name.
        Assert.Equal(Bytes, await (await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct))
            .Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// **It never falls back.** A number this asset has no version for is refused, not answered with whatever version
    /// does exist — which would hand a caller bytes they did not ask for with no way to tell.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_version_this_asset_does_not_have_is_refused_rather_than_substituted(int versionNumber)
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, versionNumber), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());

        // Nothing was sent, which is the assertion that distinguishes a refusal from a silent substitution.
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    /// <summary>
    /// **Both identifiers together.** Two assets each have a version 2 with different bytes; asking one asset's route
    /// for that number must never reach the other's. This is the test a lookup keyed on the version number alone
    /// fails.
    /// </summary>
    [Fact]
    public async Task One_assets_route_cannot_serve_another_assets_version()
    {
        var mine = await SeededAsync(title: "Mine");
        var other = await SeededAsync(title: "Other");

        byte[] myTwo = [.. Bytes, 0x01];
        byte[] theirTwo = [.. Bytes, 0x02];

        await AddVersionAsync(mine, versionNumber: 2, myTwo);
        await AddVersionAsync(other, versionNumber: 2, theirTwo);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var fromMine = await client.GetAsync(VersionIn(_fixture.WorkspaceA, mine, 2), Ct);
        var fromOther = await client.GetAsync(VersionIn(_fixture.WorkspaceA, other, 2), Ct);

        Assert.Equal(myTwo, await fromMine.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("mine-v2.jpg", fromMine.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal(theirTwo, await fromOther.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("other-v2.jpg", fromOther.Content.Headers.ContentDisposition?.FileName);
    }

    /// <summary>
    /// An asset with no version 1 of its own cannot borrow one that exists only on another asset.
    /// </summary>
    [Fact]
    public async Task An_asset_cannot_borrow_a_version_number_it_never_had()
    {
        var withTwo = await SeededAsync();
        await AddVersionAsync(withTwo, versionNumber: 2, [.. Bytes, 0x03]);

        var withOneOnly = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(VersionIn(_fixture.WorkspaceA, withOneOnly, 2), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A version download's extension follows that version's own stored type, so an asset whose newer version is a
    /// PNG still downloads its JPEG version as a .jpg.
    /// </summary>
    [Fact]
    public async Task Each_versions_extension_follows_that_versions_stored_type()
    {
        var id = await SeededAsync(mediaType: "image/jpeg");
        await AddVersionAsync(id, versionNumber: 2, [.. Bytes, 0x04], mediaType: "image/png");

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var one = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct);
        var two = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 2), Ct);

        Assert.Equal("soda-bread-hero-v1.jpg", one.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/jpeg", one.Content.Headers.ContentType?.MediaType);

        Assert.Equal("soda-bread-hero-v2.png", two.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/png", two.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Caching, the tag, the range policy and sniffing are the same as the other two byte routes, because all three
    /// share one sender. Asserted so a change reaching one and not the others fails here.
    /// </summary>
    [Fact]
    public async Task A_version_download_carries_the_same_policy_as_the_other_byte_routes()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var render = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id), Ct);
        var version = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct);

        Assert.Equal(render.Headers.ETag, version.Headers.ETag);
        Assert.Equal(render.Headers.CacheControl?.ToString(), version.Headers.CacheControl?.ToString());
        Assert.Equal(render.Headers.AcceptRanges, version.Headers.AcceptRanges);
        Assert.Equal(
            Assert.Single(render.Headers.GetValues("X-Content-Type-Options")),
            Assert.Single(version.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("attachment", version.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task A_version_download_answers_not_modified_and_releases_its_read()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct);

        var second = await client.SendAsync(
            HttpMethod.Get,
            VersionIn(_fixture.WorkspaceA, id, 1),
            body: null,
            new Dictionary<string, string> { ["If-None-Match"] = first.Headers.ETag!.ToString() },
            Ct);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(0, _store.OpenReads);
    }

    /// <summary>
    /// Removing an asset takes its history out of reach too, not only its latest bytes — so a named version of a
    /// tombstoned asset is a 404 even though the rows and the objects are all still there.
    /// </summary>
    [Fact]
    public async Task A_removed_assets_history_is_out_of_reach()
    {
        var id = await SeededAsync();
        await AddVersionAsync(id, versionNumber: 2, [.. Bytes, 0x05]);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct)).StatusCode);

        await SoftDeleteAsync(id);

        foreach (var versionNumber in (int[])[1, 2])
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, versionNumber), Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_named_version_whose_object_is_missing_is_a_not_found()
    {
        var id = await SeededAsync(storeBytes: false);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_version_download_cannot_reach_the_other_workspaces_asset()
    {
        var inA = await SeededAsync();
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var route in (string[])
            [VersionIn(_fixture.WorkspaceB, inA, 1), VersionIn(_fixture.WorkspaceA, inA, 1)])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync(route, Ct)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.OK, (await ownerB.GetAsync(VersionIn(_fixture.WorkspaceB, inB, 1), Ct)).StatusCode);
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_download_a_version()
    {
        var id = await SeededAsync();

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);

        Assert.Equal(
            HttpStatusCode.OK,
            (await member.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_version_download_leaks_no_object_key()
    {
        var id = await SeededAsync();
        var key = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 1);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(VersionIn(_fixture.WorkspaceA, id, 1), Ct);

        Assert.Null(response.Headers.Location);

        var headers = string.Join(
            '\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));

        Assert.DoesNotContain(key, headers, StringComparison.Ordinal);
        Assert.DoesNotContain("media-assets", headers, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail, cancellationToken: Ct);

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    private async Task PutAsync(string objectKey, byte[] bytes, string mediaType = "image/jpeg")
    {
        using var content = new MemoryStream(bytes);

        await _store.PutAsync(
            MediaAssetObjectKey.Container, objectKey, content, mediaType, bytes.Length + 1, Ct);
    }

    /// <summary>
    /// An asset, its first version, and the object the version names.
    /// </summary>
    /// <param name="storeBytes">
    /// False writes the row and no object, which is the state a lost or never-written object leaves behind.
    /// </param>
    /// <param name="withVersion">
    /// False writes an asset whose <c>CurrentVersionNumber</c> names no row — reachable in principle only, and
    /// modelled here because the render has to answer it rather than throw.
    /// </param>
    private async Task<Guid> SeededAsync(
        SeededWorkspace? workspace = null,
        byte[]? bytes = null,
        bool withVersion = true,
        bool storeBytes = true,
        DateTimeOffset? deletedAt = null,
        string title = "Soda bread hero",
        string mediaType = "image/jpeg")
    {
        var target = workspace ?? _fixture.WorkspaceA;
        var payload = bytes ?? Bytes;

        await using var scope = ScopeFor(target);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        if (withVersion)
        {
            var key = MediaAssetObjectKey.For(target.Id, asset.Id, 1);

            asset.Versions.Add(NewVersion(asset.Id, 1, key, payload, mediaType));

            if (storeBytes)
            {
                await PutAsync(key, payload, mediaType);
            }
        }

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    private static MediaAssetVersion NewVersion(
        Guid mediaAssetId, int versionNumber, string objectKey, byte[] payload,
        string mediaType = "image/jpeg") => new()
        {
            Id = Guid.NewGuid(),
            MediaAssetId = mediaAssetId,
            VersionNumber = versionNumber,
            MediaType = mediaType,
            SizeBytes = payload.Length,
            Width = 1600,
            Height = 1200,

            // Not the real digest: the response's entity tag is built from what the *store* measured, so a row's
            // checksum is never what a client sees. Deliberately distinct so a test asserting on the tag cannot
            // pass by reading this instead.
            ContentChecksum = $"sha256:{Convert.ToHexString(Guid.NewGuid().ToByteArray())}",
            ObjectKey = objectKey,
            OriginalFileName = "soda-bread.jpg",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

    /// <summary>Adds a version and its object, pointing the asset at it unless told otherwise.</summary>
    private async Task AddVersionAsync(
        Guid assetId,
        int versionNumber,
        byte[] payload,
        bool makeCurrent = true,
        string mediaType = "image/jpeg")
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var key = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, assetId, versionNumber);
        await PutAsync(key, payload, mediaType);

        db.MediaAssetVersions.Add(NewVersion(assetId, versionNumber, key, payload, mediaType));

        if (makeCurrent)
        {
            var asset = await db.MediaAssets.SingleAsync(candidate => candidate.Id == assetId, Ct);
            asset.CurrentVersionNumber = versionNumber;
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Points a version row at a different object key, with raw SQL.
    /// </summary>
    /// <remarks>
    /// <c>MediaAssetVersion</c> is <c>IImmutableRecord</c>, so the change tracker refuses to update one — correctly.
    /// The state being set up is one no write path can produce, so that the gateway's own key check can be shown to
    /// hold even if a row ever named somebody else's object.
    /// </remarks>
    private async Task RepointVersionAsync(SeededWorkspace workspace, Guid assetId, string objectKey)
    {
        await using var scope = ScopeFor(workspace);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database
            .ExecuteSqlRawAsync(
                "UPDATE MediaAssetVersions SET ObjectKey = {0} WHERE MediaAssetId = {1};",
                [objectKey, assetId],
                Ct);
    }

    private async Task SoftDeleteAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var asset = await db.MediaAssets.SingleAsync(candidate => candidate.Id == assetId, Ct);
        asset.DeletedAt = Now;
        asset.DeletedByMembershipId = Guid.NewGuid();

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
