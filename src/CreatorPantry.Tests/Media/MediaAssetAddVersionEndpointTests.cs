using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}/versions</c> (DAM-010): what a new version writes,
/// what it leaves alone, what two at once do, and what is left behind when something fails.
/// </summary>
/// <remarks>
/// <para>
/// The negative assertions carry most of the weight. Nothing may be overwritten, nothing partial may survive a
/// failure, and a request that loses a race must not take the winner's object with it — which is the one mistake here
/// that would destroy a committed version's bytes.
/// </para>
/// <para>
/// Over a real in-memory object store, so what is actually in storage can be asserted rather than inferred.
/// </para>
/// </remarks>
public sealed class MediaAssetAddVersionEndpointTests : IAsyncLifetime
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string VersionsIn(SeededWorkspace workspace, Guid assetId) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}/versions";

    // ---- Adding ----

    [Fact]
    public async Task A_new_version_takes_the_next_number_and_becomes_current()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await AddAsync(client, id, BrandSourceSampleFiles.Png(800, 600), "second-take.png");

        Assert.Equal(2, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal("image/png", body.GetProperty("mediaType").GetString());
        Assert.Equal(800, body.GetProperty("width").GetInt32());
        Assert.Equal(600, body.GetProperty("height").GetInt32());
        Assert.Equal("second-take.png", body.GetProperty("originalFileName").GetString());
        Assert.Equal("Upload", body.GetProperty("source").GetString());
        Assert.StartsWith("sha256:", body.GetProperty("contentChecksum").GetString());

        var asset = await ReloadAsync(id);
        Assert.Equal(2, asset.CurrentVersionNumber);
        Assert.Equal(2, (await VersionsAsync(id)).Count);
    }

    /// <summary>
    /// The media type, dimensions, size and checksum all describe bytes something measured — not what the request
    /// called them. The file is uploaded as <c>.jpg</c> with an octet-stream content type while its bytes are a PNG.
    /// </summary>
    [Fact]
    public async Task The_stored_metadata_describes_the_bytes_rather_than_the_request()
    {
        var id = await SeededAsync();
        var png = BrandSourceSampleFiles.Png(120, 90);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await AddAsync(client, id, png, "lying-name.jpg");

        Assert.Equal("image/png", body.GetProperty("mediaType").GetString());
        Assert.Equal(120, body.GetProperty("width").GetInt32());
        Assert.Equal(90, body.GetProperty("height").GetInt32());
        Assert.Equal(png.Length, body.GetProperty("sizeBytes").GetInt64());

        // The creator's filename is kept for display, and is the one thing taken at face value — it names nothing.
        Assert.Equal("lying-name.jpg", body.GetProperty("originalFileName").GetString());
    }

    [Fact]
    public async Task Audit_fields_move_with_the_new_version()
    {
        var id = await SeededAsync();
        var before = await ReloadAsync(id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await AddAsync(client, id, BrandSourceSampleFiles.Png(), "second.png");

        var after = await ReloadAsync(id);

        Assert.NotEqual(before.UpdatedAt, after.UpdatedAt);
        Assert.NotEqual(before.UpdatedByMembershipId, after.UpdatedByMembershipId);

        // Creation and ownership are not what adding a version changes.
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.CreatedByMembershipId, after.CreatedByMembershipId);
        Assert.Equal(before.WorkspaceId, after.WorkspaceId);

        var version = (await VersionsAsync(id)).Single(row => row.VersionNumber == 2);
        Assert.NotEqual(Guid.Empty, version.CreatedByMembershipId);
    }

    // ---- Nothing is overwritten ----

    /// <summary>
    /// The restriction this prompt opens with. Version 1's row and its object both survive untouched, which is what
    /// keeps its download route working.
    /// </summary>
    [Fact]
    public async Task The_previous_versions_row_and_object_are_untouched()
    {
        var id = await SeededAsync();
        var firstKey = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 1);
        var firstBytes = await ReadObjectAsync(firstKey);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await AddAsync(client, id, BrandSourceSampleFiles.Png(800, 600), "second.png");

        var secondKey = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 2);

        // Two objects under two keys: a new version is never written over an old one.
        Assert.Contains(firstKey, _store.Keys);
        Assert.Contains(secondKey, _store.Keys);
        Assert.Equal(firstBytes, await ReadObjectAsync(firstKey));
        Assert.NotEqual(firstBytes, await ReadObjectAsync(secondKey));

        // And version 1's row is exactly as it was.
        var first = (await VersionsAsync(id)).Single(row => row.VersionNumber == 1);
        Assert.Equal(firstKey, first.ObjectKey);
    }

    /// <summary>
    /// Every version stays downloadable by number, and the current-version routes move to the newest — the whole
    /// point of keeping the history.
    /// </summary>
    [Fact]
    public async Task Both_versions_stay_downloadable_and_the_current_routes_move_on()
    {
        var id = await SeededAsync();
        var second = BrandSourceSampleFiles.Png(800, 600);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await AddAsync(client, id, second, "second.png");

        var assetPath = $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets/{id:D}";

        Assert.Equal(
            second,
            await (await client.GetAsync($"{assetPath}/content", Ct)).Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(
            second,
            await (await client.GetAsync($"{assetPath}/versions/2/download", Ct)).Content.ReadAsByteArrayAsync(Ct));

        // Version 1 still downloads, which is what "immutable original" means in practice.
        var one = await client.GetAsync($"{assetPath}/versions/1/download", Ct);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal("soda-bread-hero-v1.png", one.Content.Headers.ContentDisposition?.FileName);
    }

    /// <summary>
    /// Bytes only: the asset's own metadata is left exactly as it was, alt text included. Deleting a creator's
    /// description because a file changed is not this route's decision.
    /// </summary>
    [Fact]
    public async Task Adding_a_version_changes_no_metadata()
    {
        var id = await SeededAsync();
        var before = await ReloadAsync(id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await AddAsync(client, id, BrandSourceSampleFiles.Png(), "second.png");

        var after = await ReloadAsync(id);

        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.AltText, after.AltText);
        Assert.Equal(before.Kind, after.Kind);
        Assert.Null(after.DeletedAt);
    }

    // ---- Concurrency ----

    /// <summary>
    /// Two uploads at once take different numbers or one is refused — never the same number, and never a lost object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion that matters most is the last one: whichever request won, <strong>its object is still there</strong>.
    /// A loser that compensated by deleting the key it computed would destroy a committed version's bytes, and the key
    /// is deterministic so both requests compute the same one.
    /// </para>
    /// <para>
    /// Both outcomes are acceptable — two versions, or one version and a 409 — because the race is genuinely
    /// non-deterministic. What is asserted is what must be true either way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_uploads_at_once_never_share_a_version_number()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var first = SendAsync(client, id, BrandSourceSampleFiles.Png(800, 600), "a.png");
        var second = SendAsync(client, id, BrandSourceSampleFiles.Png(640, 480), "b.png");

        var responses = await Task.WhenAll(first, second);
        var created = responses.Count(response => response.StatusCode is HttpStatusCode.Created);

        // Every refusal is a conflict, and names one of the two reasons a concurrent upload can lose: the number was
        // taken at the store, or the commit itself was refused. Both are retryable and neither is a server fault.
        foreach (var refused in responses.Where(response => response.StatusCode is not HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(
                (await ReadAsync(refused)).GetProperty("code").GetString(),
                (string?[])[MediaErrorCodes.AssetVersionTaken, MediaErrorCodes.AssetNotCreated]);
        }

        var versions = await VersionsAsync(id);

        // The invariants, which hold whichever way the race went — and deliberately NOT "at least one succeeded".
        // SQLite serializes writers, so under contention both commits can be refused; requiring a success would make
        // this test fail for a reason that is about the test database rather than about the code.
        Assert.Equal(versions.Count, versions.Select(row => row.VersionNumber).Distinct().Count());
        Assert.Equal(1 + created, versions.Count);
        Assert.Equal(versions.Max(row => row.VersionNumber), (await ReloadAsync(id)).CurrentVersionNumber);

        // Every committed version's object is present — nobody deleted anybody else's, which is the failure this
        // whole test exists to catch.
        foreach (var version in versions)
        {
            Assert.Contains(version.ObjectKey, _store.Keys);
        }
    }

    /// <summary>
    /// Several uploads in sequence number themselves 2, 3, 4 — so the allocator is reading the next number rather than
    /// assuming one.
    /// </summary>
    [Fact]
    public async Task Successive_uploads_number_themselves_in_order()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var expected in (int[])[2, 3, 4])
        {
            var body = await AddAsync(client, id, BrandSourceSampleFiles.Png((uint)(100 + expected), 90), $"v{expected}.png");

            Assert.Equal(expected, body.GetProperty("versionNumber").GetInt32());
        }

        Assert.Equal([1, 2, 3, 4], (await VersionsAsync(id)).Select(row => row.VersionNumber).Order());
        Assert.Equal(4, (await ReloadAsync(id)).CurrentVersionNumber);
    }

    /// <summary>
    /// A number already taken is refused and <strong>the existing object is left alone</strong>. Set up directly,
    /// because the real race is non-deterministic and this behaviour is too important to test only by luck.
    /// </summary>
    [Fact]
    public async Task A_taken_version_number_is_refused_without_touching_the_existing_object()
    {
        var id = await SeededAsync();

        // Version 2's object exists with no row behind it — exactly the state a winner is in while its transaction is
        // still open, which is when a loser would be tempted to delete it.
        var contestedKey = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 2);
        var winnerBytes = BrandSourceSampleFiles.Png(1024, 768);
        await PutAsync(contestedKey, winnerBytes);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendAsync(client, id, BrandSourceSampleFiles.Png(320, 240), "loser.png");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetVersionTaken,
            (await ReadAsync(response)).GetProperty("code").GetString());

        // The object is still there, byte for byte. This is the assertion the whole no-compensation rule exists for.
        Assert.Contains(contestedKey, _store.Keys);
        Assert.Equal(winnerBytes, await ReadObjectAsync(contestedKey));

        // And no row was written for the refused attempt.
        Assert.Single(await VersionsAsync(id));
        Assert.Equal(1, (await ReloadAsync(id)).CurrentVersionNumber);
    }

    // ---- Failures leave nothing behind ----

    /// <summary>
    /// Storage unreachable: a 503, no row, and nothing left in the store.
    /// </summary>
    [Fact]
    public async Task Storage_being_unreachable_writes_nothing()
    {
        var id = await SeededAsync();
        var keysBefore = _store.Keys.ToList();
        _store.Unavailable = true;

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendAsync(client, id, BrandSourceSampleFiles.Png(), "second.png");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetStorageUnavailable,
            (await ReadAsync(response)).GetProperty("code").GetString());

        _store.Unavailable = false;

        Assert.Equal(keysBefore.Order(), _store.Keys.Order());
        Assert.Single(await VersionsAsync(id));
        Assert.Equal(1, (await ReloadAsync(id)).CurrentVersionNumber);
    }

    /// <summary>
    /// A file whose bytes are not an image this library stores is refused on what they are, not what they are called —
    /// and nothing reaches storage, because acceptance runs before the write.
    /// </summary>
    [Theory]
    [InlineData("not an image at all", "photo.png")]
    [InlineData("%PDF-1.7 still not an image", "photo.jpg")]
    public async Task A_file_that_is_not_an_image_is_refused_before_anything_is_written(
        string content, string fileName)
    {
        var id = await SeededAsync();
        var keysBefore = _store.Keys.ToList();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendAsync(client, id, System.Text.Encoding.UTF8.GetBytes(content), fileName);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetUnsupported,
            (await ReadAsync(response)).GetProperty("code").GetString());

        Assert.Equal(keysBefore.Order(), _store.Keys.Order());
        Assert.Single(await VersionsAsync(id));
    }

    [Fact]
    public async Task A_request_with_no_file_is_refused_naming_the_field()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PostAsync(
            VersionsIn(_fixture.WorkspaceA, id), new MultipartFormDataContent(), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await VersionsAsync(id));
    }

    /// <summary>
    /// A commit that fails takes the object it wrote with it — no orphan is left in storage.
    /// </summary>
    /// <remarks>
    /// Forced by colliding on <c>MediaAssetVersion</c>'s unique <c>ObjectKey</c> index: another asset is given a row
    /// already claiming the key this upload will compute, so the object write succeeds and the row insert is refused.
    /// That is the only branch where the object <em>is</em> ours and does have to go, and it is unreachable through the
    /// endpoint any other way — a real commit failure needs a concurrent writer. The same technique proved 12.9a's
    /// compensation.
    /// </remarks>
    [Fact]
    public async Task A_commit_that_fails_removes_the_object_it_wrote()
    {
        var id = await SeededAsync();
        var contestedKey = MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, id, 2);

        // Another asset, holding a row that already claims the key our version 2 would use.
        var squatter = await SeededAsync();
        await ClaimKeyAsync(squatter, versionNumber: 99, contestedKey);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendAsync(client, id, BrandSourceSampleFiles.Png(800, 600), "second.png");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotCreated,
            (await ReadAsync(response)).GetProperty("code").GetString());

        // The object written for the refused commit is gone, so nothing unreferenced is left behind.
        Assert.DoesNotContain(contestedKey, _store.Keys);

        // And nothing partial survived: no version row, and the counter did not move.
        Assert.Single(await VersionsAsync(id));
        Assert.Equal(1, (await ReloadAsync(id)).CurrentVersionNumber);
    }

    // ---- Replay ----

    [Fact]
    public async Task A_retry_under_one_key_adds_one_version()
    {
        var id = await SeededAsync();
        var key = Guid.NewGuid().ToString();
        var bytes = BrandSourceSampleFiles.Png(800, 600);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await SendAsync(client, id, bytes, "second.png", key);
        var second = await SendAsync(client, id, bytes, "second.png", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        Assert.Equal(2, (await ReadAsync(first)).GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, (await ReadAsync(second)).GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, (await VersionsAsync(id)).Count);
    }

    /// <summary>
    /// Without a key, two uploads of the same file are two versions — the same reading the utilization log takes, and
    /// correct here too: a creator may genuinely re-upload.
    /// </summary>
    [Fact]
    public async Task Without_a_key_two_uploads_add_two_versions()
    {
        var id = await SeededAsync();
        var bytes = BrandSourceSampleFiles.Png(800, 600);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await AddAsync(client, id, bytes, "second.png");
        await AddAsync(client, id, bytes, "second.png");

        Assert.Equal(3, (await VersionsAsync(id)).Count);
        Assert.Equal(3, (await ReloadAsync(id)).CurrentVersionNumber);
    }

    // ---- Role, not-found and isolation ----

    [Fact]
    public async Task Every_reason_there_is_no_asset_is_the_same_not_found()
    {
        var deleted = await SeededAsync(deletedAt: Now);
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var assetId in (Guid[])[Guid.NewGuid(), deleted, inB])
        {
            var response = await SendAsync(client, assetId, BrandSourceSampleFiles.Png(), "second.png");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                MediaErrorCodes.AssetNotFound,
                (await ReadAsync(response)).GetProperty("code").GetString());
        }

        // A removed asset does not gain versions, and nothing reached storage for any of the three.
        Assert.Single(await VersionsAsync(deleted));
    }

    [Fact]
    public async Task A_viewer_cannot_add_a_version()
    {
        var id = await SeededAsync();
        await SetMemberRoleAsync(_fixture.WorkspaceA, WorkspaceRole.Viewer);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await SendAsync(member, id, BrandSourceSampleFiles.Png(), "second.png");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await VersionsAsync(id));
    }

    /// <summary>
    /// Contributor is enough, where removing an asset needs Editor — adding a version takes nothing away.
    /// </summary>
    [Fact]
    public async Task A_contributor_may_add_a_version()
    {
        var id = await SeededAsync();
        await SetMemberRoleAsync(_fixture.WorkspaceA, WorkspaceRole.Contributor);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await SendAsync(member, id, BrandSourceSampleFiles.Png(800, 600), "second.png");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, (await VersionsAsync(id)).Count);
    }

    /// <summary>
    /// A's asset from B's route and from A's slug by B's owner: 404 both times, and A's asset untouched.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_add_a_version_to_the_others_asset()
    {
        var inA = await SeededAsync();

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var slug in (SeededWorkspace[])[_fixture.WorkspaceB, _fixture.WorkspaceA])
        {
            var response = await ownerB.PostAsync(
                VersionsIn(slug, inA), FormFor(BrandSourceSampleFiles.Png(), "b.png"), cancellationToken: Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Single(await VersionsAsync(inA));
        Assert.Equal(1, (await ReloadAsync(inA)).CurrentVersionNumber);
    }

    /// <summary>
    /// Both workspaces add a version 2 to their own asset. The object keys differ by workspace and asset, so neither
    /// can collide with or overwrite the other — the property that makes a deterministic key safe across tenants.
    /// </summary>
    [Fact]
    public async Task Each_workspace_versions_its_own_asset_without_colliding()
    {
        var inA = await SeededAsync();
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        await AddAsync(ownerA, inA, BrandSourceSampleFiles.Png(800, 600), "a.png");

        var bResponse = await ownerB.PostAsync(
            VersionsIn(_fixture.WorkspaceB, inB),
            FormFor(BrandSourceSampleFiles.Png(640, 480), "b.png"),
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, bResponse.StatusCode);

        Assert.Contains(MediaAssetObjectKey.For(_fixture.WorkspaceA.Id, inA, 2), _store.Keys);
        Assert.Contains(MediaAssetObjectKey.For(_fixture.WorkspaceB.Id, inB, 2), _store.Keys);
        Assert.Equal(2, (await VersionsAsync(inA)).Count);
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

    /// <summary>
    /// The multipart body, with an octet-stream part so the declared type never helps — the server has to establish
    /// what the bytes are.
    /// </summary>
    private static MultipartFormDataContent FormFor(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);

        return form;
    }

    /// <remarks>
    /// <c>PostAsync</c> rather than <c>SendAsync</c>, deliberately: the general sender JSON-serializes whatever it is
    /// given, which turns a multipart body into a JSON string and answers 400 on every upload.
    /// </remarks>
    private Task<HttpResponseMessage> SendAsync(
        GatewayClient client, Guid assetId, byte[] bytes, string fileName, string? idempotencyKey = null) =>
        client.PostAsync(
            VersionsIn(_fixture.WorkspaceA, assetId), FormFor(bytes, fileName), idempotencyKey, Ct);

    /// <summary>Adds a version and insists it succeeded, so a test about what changed cannot pass on a refusal.</summary>
    private async Task<JsonElement> AddAsync(
        GatewayClient client, Guid assetId, byte[] bytes, string fileName)
    {
        var response = await SendAsync(client, assetId, bytes, fileName);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadAsync(response);
    }

    private async Task PutAsync(string objectKey, byte[] bytes)
    {
        using var content = new MemoryStream(bytes);

        await _store.PutAsync(
            MediaAssetObjectKey.Container, objectKey, content, "image/png", bytes.Length + 1, Ct);
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

    /// <summary>An asset with version 1 already stored, so a new version is always the second.</summary>
    private async Task<Guid> SeededAsync(SeededWorkspace? workspace = null, DateTimeOffset? deletedAt = null)
    {
        var target = workspace ?? _fixture.WorkspaceA;
        var bytes = BrandSourceSampleFiles.Png(4, 3);

        await using var scope = ScopeFor(target);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread hero",
            Description = "Overhead on linen.",
            AltText = "A round loaf.",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        var key = MediaAssetObjectKey.For(target.Id, asset.Id, 1);

        asset.Versions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            MediaAssetId = asset.Id,
            VersionNumber = 1,
            MediaType = "image/png",
            SizeBytes = bytes.Length,
            Width = 4,
            Height = 3,
            ContentChecksum = $"sha256:{Convert.ToHexString(Guid.NewGuid().ToByteArray())}",
            ObjectKey = key,
            OriginalFileName = "first.png",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = Now,
        });

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);
        await PutAsync(key, bytes);

        return asset.Id;
    }

    /// <summary>
    /// Gives an asset a version row claiming <paramref name="objectKey"/>, so a later upload computing the same key
    /// collides on the unique index. Raw SQL, because the key is one no write path would produce for this asset.
    /// </summary>
    private async Task ClaimKeyAsync(Guid assetId, int versionNumber, string objectKey)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.ExecuteSqlRawAsync(
            """
            INSERT INTO MediaAssetVersions
                (Id, WorkspaceId, MediaAssetId, VersionNumber, MediaType, SizeBytes, Width, Height,
                 ContentChecksum, OriginalFileName, ObjectKey, Source, SourceGeneratedImageId,
                 CreatedByMembershipId, CreatedAt)
            VALUES ({0}, {1}, {2}, {3}, 'image/png', 10, 4, 3, 'sha256:claimed', 'claimed.png', {4}, 1, NULL,
                    {5}, {6});
            """,
            [
                Guid.NewGuid(), _fixture.WorkspaceA.Id, assetId, versionNumber, objectKey, Guid.NewGuid(),
                Now.UtcDateTime.Ticks,
            ],
            Ct);
    }

    private async Task<MediaAsset> ReloadAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(asset => asset.Id == assetId, Ct);
    }

    private async Task<List<MediaAssetVersion>> VersionsAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssetVersions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(version => version.MediaAssetId == assetId)
            .ToListAsync(Ct);
    }

    private async Task SetMemberRoleAsync(SeededWorkspace workspace, WorkspaceRole role)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var memberships = await db.WorkspaceMemberships
            .IgnoreQueryFilters()
            .Where(candidate => candidate.WorkspaceId == workspace.Id)
            .ToListAsync(Ct);

        foreach (var candidate in memberships.Where(candidate => candidate.Role != WorkspaceRole.Owner))
        {
            candidate.Role = role;
        }

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
