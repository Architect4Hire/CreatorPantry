using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.FileInspection;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The 15.2 upload pass at the DAM's own routes, through the real gateway: polyglots, trailers, the scanner, the
/// edge limit, the location report, and two workspaces that never see each other's uploads.
/// </summary>
public sealed class DamUploadSecurityEndpointTests : IAsyncLifetime
{
    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly FakeMalwareScanGateway _scanner = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(_scanner);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AssetsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/dam-assets";

    // ---- malicious and polyglot -------------------------------------------------------------------------

    public static TheoryData<string, byte[]> Polyglots() => new()
    {
        { "png+zip", HostileSamples.AppendZip(BrandSourceSampleFiles.Png()) },
        { "jpeg+zip", HostileSamples.AppendZip(BrandSourceSampleFiles.Jpeg()) },
        { "gif+zip", HostileSamples.AppendZip(BrandSourceSampleFiles.Gif()) },
        { "webp+zip", HostileSamples.AppendZip(BrandSourceSampleFiles.Webp()) },
        { "png hiding a zip", HostileSamples.PngHidingZip() },
        { "png+html", HostileSamples.Concat(BrandSourceSampleFiles.Png(), Encoding.ASCII.GetBytes("<script>alert(1)</script>")) },
    };

    [Theory]
    [MemberData(nameof(Polyglots))]
    public async Task A_picture_that_is_also_something_else_is_refused_before_anything_is_stored(string kind, byte[] bytes)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, bytes, "photo.png");

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, kind);
        Assert.Equal(MediaErrorCodes.AssetUnsupported, (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_store.Keys);
        Assert.Equal(0, _scanner.Scans);
    }

    [Fact]
    public async Task A_motion_photo_is_refused_with_a_way_forward()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(
            client, _fixture.WorkspaceA, HostileSamples.Concat(BrandSourceSampleFiles.Jpeg(), HostileSamples.Mp4Box()), "PXL_0042.MP.jpg");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("still", (await ReadAsync(response)).GetRawText(), StringComparison.Ordinal);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task A_phone_photo_with_a_gain_map_after_it_is_accepted()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(
            client,
            _fixture.WorkspaceA,
            HostileSamples.Concat(BrandSourceSampleFiles.Jpeg(width: 40, height: 30), BrandSourceSampleFiles.Jpeg(width: 10, height: 8)),
            "IMG_0042.jpg");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_picture_the_scanner_refuses_is_never_stored()
    {
        // A whole, valid PNG, with the scanner's marker in an ancillary chunk: only the scan can refuse it.
        var infected = new PngSampleFile
        {
            Width = 2, Height = 2, ColourType = 2, Samples = new byte[12],
            BeforeData = [("tEXt", Encoding.ASCII.GetBytes("Comment\0" + BrandSourceSampleFiles.MalwareMarker))],
        }.Build();
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, infected, "photo.png");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1, _scanner.Scans);
        Assert.Empty(_store.Keys);
        Assert.DoesNotContain(BrandSourceSampleFiles.MalwareMarker, (await ReadAsync(response)).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_scanner_verdict_stores_nothing()
    {
        _scanner.Unavailable = true;
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, BrandSourceSampleFiles.Png(), "photo.png");

        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(_store.Keys);
    }

    // ---- oversized ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_body_past_the_routes_limit_is_refused_at_the_edge()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(
            client, _fixture.WorkspaceA, new byte[MediaPolicy.AssetUploadRequestMaxBytes + 1], "huge.png");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task A_file_over_the_image_cap_inside_the_body_limit_is_refused_by_the_api()
    {
        // A real PNG padded inside a chunk to one byte over 32 MB: through the edge, and refused by size alone.
        var padding = (int)MediaPolicy.ImageMaxBytes - BrandSourceSampleFiles.Png().Length - 11;
        var oversized = new PngSampleFile
        {
            Width = 4, Height = 3, ColourType = 2, Samples = new byte[36], BeforeData = [("ruSt", new byte[padding])],
        }.Build();
        Assert.True(oversized.Length > MediaPolicy.ImageMaxBytes);
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, oversized, "photo.png");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_store.Keys);
    }

    // ---- location metadata -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_photograph_carrying_a_position_is_kept_as_uploaded_and_reported()
    {
        var photo = HostileSamples.JpegWithExif(withPosition: true);
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var created = await CreatedAsync(client, _fixture.WorkspaceA, photo);
        var version = await CurrentVersionAsync(client, _fixture.WorkspaceA, created);

        Assert.True(version.GetProperty("containsLocationMetadata").GetBoolean());

        // Not stripped: the stored original is the uploaded bytes exactly.
        var stored = Assert.Single(_store.Keys);
        Assert.Equal(photo, await ReadObjectAsync(stored));
    }

    [Fact]
    public async Task A_photograph_with_no_position_says_so()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var created = await CreatedAsync(client, _fixture.WorkspaceA, HostileSamples.JpegWithExif(withPosition: false));

        Assert.False((await CurrentVersionAsync(client, _fixture.WorkspaceA, created)).GetProperty("containsLocationMetadata").GetBoolean());
    }

    [Fact]
    public async Task A_new_version_reports_its_own_position_not_its_predecessors()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var created = await CreatedAsync(client, _fixture.WorkspaceA, BrandSourceSampleFiles.Png());

        var form = FormFor(HostileSamples.JpegWithXmpPosition(), "second.jpg");
        var response = await client.PostAsync($"{AssetsIn(_fixture.WorkspaceA)}/{created:D}/versions", form, null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("containsLocationMetadata").GetBoolean());
    }

    // ---- duplicate and isolation -------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_bytes_twice_are_two_objects_under_two_server_keys()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var bytes = BrandSourceSampleFiles.Png();

        var first = await CreatedAsync(client, _fixture.WorkspaceA, bytes, "same.png");
        var second = await CreatedAsync(client, _fixture.WorkspaceA, bytes, "same.png");

        Assert.NotEqual(first, second);
        Assert.Equal(2, _store.Keys.Count());

        // Neither key carries the filename the creator chose.
        Assert.All(_store.Keys, key => Assert.DoesNotContain("same", key, StringComparison.Ordinal));
        Assert.All(_store.Keys, key => Assert.True(ObjectKeyGrammar.IsWellFormed(key), key));
    }

    [Fact]
    public async Task Each_workspaces_upload_lands_under_its_own_workspace_and_is_not_the_others_to_read()
    {
        using var mine = await SignInAsync(_fixture.WorkspaceA);
        using var theirs = await SignInAsync(_fixture.WorkspaceB);

        // The same bytes from both sides: what separates the two objects is the server's key, never the content.
        var bytes = HostileSamples.JpegWithExif(withPosition: true);
        var ours = await CreatedAsync(mine, _fixture.WorkspaceA, bytes);
        var their = await CreatedAsync(theirs, _fixture.WorkspaceB, bytes);

        Assert.Single(_store.Keys, key => key.StartsWith(MediaAssetObjectKey.PrefixFor(_fixture.WorkspaceA.Id), StringComparison.Ordinal));
        Assert.Single(_store.Keys, key => key.StartsWith(MediaAssetObjectKey.PrefixFor(_fixture.WorkspaceB.Id), StringComparison.Ordinal));
        Assert.Equal(2, _store.Keys.Count());

        // Each reads its own, location report included.
        Assert.True((await CurrentVersionAsync(theirs, _fixture.WorkspaceB, their)).GetProperty("containsLocationMetadata").GetBoolean());

        // B reading A's asset — at B's slug, and at A's where B is no member — and adding a version to it, is the
        // one answer an asset nobody has gets.
        var unknown = await ProblemAsync(await theirs.GetAsync($"{AssetsIn(_fixture.WorkspaceB)}/{Guid.NewGuid():D}/content", Ct));

        Assert.Equal(unknown, await ProblemAsync(await theirs.GetAsync($"{AssetsIn(_fixture.WorkspaceB)}/{ours:D}/content", Ct)));
        Assert.Equal(unknown, await ProblemAsync(await theirs.GetAsync($"{AssetsIn(_fixture.WorkspaceB)}/{ours:D}", Ct)));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await theirs.GetAsync($"{AssetsIn(_fixture.WorkspaceA)}/{ours:D}/content", Ct)).StatusCode);

        var keysBefore = _store.Keys.ToList();
        var write = await theirs.PostAsync(
            $"{AssetsIn(_fixture.WorkspaceB)}/{ours:D}/versions", FormFor(BrandSourceSampleFiles.Png(), "mine.png"), null, Ct);

        Assert.Equal(unknown, await ProblemAsync(write));
        Assert.Equal(keysBefore.Order(), _store.Keys.Order());
    }

    /// <summary>Status and stable code: what a client can tell one not-found from another by.</summary>
    private static async Task<(HttpStatusCode Status, string? Code)> ProblemAsync(HttpResponseMessage response) =>
        (response.StatusCode, (await ReadAsync(response)).GetProperty("code").GetString());

    // ---- helpers ---------------------------------------------------------------------------------------------

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static MultipartFormDataContent FormFor(byte[] bytes, string fileName, string? title = null)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "file", fileName);

        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        return form;
    }

    private static Task<HttpResponseMessage> UploadAsync(
        GatewayClient client, SeededWorkspace workspace, byte[] bytes, string fileName) =>
        client.PostAsync(AssetsIn(workspace), FormFor(bytes, fileName, "A photograph"), null, Ct);

    private static async Task<Guid> CreatedAsync(
        GatewayClient client, SeededWorkspace workspace, byte[] bytes, string fileName = "photo.jpg")
    {
        var response = await UploadAsync(client, workspace, bytes, fileName);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CurrentVersionAsync(GatewayClient client, SeededWorkspace workspace, Guid assetId)
    {
        var response = await client.GetAsync($"{AssetsIn(workspace)}/{assetId:D}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("currentVersion");
    }

    private async Task<byte[]> ReadObjectAsync(string objectKey)
    {
        var opened = await _store.OpenReadAsync(MediaAssetObjectKey.Container, objectKey, Ct);

        Assert.NotNull(opened);

        await using (opened)
        {
            using var buffer = new MemoryStream();
            await opened.Content.CopyToAsync(buffer, Ct);

            return buffer.ToArray();
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
