using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

#pragma warning disable MEAI001

/// <summary>
/// The rendition job end to end (B-28, AF.5.5): a request in the outbox, delivered by the real dispatcher
/// to the real handler, against real PNG bytes — so what is asserted is what a Worker would do.
/// </summary>
/// <remarks>
/// Only the store and the clock are stand-ins. The codecs are the production ones, which is the point:
/// "a rendition was stored" here means a JPEG a strict reader accepts, smaller than its source.
/// </remarks>
public sealed class MediaRenditionJobTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>A detailed 800 × 600 picture: larger than the thumbnail box, smaller than the web one.</summary>
    private static readonly byte[] Photograph = Picture(800, 600, opaque: true);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly ServiceProvider _provider;

    public MediaRenditionJobTests()
    {
        _connection.Open();
        _store.Now = _clock.UtcNow;

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddApplicationTime()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddIdempotency(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build())
            .AddMediaModule()
            .AddGeneratedImageWorker()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

        services.RemoveAll<IImageGenerator>();
        services.AddSingleton<IImageGenerator>(new FakeImageGenerator());
        services.RemoveAll<IPrivateObjectStore>();
        services.AddSingleton<IPrivateObjectStore>(_store);
        services.RemoveAll<IMalwareScanGateway>();
        services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());

        _provider = services.BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = _clock.UtcNow },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = _clock.UtcNow });
        db.SaveChanges();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // ---- success ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_staged_image_gets_a_web_rendition_and_a_thumbnail_both_smaller_than_it()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceA, image.Source);

        var summary = await DispatchAsync();

        Assert.Equal(1, summary.Completed);
        await AssertRenderedAsync(image, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
    }

    [Fact]
    public async Task A_library_version_gets_the_same_two_renditions()
    {
        var version = await AddVersionAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceA, version.Source);

        await DispatchAsync();

        await AssertRenderedAsync(version, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
    }

    [Fact]
    public async Task The_same_request_delivered_twice_makes_one_of_each()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceA, image.Source);
        await RequestAsync(WorkspaceA, image.Source);

        var summary = await DispatchAsync();

        Assert.Equal(2, summary.Completed);
        Assert.Equal(2, (await RenditionsAsync(WorkspaceA)).Count);

        // The original and one object per purpose, and nothing a second delivery left behind.
        Assert.Equal(3, _store.Keys.Count);
    }

    // ---- sources that will never have one --------------------------------------------------------------

    public static TheoryData<string, byte[], MediaRenditionReason> UnreadableSources() => new()
    {
        { "a JPEG", BrandSourceSampleFiles.Jpeg(), MediaRenditionReason.UnreadableFormat },
        { "a PNG cut short", Photograph[..(Photograph.Length / 2)], MediaRenditionReason.Corrupt },
        { "an interlaced PNG", new PngSampleFile { Interlace = 1, Samples = [1, 2, 3, 255] }.Build(), MediaRenditionReason.UnsupportedVariant },
        { "a PNG past the pixel cap", new PngSampleFile { Width = 6000, Height = 4001, OmitData = true }.Build(), MediaRenditionReason.TooLarge },
        { "a picture with transparency", Picture(64, 48, opaque: false), MediaRenditionReason.HasTransparency },
        { "a picture already smaller than its rendition would be", new PngSampleFile { Width = 8, Height = 8, Samples = new byte[8 * 8 * 4].Select(_ => (byte)255).ToArray() }.Build(), MediaRenditionReason.NotSmaller },
    };

    [Theory]
    [MemberData(nameof(UnreadableSources))]
    public async Task A_source_that_cannot_be_compressed_is_recorded_as_such_and_never_attempted_again(
        string kind, byte[] bytes, MediaRenditionReason reason)
    {
        var image = await StageAsync(WorkspaceA, bytes);
        await RequestAsync(WorkspaceA, image.Source);

        Assert.Equal(1, (await DispatchAsync()).Completed);

        var rows = await RenditionsAsync(WorkspaceA);
        Assert.True(rows.Count == 2, kind);
        Assert.All(rows, row =>
        {
            Assert.Equal(MediaRenditionStatus.NotCompressed, row.Status);
            Assert.Equal(reason, row.NotCompressedReason);
            Assert.Null(row.ObjectKey);
        });

        // Nothing was stored for it, and the picture itself is exactly where it was.
        Assert.Equal(image.ObjectKey, Assert.Single(_store.Keys));

        // Asked again, with storage switched off: the rows hold the slots, so the source is not even read
        // a second time. Had it been, this delivery would have failed and been left to retry.
        await RequestAsync(WorkspaceA, image.Source);
        _store.Unavailable = true;
        Assert.Equal(1, (await DispatchAsync()).Completed);
        Assert.Equal(2, (await RenditionsAsync(WorkspaceA)).Count);
    }

    [Fact]
    public async Task A_picture_that_is_no_longer_waiting_gets_nothing()
    {
        var declined = await StageAsync(WorkspaceA, Photograph, GeneratedImageStatus.Rejected);
        var deleted = await AddVersionAsync(WorkspaceA, Photograph, deletedAt: _clock.UtcNow);
        await RequestAsync(WorkspaceA, declined.Source);
        await RequestAsync(WorkspaceA, deleted.Source);
        await RequestAsync(WorkspaceA, MediaRenditionSource.ForGeneratedImage(Guid.NewGuid()));

        // Done, not failed: the requests were true when written and the pictures have moved on.
        Assert.Equal(3, (await DispatchAsync()).Completed);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Equal(2, _store.Keys.Count);
    }

    // ---- retries ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_crash_between_storing_and_recording_is_finished_by_the_retry_with_one_object_and_one_row()
    {
        var image = await StageAsync(WorkspaceA, Photograph);

        // What an attempt that died after its write leaves: an object under the rendition's key, and no row.
        var key = MediaRenditionObjectKey.For(image.ObjectKey, MediaRenditionPurpose.Web);
        await _store.PutAsync(
            GeneratedImageObjectKey.Container, key, new MemoryStream([9, 9, 9]), "image/jpeg", 16, Token);

        await RequestAsync(WorkspaceA, image.Source);
        await DispatchAsync();

        var web = Assert.Single(await RenditionsAsync(WorkspaceA), row => row.Purpose == MediaRenditionPurpose.Web);
        var stored = await ReadAsync(key);

        // One object under the key, it is the rendition, and the row describes exactly it.
        Assert.Equal(MediaRenditionStatus.Ready, web.Status);
        Assert.Equal(key, web.ObjectKey);
        Assert.Equal(stored.Length, web.SizeBytes);
        Assert.Equal("image/jpeg", GeneratedImageInspector.Inspect(stored).MediaType);
        Assert.Equal(3, _store.Keys.Count);
    }

    [Fact]
    public async Task Storage_that_cannot_be_reached_is_retried_and_succeeds_when_it_returns()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceA, image.Source);
        _store.Unavailable = true;

        var failed = await DispatchAsync();

        Assert.Equal(1, failed.Retrying);
        Assert.Empty(await RenditionsAsync(WorkspaceA));

        // What the outbox keeps of the failure is this application's own sentence and the kind of failure:
        // not the store's message, which for a real provider can name a host or a key.
        Assert.Equal(
            "The rendition request could not be completed (ObjectStoreUnavailableException).", await LastErrorAsync());

        _store.Unavailable = false;
        Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(1, (await DispatchAsync()).Completed);
        await AssertRenderedAsync(image, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
    }

    [Fact]
    public async Task A_source_whose_bytes_never_turn_up_is_given_up_on_and_recorded()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await _store.DeleteAsync(GeneratedImageObjectKey.Container, image.ObjectKey, Token);
        await RequestAsync(WorkspaceA, image.Source);

        var summaries = await DispatchUntilSettledAsync();

        // Retried as often as the outbox allows, then settled rather than poisoned: the last delivery
        // records the outcome instead of throwing.
        Assert.Equal(OutboxPolicy.MaxAttempts, summaries.Count);
        Assert.Equal(1, summaries[^1].Completed);
        Assert.All(summaries, summary => Assert.Equal(0, summary.Poisoned));

        var rows = await RenditionsAsync(WorkspaceA);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(MediaRenditionReason.RetriesExhausted, row.NotCompressedReason));

        // And it is over: a row holds each slot, so the backfill does not find the picture again.
        Advance(MediaPolicy.RenditionBackfillMinimumAge + TimeSpan.FromMinutes(1));
        Assert.Equal(0, await BackfillAsync());
    }

    [Fact]
    public async Task An_outage_that_outlasts_the_retries_records_nothing_and_the_backfill_tries_again_later()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceA, image.Source);
        _store.Unavailable = true;

        var summaries = await DispatchUntilSettledAsync();

        // Giving up needs storage too, to be sure nothing was left behind, so with storage down the request
        // fails honestly instead of recording an outcome it could not check.
        Assert.Equal(1, summaries[^1].Poisoned);
        Assert.Empty(await RenditionsAsync(WorkspaceA));

        _store.Unavailable = false;
        Advance(MediaPolicy.RenditionBackfillMinimumAge + TimeSpan.FromMinutes(1));

        Assert.Equal(1, await BackfillAsync());
        Assert.Equal(1, (await DispatchAsync()).Completed);
        await AssertRenderedAsync(image, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
    }

    // ---- backfill --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_backfill_works_through_old_pictures_a_batch_at_a_time_and_stops_when_they_are_done()
    {
        var tiny = new PngSampleFile { Width = 2, Height = 2, Samples = new byte[16].Select(_ => (byte)255).ToArray() }.Build();
        var total = MediaPolicy.RenditionBackfillBatchSize + 7;

        for (var i = 0; i < total; i++)
        {
            await StageAsync(WorkspaceA, tiny);
        }

        Advance(MediaPolicy.RenditionBackfillMinimumAge + TimeSpan.FromMinutes(1));

        // Stored a moment ago: its own request is still in flight, so it is not the backfill's yet.
        var recent = await StageAsync(WorkspaceA, tiny);

        Assert.Equal(MediaPolicy.RenditionBackfillBatchSize, await BackfillAsync());
        await DrainAsync();

        // The second pass starts where the first one's work ended, with no cursor kept anywhere.
        Assert.Equal(7, await BackfillAsync());
        await DrainAsync();

        Assert.Equal(0, await BackfillAsync());

        var rows = await RenditionsAsync(WorkspaceA);
        Assert.Equal(total * 2, rows.Count);
        Assert.DoesNotContain(rows, row => row.GeneratedImageId == recent.Source.GeneratedImageId);
    }

    [Fact]
    public async Task The_backfill_finds_library_versions_too_and_only_pictures_that_still_want_renditions()
    {
        var version = await AddVersionAsync(WorkspaceA, Photograph);
        var waiting = await StageAsync(WorkspaceB, Photograph);
        await AddVersionAsync(WorkspaceA, Photograph, deletedAt: _clock.UtcNow);
        await StageAsync(WorkspaceA, Photograph, GeneratedImageStatus.Rejected);

        Advance(MediaPolicy.RenditionBackfillMinimumAge + TimeSpan.FromMinutes(1));

        Assert.Equal(2, await BackfillAsync());
        await DrainAsync();

        await AssertRenderedAsync(version, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
        await AssertRenderedAsync(waiting, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
        Assert.Equal(0, await BackfillAsync());
    }

    // ---- two workspaces --------------------------------------------------------------------------------

    [Fact]
    public async Task Two_workspaces_are_each_processed_under_their_own_context()
    {
        var mine = await StageAsync(WorkspaceA, Photograph);
        var theirs = await StageAsync(WorkspaceB, Photograph);
        await RequestAsync(WorkspaceA, mine.Source);
        await RequestAsync(WorkspaceB, theirs.Source);

        Assert.Equal(2, (await DispatchAsync()).Completed);

        await AssertRenderedAsync(mine, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
        await AssertRenderedAsync(theirs, expectedWeb: (800, 600), expectedThumbnail: (480, 360));

        // Each workspace's objects are under its own prefix and nowhere else.
        Assert.All(await RenditionsAsync(WorkspaceA), row => Assert.StartsWith($"workspaces/{WorkspaceA:N}/", row.ObjectKey));
        Assert.All(await RenditionsAsync(WorkspaceB), row => Assert.StartsWith($"workspaces/{WorkspaceB:N}/", row.ObjectKey));
    }

    [Fact]
    public async Task A_request_naming_one_workspace_and_anothers_picture_makes_nothing()
    {
        var theirs = await StageAsync(WorkspaceB, Photograph);
        var theirVersion = await AddVersionAsync(WorkspaceB, Photograph);

        // B's ids under A's name: what a tampered or mistaken payload would be.
        await RequestAsync(WorkspaceA, theirs.Source);
        await RequestAsync(WorkspaceA, theirVersion.Source);

        // Storage is switched off for the delivery, so completing at all proves nothing of B's was read:
        // a read would have thrown and left the request to retry.
        _store.Unavailable = true;
        Assert.Equal(2, (await DispatchAsync()).Completed);
        _store.Unavailable = false;

        // Not rendered, and nothing written into either workspace.
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Empty(await RenditionsAsync(WorkspaceB));
        Assert.Equal(2, _store.Keys.Count);
    }

    [Fact]
    public async Task A_request_for_a_workspace_that_is_gone_is_done_rather_than_retried()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(Guid.NewGuid(), image.Source);

        Assert.Equal(1, (await DispatchAsync()).Completed);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_request_naming_the_other_workspace_fails_the_same_way_round()
    {
        var mine = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceB, mine.Source);

        _store.Unavailable = true;
        Assert.Equal(1, (await DispatchAsync()).Completed);
        _store.Unavailable = false;

        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Empty(await RenditionsAsync(WorkspaceB));
    }

    [Fact]
    public async Task Only_the_platform_makes_renditions_and_a_members_context_is_answered_with_nothing()
    {
        var image = await StageAsync(WorkspaceA, Photograph);

        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            WorkspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        // The owner of the workspace, with the picture's real id. Still not who this is for.
        var summary = await scope.ServiceProvider.GetRequiredService<IMediaRenditionFacade>()
            .ProduceAsync(image.Source, finalAttempt: false, Token);

        Assert.Equal(MediaRenditionProduceSummary.Nothing, summary);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Equal(image.ObjectKey, Assert.Single(_store.Keys));
    }

    [Fact]
    public async Task The_backfills_claim_returns_each_pictures_own_workspace_and_ids_and_nothing_else()
    {
        var mine = await StageAsync(WorkspaceA, Photograph);
        var theirs = await AddVersionAsync(WorkspaceB, Photograph);

        await using var scope = _provider.CreateAsyncScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<MediaRenditionClaimRepository>()
            .FindUnrenderedAsync(_clock.UtcNow, 10, Token);

        // Read with no workspace resolved, across both — which is why what comes back is only this.
        Assert.Equal(
            [
                MediaRenditionRequestedEvent.For(WorkspaceA, mine.Source),
                MediaRenditionRequestedEvent.For(WorkspaceB, theirs.Source),
            ],
            claimed);
    }

    // ---- two deliveries at once ------------------------------------------------------------------------

    [Fact]
    public async Task A_retry_that_finds_its_own_bytes_already_stored_adopts_them_rather_than_storing_again()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        await RequestAsync(WorkspaceA, image.Source);
        await DispatchAsync();

        var key = MediaRenditionObjectKey.For(image.ObjectKey, MediaRenditionPurpose.Web);
        var before = await ReadAsync(key);
        var storedAt = await StoredAtAsync(image.WorkspaceId, key);

        // The state a crash between the write and the row leaves, with the real bytes: objects, no rows.
        await ForgetRowsAsync(WorkspaceA);
        Advance(TimeSpan.FromMinutes(5));

        await RequestAsync(WorkspaceA, image.Source);
        Assert.Equal(1, (await DispatchAsync()).Completed);

        // The same object, not a new one written over it: an object another delivery might be about to
        // record is never deleted to make room.
        Assert.Equal(storedAt, await StoredAtAsync(image.WorkspaceId, key));
        Assert.Equal(before, await ReadAsync(key));
        await AssertRenderedAsync(image, expectedWeb: (800, 600), expectedThumbnail: (480, 360));
        Assert.Equal(3, _store.Keys.Count);
    }

    [Fact]
    public async Task A_rendition_stored_after_another_delivery_gave_up_is_removed_rather_than_left_unnamed()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };

        await using var scope = _provider.CreateAsyncScope();
        var renditions = await RenditionDataAsync(scope, WorkspaceA);
        var target = (await renditions.FindTargetAsync(image.Source, Token))!;

        // One delivery gives up on the web rendition; the other, which read the same target, then finishes.
        await renditions.RecordGaveUpAsync(target, MediaRenditionPurpose.Web, Token);
        await renditions.StoreAsync(target, MediaRenditionPurpose.Web, jpeg, 4, 3, Token);

        // The slot is the first row's, and nothing is stored that no row names.
        var row = Assert.Single(await RenditionsAsync(WorkspaceA));
        Assert.Equal(MediaRenditionReason.RetriesExhausted, row.NotCompressedReason);
        Assert.Equal(image.ObjectKey, Assert.Single(_store.Keys));
    }

    [Fact]
    public async Task Giving_up_removes_an_unrecorded_object_and_never_one_a_row_names()
    {
        var image = await StageAsync(WorkspaceA, Photograph);
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var web = MediaRenditionObjectKey.For(image.ObjectKey, MediaRenditionPurpose.Web);
        var thumbnail = MediaRenditionObjectKey.For(image.ObjectKey, MediaRenditionPurpose.Thumbnail);

        await using var scope = _provider.CreateAsyncScope();
        var renditions = await RenditionDataAsync(scope, WorkspaceA);
        var target = (await renditions.FindTargetAsync(image.Source, Token))!;

        // Web: recorded by another delivery. Thumbnail: stored by an attempt that never wrote its row.
        await renditions.StoreAsync(target, MediaRenditionPurpose.Web, jpeg, 4, 3, Token);
        await _store.PutAsync(GeneratedImageObjectKey.Container, thumbnail, new MemoryStream(jpeg), "image/jpeg", 16, Token);

        await renditions.RecordGaveUpAsync(target, MediaRenditionPurpose.Web, Token);
        await renditions.RecordGaveUpAsync(target, MediaRenditionPurpose.Thumbnail, Token);

        var rows = await RenditionsAsync(WorkspaceA);
        Assert.Equal(MediaRenditionStatus.Ready, Assert.Single(rows, row => row.Purpose == MediaRenditionPurpose.Web).Status);
        Assert.Equal(
            MediaRenditionReason.RetriesExhausted,
            Assert.Single(rows, row => row.Purpose == MediaRenditionPurpose.Thumbnail).NotCompressedReason);
        Assert.Contains(web, _store.Keys);
        Assert.DoesNotContain(thumbnail, _store.Keys);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    /// <summary>The rendition data layer, in a scope resolved for the platform the way the handler's is.</summary>
    private static async Task<IMediaRenditionDataLayer> RenditionDataAsync(AsyncServiceScope scope, Guid workspaceId)
    {
        Assert.True((await scope.ServiceProvider
            .GetRequiredService<Domain.Modules.Tenancy.Facade.IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(workspaceId, Token)).Succeeded);

        return scope.ServiceProvider.GetRequiredService<IMediaRenditionDataLayer>();
    }

    private async Task ForgetRowsAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MediaRenditions.RemoveRange(await db.MediaRenditions
            .IgnoreQueryFilters()
            .Where(rendition => rendition.WorkspaceId == workspaceId)
            .ToListAsync(Token));
        await db.SaveChangesAsync(Token);
    }

    /// <summary>When the store says an object was written, which a replaced object would not keep.</summary>
    private async Task<DateTimeOffset> StoredAtAsync(Guid workspaceId, string key)
    {
        var page = await _store.ListAsync(
            GeneratedImageObjectKey.Container, GeneratedImageObjectKey.PrefixFor(workspaceId), 100, null, Token);

        return Assert.Single(page.Objects, stored => stored.Key == key).CreatedAt;
    }

    private async Task<string?> LastErrorAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return (await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .OutboxMessages
            .AsNoTracking()
            .SingleAsync(Token)).LastError;
    }

    private sealed record StoredPicture(Guid WorkspaceId, MediaRenditionSource Source, string ObjectKey, string Checksum, long SizeBytes);

    private void Advance(TimeSpan by)
    {
        _clock.Advance(by);
        _store.Now = _clock.UtcNow;
    }

    /// <summary>One pass of the real outbox dispatcher, which is what delivers a request to the handler.</summary>
    private async Task<OutboxDispatchSummary> DispatchAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Token);
    }

    /// <summary>Dispatches until a pass claims nothing: more than one outbox batch may be waiting.</summary>
    private async Task DrainAsync()
    {
        while ((await DispatchAsync()).Claimed > 0)
        {
        }
    }

    /// <summary>Dispatches past each backoff until the one waiting request completes or is poisoned.</summary>
    private async Task<IReadOnlyList<OutboxDispatchSummary>> DispatchUntilSettledAsync()
    {
        var summaries = new List<OutboxDispatchSummary>();

        do
        {
            summaries.Add(await DispatchAsync());
            Advance(TimeSpan.FromMinutes(31));
        }
        while (summaries[^1].Retrying > 0 && summaries.Count < OutboxPolicy.MaxAttempts * 2);

        return summaries;
    }

    private async Task<int> BackfillAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IMediaRenditionBackfillWorker>().RunAsync(Token);
    }

    /// <summary>The request a stored picture is given, written the way the storing code writes it.</summary>
    private async Task RequestAsync(Guid workspaceId, MediaRenditionSource source)
    {
        await using var scope = _provider.CreateAsyncScope();
        var request = MediaRenditionRequestedEvent.For(workspaceId, source);

        scope.ServiceProvider.GetRequiredService<IOutboxWriter>()
            .Enqueue(MediaRenditionRequestedEvent.MessageType, request.Serialize(), request.CorrelationId);
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Token);
    }

    private async Task AssertRenderedAsync(
        StoredPicture picture, (int Width, int Height) expectedWeb, (int Width, int Height) expectedThumbnail)
    {
        var rows = (await RenditionsAsync(picture.WorkspaceId))
            .Where(row => row.GeneratedImageId == picture.Source.GeneratedImageId && row.MediaAssetId == picture.Source.MediaAssetId)
            .ToList();

        Assert.Equal(2, rows.Count);

        foreach (var (purpose, expected) in new[]
        {
            (MediaRenditionPurpose.Web, expectedWeb),
            (MediaRenditionPurpose.Thumbnail, expectedThumbnail),
        })
        {
            var row = Assert.Single(rows, candidate => candidate.Purpose == purpose);
            var key = MediaRenditionObjectKey.For(picture.ObjectKey, purpose);
            var stored = await ReadAsync(key);
            var inspection = GeneratedImageInspector.Inspect(stored);

            // A JPEG of the fitted size, beside its source, smaller than it, and described truthfully.
            Assert.Equal(MediaRenditionStatus.Ready, row.Status);
            Assert.Equal(key, row.ObjectKey);
            Assert.Equal("image/jpeg", row.MediaType);
            Assert.Equal(GeneratedImageInspector.JpegMediaType, inspection.MediaType);
            Assert.Equal(expected, (inspection.Width, inspection.Height));
            Assert.Equal(expected, (row.Width!.Value, row.Height!.Value));
            Assert.Equal(stored.Length, row.SizeBytes);
            Assert.InRange(stored.Length, 1, picture.SizeBytes - 1);
            Assert.Equal(picture.Checksum, row.SourceContentChecksum);
            Assert.StartsWith("sha256:", row.ContentChecksum, StringComparison.Ordinal);
        }

        // Every read the job opened was handed back.
        Assert.Equal(0, _store.OpenReads);

        // The original is exactly the bytes that were stored.
        Assert.Equal(picture.SizeBytes, (await ReadAsync(picture.ObjectKey, MediaContainer(picture))).Length);
    }

    private static string MediaContainer(StoredPicture picture) =>
        picture.Source.GeneratedImageId is null ? MediaAssetObjectKey.Container : GeneratedImageObjectKey.Container;

    private async Task<byte[]> ReadAsync(string key, string? container = null)
    {
        container ??= MediaRenditionObjectKey.TryParse(key, out var parts) ? parts.Container : GeneratedImageObjectKey.Container;

        await using var content = await _store.OpenReadAsync(container, key, Token);
        Assert.NotNull(content);

        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, Token);

        return buffer.ToArray();
    }

    private async Task<StoredPicture> StageAsync(
        Guid workspaceId, byte[] bytes, GeneratedImageStatus status = GeneratedImageStatus.Staged)
    {
        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var key = GeneratedImageObjectKey.For(workspaceId, operationId, 0);

        var write = await _store.PutAsync(
            GeneratedImageObjectKey.Container, key, new MemoryStream(bytes), "image/png", MediaPolicy.ImageMaxBytes, Token);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = _clock.UtcNow;

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = 1,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspaceId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = status,
            ObjectKey = key,
            MediaType = "image/png",
            Width = 4,
            Height = 3,
            SizeBytes = write.Object!.SizeBytes,
            ContentChecksum = write.Object.ContentChecksum,
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        });

        await db.SaveChangesAsync(Token);

        return new StoredPicture(
            workspaceId, MediaRenditionSource.ForGeneratedImage(imageId), key, write.Object.ContentChecksum, write.Object.SizeBytes);
    }

    private async Task<StoredPicture> AddVersionAsync(Guid workspaceId, byte[] bytes, DateTimeOffset? deletedAt = null)
    {
        var assetId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var key = MediaAssetObjectKey.For(workspaceId, assetId, 1);

        var write = await _store.PutAsync(
            MediaAssetObjectKey.Container, key, new MemoryStream(bytes), "image/png", MediaPolicy.ImageMaxBytes, Token);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = _clock.UtcNow;

        db.MediaAssets.Add(new MediaAsset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            Title = "Soda bread hero",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        });

        db.MediaAssetVersions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MediaAssetId = assetId,
            VersionNumber = 1,
            MediaType = "image/png",
            SizeBytes = write.Object!.SizeBytes,
            Width = 4,
            Height = 3,
            ContentChecksum = write.Object.ContentChecksum,
            ObjectKey = key,
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(Token);

        return new StoredPicture(
            workspaceId, MediaRenditionSource.ForAssetVersion(assetId, 1), key, write.Object.ContentChecksum, write.Object.SizeBytes);
    }

    /// <summary>Reads past the filters, because a test asserting isolation has to see both sides of it.</summary>
    private async Task<IReadOnlyList<MediaRendition>> RenditionsAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaRenditions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(rendition => rendition.WorkspaceId == workspaceId)
            .ToListAsync(Token);
    }

    /// <summary>
    /// A real PNG with detail in it, so its renditions are honestly smaller. With <paramref name="opaque"/>
    /// false one pixel is see-through, which is all it takes.
    /// </summary>
    private static byte[] Picture(int width, int height, bool opaque)
    {
        var samples = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var detail = (12 * Math.Sin((x + (3 * y)) / 2.0)) + (((x * 7) + (y * 13)) % 17) - 8;
                var at = ((y * width) + x) * 4;
                samples[at] = (byte)(128 + (70 * Math.Sin(x / 7.0)) + detail);
                samples[at + 1] = (byte)(128 + (70 * Math.Cos(y / 5.0)) + detail);
                samples[at + 2] = (byte)(128 + (70 * Math.Sin((x + y) / 9.0)) + detail);
                samples[at + 3] = byte.MaxValue;
            }
        }

        if (!opaque)
        {
            samples[3] = 128;
        }

        return new PngSampleFile { Width = (uint)width, Height = (uint)height, Samples = samples }.Build();
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
