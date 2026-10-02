using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand-source embedding job end to end: what an extraction queues, how a pass batches, retries and
/// resumes, how a changed model replaces what it embedded, and that none of it crosses a workspace.
/// </summary>
/// <remarks>
/// <para>
/// Over SQLite with a fake <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>: no network, no credential, no
/// model. As <see cref="BrandSourceChunkAggregateTests"/> says, nothing here is a claim about vector
/// <em>similarity</em> — the vectors are fixed so a write and a read compare exactly.
/// </para>
/// <para>
/// The clock is movable and nothing sleeps. As in <see cref="BrandSourceExtractionQueueTests"/>, SQLite never
/// moves <c>RowVersion</c>, so lease assertions are made against the application's own token check.
/// </para>
/// </remarks>
public sealed class BrandSourceEmbeddingWorkerTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly FakeEmbeddingGenerator _generator = new();
    private readonly ServiceProvider _provider;

    // Read each time a scope asks for the generator, so a test can swap the deployment between passes.
    private IEmbeddingGenerator<string, Embedding<float>> _current;

    public BrandSourceEmbeddingWorkerTests()
    {
        _current = _generator;
        _connection.Open();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddTenancy()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build())
            .AddApplicationTime()
            .AddBrandModule()
            .AddBrandSourceExtractionWorker()
            .AddBrandSourceEmbeddingWorker()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

        services.AddScoped<IEmbeddingGenerator<string, Embedding<float>>>(_ => _current);
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

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // ---- what an extraction queues ---------------------------------------------------------------------

    [Fact]
    public async Task An_extraction_that_produced_text_queues_one_embedding_for_its_exact_artifact()
    {
        var seeded = await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();

        var extraction = Assert.Single(await ExtractionsAsync());
        var operation = Assert.Single(await OperationsAsync());

        Assert.Equal(extraction.Id, operation.BrandSourceExtractionId);
        Assert.Equal(seeded.DocumentId, operation.BrandSourceDocumentId);
        Assert.Equal(BrandSourceEmbeddingOperationStatus.Queued, operation.Status);
        Assert.Null(operation.EmbeddingModel);
        Assert.Equal(0, operation.Attempts);

        // Nothing has been embedded by merely extracting: no per-request or per-commit provider call.
        Assert.Empty(_generator.Calls);
    }

    [Fact]
    public async Task An_extraction_with_no_text_queues_nothing_to_embed()
    {
        await UploadAsync(WorkspaceA, BrandSourceSampleFiles.Png(), "reference.png");
        await ExtractAsync();

        Assert.Empty(await OperationsAsync());
    }

    [Fact]
    public async Task A_correction_queues_an_embedding_for_the_new_artifact()
    {
        var seeded = await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();
        await EmbedAsync();

        var first = Assert.Single(await ExtractionsAsync());
        await CorrectAsync(seeded, first.Id, "A corrected passage about evergreen.");

        var operations = await OperationsAsync();

        Assert.Equal(2, operations.Count);
        Assert.Contains(operations, operation =>
            operation.Status == BrandSourceEmbeddingOperationStatus.Queued
            && operation.BrandSourceExtractionId != first.Id);
    }

    // ---- fake-vector: a pass builds a verifiable set ---------------------------------------------------

    [Fact]
    public async Task A_pass_chunks_the_artifact_and_makes_a_whole_verified_set_current()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 40));
        await ExtractAsync();

        var summary = await EmbedAsync();

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(1, summary.Embedded);

        var extraction = Assert.Single(await ExtractionsAsync());
        var set = Assert.Single(await SetsAsync());
        var chunks = await ChunksAsync(set.Id);

        Assert.Equal(BrandSourceChunkSetStatus.Current, set.Status);
        Assert.Equal(extraction.Id, set.BrandSourceExtractionId);
        Assert.Equal("model-a", set.EmbeddingModel);
        Assert.Equal(BrandSourceChunker.Id, set.ChunkerId);
        Assert.Equal(BrandPolicy.EmbeddingDimension, set.EmbeddingDimension);
        Assert.Equal(chunks.Count, set.ChunkCount);
        Assert.True(set.ChunkCount > 1);
        Assert.Equal(_clock.UtcNow, set.EmbeddedAt);

        // Every chunk is provably a slice of the immutable artifact, and carries its own vector.
        var artifact = await ArtifactBytesAsync(extraction);

        foreach (var chunk in chunks)
        {
            var slice = artifact.AsSpan((int)chunk.StartByteOffset, chunk.ByteLength);

            Assert.Equal(chunk.Text, Encoding.UTF8.GetString(slice));
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(slice)), chunk.ContentChecksum);
            Assert.Equal(FakeEmbeddingGenerator.VectorFor(chunk.Text), chunk.Embedding.Memory.ToArray());
        }

        Assert.Equal(Enumerable.Range(1, chunks.Count), chunks.Select(chunk => chunk.Ordinal));

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceEmbeddingOperationStatus.Completed, operation.Status);
        Assert.Equal(set.Id, operation.BrandSourceChunkSetId);
        Assert.Equal("model-a", operation.EmbeddingModel);
    }

    [Fact]
    public async Task Passages_are_embedded_in_bounded_batches_in_reading_order()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 190));
        await ExtractAsync();
        await EmbedAsync();

        var set = Assert.Single(await SetsAsync());

        Assert.True(set.ChunkCount > BrandPolicy.EmbeddingBatchSize * 2);

        Assert.All(_generator.Calls, call => Assert.InRange(call.Count, 1, BrandPolicy.EmbeddingBatchSize));
        Assert.All(_generator.Calls.SkipLast(1), call => Assert.Equal(BrandPolicy.EmbeddingBatchSize, call.Count));
        Assert.Equal(set.ChunkCount, _generator.Calls.Sum(call => call.Count));

        var chunks = await ChunksAsync(set.Id);
        Assert.Equal(chunks.Select(chunk => chunk.Text), _generator.Calls.SelectMany(call => call));
    }

    // ---- replay ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_embedding_is_not_run_again_by_another_pass_or_a_sweep()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();
        await EmbedAsync();

        var callsAfterFirstRun = _generator.Calls.Count;

        var again = await EmbedAsync();
        var sweep = await MaintainAsync();

        Assert.Equal(0, again.Claimed);
        Assert.Equal(0, sweep.Enqueued);
        Assert.Equal(callsAfterFirstRun, _generator.Calls.Count);
        Assert.Single(await SetsAsync());
        Assert.Single(await OperationsAsync());
    }

    [Fact]
    public async Task Enqueueing_an_artifact_that_already_has_a_live_operation_is_a_no_op()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();

        var extraction = Assert.Single(await ExtractionsAsync());

        await using var scope = ScopeFor(WorkspaceA);
        var queued = await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingFacade>()
            .EnqueueAsync(extraction.Id, TestContext.Current.CancellationToken);

        Assert.False(queued);
        Assert.Single(await OperationsAsync());
    }

    [Fact]
    public async Task A_second_live_operation_for_one_artifact_is_refused_by_the_database()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();

        var extraction = Assert.Single(await ExtractionsAsync());
        var existing = Assert.Single(await OperationsAsync());

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.BrandSourceEmbeddingOperations.Add(BrandSourceEmbeddingQueue.For(
            WorkspaceA, existing.BrandSourceDocumentId, extraction, _clock.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ---- model change ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_changed_model_builds_a_new_set_and_supersedes_the_old_one_only_when_it_is_whole()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 40));
        await ExtractAsync();
        await EmbedAsync();

        var first = Assert.Single(await SetsAsync());

        _generator.ModelId = "model-b";
        var sweep = await MaintainAsync();

        Assert.Equal(1, sweep.Enqueued);
        Assert.Equal(1, (await EmbedAsync()).Embedded);

        var sets = await SetsAsync();
        var old = Assert.Single(sets, set => set.Id == first.Id);
        var replacement = Assert.Single(sets, set => set.Id != first.Id);

        Assert.Equal(BrandSourceChunkSetStatus.Superseded, old.Status);
        Assert.Equal(_clock.UtcNow, old.SupersededAt);
        Assert.Equal(BrandSourceChunkSetStatus.Current, replacement.Status);
        Assert.Equal("model-b", replacement.EmbeddingModel);

        // Exactly one current set, so retrieval never compares vectors from two models.
        Assert.Single(sets, set => set.Status == BrandSourceChunkSetStatus.Current);
    }

    [Fact]
    public async Task A_superseded_set_is_kept_through_retention_and_then_deleted_with_its_chunks()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();
        await EmbedAsync();

        var first = Assert.Single(await SetsAsync());

        _generator.ModelId = "model-b";
        await MaintainAsync();
        await EmbedAsync();

        // Inside the window: still there, so a reader that selected it a moment ago is not stranded.
        _clock.Advance(BrandPolicy.SupersededSetRetention - TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await MaintainAsync()).Retired);
        Assert.NotEmpty(await ChunksAsync(first.Id));

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await MaintainAsync()).Retired);

        Assert.DoesNotContain(await SetsAsync(), set => set.Id == first.Id);
        Assert.Empty(await ChunksAsync(first.Id));
        Assert.Single(await SetsAsync(), set => set.Status == BrandSourceChunkSetStatus.Current);
    }

    [Fact]
    public async Task A_corrected_artifact_replaces_the_set_of_the_one_it_corrects_only_after_it_is_current()
    {
        var seeded = await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();
        await EmbedAsync();

        var original = Assert.Single(await SetsAsync());
        var extraction = Assert.Single(await ExtractionsAsync());

        await CorrectAsync(seeded, extraction.Id, "Corrected: warm, plain and never breathless.");

        // Queued but not yet run: the set retrieval reads has not moved.
        Assert.Equal(BrandSourceChunkSetStatus.Current, Assert.Single(await SetsAsync()).Status);

        await EmbedAsync();

        var sets = await SetsAsync();
        var replacement = Assert.Single(sets, set => set.Id != original.Id);

        Assert.Equal(BrandSourceChunkSetStatus.Current, replacement.Status);
        Assert.NotEqual(extraction.Id, replacement.BrandSourceExtractionId);
        Assert.Equal(BrandSourceChunkSetStatus.Superseded, sets.Single(set => set.Id == original.Id).Status);
    }

    [Fact]
    public async Task An_embedding_for_an_artifact_that_was_since_corrected_is_cancelled_not_run()
    {
        var seeded = await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();

        var extraction = Assert.Single(await ExtractionsAsync());
        await CorrectAsync(seeded, extraction.Id, "Corrected before anything was embedded.");

        var summary = await EmbedAsync();

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(1, summary.Cancelled);
        Assert.Equal(1, summary.Embedded);

        var cancelled = Assert.Single(
            await OperationsAsync(), operation => operation.Status == BrandSourceEmbeddingOperationStatus.Cancelled);

        Assert.Equal(extraction.Id, cancelled.BrandSourceExtractionId);
        Assert.Null(cancelled.FailureCategory);
        Assert.Single(await SetsAsync());
    }

    [Fact]
    public async Task A_removed_document_is_cancelled_rather_than_embedded()
    {
        var seeded = await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();
        await RemoveAsync(seeded);

        var summary = await EmbedAsync();

        Assert.Equal(1, summary.Cancelled);
        Assert.Empty(await SetsAsync());
        Assert.Empty(_generator.Calls);
    }

    // ---- partial failure and retry ---------------------------------------------------------------------

    [Fact]
    public async Task A_failure_part_way_leaves_the_current_set_untouched_and_a_retry_resumes_without_repaying()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 190));
        await ExtractAsync();
        await EmbedAsync();

        var current = Assert.Single(await SetsAsync());
        var currentChunks = await ChunksAsync(current.Id);

        // A new model, whose third batch fails.
        _generator.ModelId = "model-b";
        _generator.Calls.Clear();
        _generator.FailCall = call => call == 3;

        await MaintainAsync();
        var failed = await EmbedAsync();

        Assert.Equal(1, failed.Requeued);

        // The set retrieval reads is exactly as it was, with every vector it had.
        var afterFailure = await SetsAsync();
        Assert.Equal(BrandSourceChunkSetStatus.Current, afterFailure.Single(set => set.Id == current.Id).Status);
        Assert.Equal(currentChunks.Count, (await ChunksAsync(current.Id)).Count);

        // The new set is still being built, and holds the two batches that did succeed.
        var building = Assert.Single(afterFailure, set => set.Status == BrandSourceChunkSetStatus.Building);
        Assert.Equal(BrandPolicy.EmbeddingBatchSize * 2, (await ChunksAsync(building.Id)).Count);

        var requeued = Assert.Single(
            await OperationsAsync(), operation => operation.Status == BrandSourceEmbeddingOperationStatus.Queued);
        Assert.Equal(1, requeued.Attempts);
        Assert.True(requeued.AvailableAt > _clock.UtcNow);

        // Not due yet: backoff is respected.
        Assert.Equal(0, (await EmbedAsync()).Claimed);

        _generator.FailCall = null;
        _generator.Calls.Clear();
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, (await EmbedAsync()).Embedded);

        // The retry paid only for what was missing.
        var final = await SetsAsync();
        var replacement = Assert.Single(final, set => set.Status == BrandSourceChunkSetStatus.Current && set.Id != current.Id);

        Assert.Equal(replacement.ChunkCount, BrandPolicy.EmbeddingBatchSize * 2 + _generator.Calls.Sum(call => call.Count));
        Assert.Equal(replacement.ChunkCount, (await ChunksAsync(replacement.Id)).Count);
        Assert.Equal(building.Id, replacement.Id);
        Assert.Equal(BrandSourceChunkSetStatus.Superseded, final.Single(set => set.Id == current.Id).Status);
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_exhausts_its_attempts_and_is_not_requeued_by_the_sweep()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();
        _generator.FailCall = _ => true;

        for (var attempt = 1; attempt < BrandPolicy.EmbeddingMaxAttempts; attempt++)
        {
            Assert.Equal(1, (await EmbedAsync()).Requeued);
            _clock.Advance(TimeSpan.FromMinutes(11));
        }

        Assert.Equal(1, (await EmbedAsync()).Failed);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceEmbeddingOperationStatus.Failed, operation.Status);
        Assert.Equal(BrandSourceEmbeddingFailureCategory.ProviderUnavailable, operation.FailureCategory);
        Assert.Equal(BrandPolicy.EmbeddingMaxAttempts, operation.Attempts);
        Assert.Empty(await SetsAsync(includeBuilding: false));

        // A document that always fails is not queued again every minute for ever.
        Assert.Equal(0, (await MaintainAsync()).Enqueued);
    }

    [Fact]
    public async Task A_provider_timeout_is_requeued_and_does_not_abort_the_rest_of_the_pass()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await UploadAsync(WorkspaceB, Text("beta", 4));
        await ExtractAsync();

        // An HttpClient timeout surfaces as a cancellation the caller did not ask for.
        _generator.FailCall = call => call == 1;
        _generator.FailWith = () => new TaskCanceledException("The request timed out.");

        var summary = await EmbedAsync();

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(1, summary.Requeued);
        Assert.Equal(1, summary.Embedded);
    }

    [Fact]
    public async Task A_provider_that_returns_the_wrong_width_is_refused_and_nothing_is_stored()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();
        _generator.Width = 384;

        Assert.Equal(1, (await EmbedAsync()).Failed);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceEmbeddingFailureCategory.InvalidEmbedding, operation.FailureCategory);
        Assert.Empty(await SetsAsync(includeBuilding: false));
        Assert.Empty(await AllChunksAsync());
    }

    [Fact]
    public async Task A_host_with_no_embedding_deployment_fails_the_job_and_a_later_deployment_picks_it_up()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();
        _current = new UnconfiguredEmbeddingGenerator();

        Assert.Equal(1, (await EmbedAsync()).Failed);
        Assert.Equal(
            BrandSourceEmbeddingFailureCategory.ProviderNotConfigured,
            Assert.Single(await OperationsAsync()).FailureCategory);

        // Still none: the sweep has no model to compare against, so it queues nothing.
        Assert.Equal(0, (await MaintainAsync()).Enqueued);

        _current = _generator;

        Assert.Equal(1, (await MaintainAsync()).Enqueued);
        Assert.Equal(1, (await EmbedAsync()).Embedded);
        Assert.Single(await SetsAsync());
    }

    [Fact]
    public async Task A_deployment_that_does_not_name_its_model_is_refused_rather_than_given_a_guessed_name()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();
        _generator.ModelId = null;

        Assert.Equal(1, (await EmbedAsync()).Failed);
        Assert.Equal(
            BrandSourceEmbeddingFailureCategory.ModelUnidentified,
            Assert.Single(await OperationsAsync()).FailureCategory);
        Assert.Empty(await AllChunksAsync());
        Assert.Empty(_generator.Calls);
    }

    [Fact]
    public async Task A_building_set_cut_by_another_chunker_is_discarded_not_resumed()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 190));
        await ExtractAsync();
        _generator.FailCall = call => call == 2;
        await EmbedAsync();

        var stale = Assert.Single(await SetsAsync());
        await MutateSetAsync(stale.Id, set => set.ChunkerId = "text/paragraph-800c-100o@0");

        _generator.FailCall = null;
        _generator.Calls.Clear();
        _clock.Advance(TimeSpan.FromMinutes(11));
        await EmbedAsync();

        var sets = await SetsAsync();
        var current = Assert.Single(sets);

        Assert.NotEqual(stale.Id, current.Id);
        Assert.Equal(BrandSourceChunker.Id, current.ChunkerId);

        // Nothing of the discarded set was reused, so every passage was embedded afresh.
        Assert.Equal(current.ChunkCount, _generator.Calls.Sum(call => call.Count));
    }

    [Fact]
    public async Task A_worker_that_is_shutting_down_hands_the_job_back_uncharged_and_keeps_its_batches()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 190));
        await ExtractAsync();

        using var shutdown = new CancellationTokenSource();
        _generator.OnCall = call =>
        {
            if (call == 2)
            {
                shutdown.Cancel();
                throw new OperationCanceledException(shutdown.Token);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingWorker>()
                .RunPendingAsync(shutdown.Token);
        });

        var operation = Assert.Single(await OperationsAsync());

        // Not failed, not charged: a deploy must not spend a document's retries.
        Assert.Equal(BrandSourceEmbeddingOperationStatus.Queued, operation.Status);
        Assert.Equal(0, operation.Attempts);
        Assert.Null(operation.LeasedBy);

        var building = Assert.Single(await SetsAsync());
        Assert.Equal(BrandPolicy.EmbeddingBatchSize, (await ChunksAsync(building.Id)).Count);

        _generator.OnCall = null;
        Assert.Equal(1, (await EmbedAsync()).Embedded);
        Assert.Equal(building.Id, Assert.Single(await SetsAsync()).Id);
    }

    // ---- lease and crash -------------------------------------------------------------------------------

    [Fact]
    public async Task A_worker_that_lost_its_lease_changes_nothing_and_recovery_requeues_the_job()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();

        BrandSourceEmbeddingClaim? stale;

        await using (var scope = _provider.CreateAsyncScope())
        {
            stale = await scope.ServiceProvider.GetRequiredService<BrandSourceEmbeddingClaimRepository>()
                .ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(stale);

        // The worker dies holding it; time passes; the sweep takes it back.
        _clock.Advance(BrandPolicy.EmbeddingLeaseDuration + TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await MaintainAsync()).Requeued);

        // The first worker comes back to life and tries to carry on under a lease it no longer holds.
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingFacade>()
                .ExecuteAsync(stale!.OperationId, stale.LeaseToken, TestContext.Current.CancellationToken);

            Assert.Equal(BrandSourceEmbeddingRunOutcome.Skipped, outcome);
        }

        Assert.Empty(_generator.Calls);
        Assert.Empty(await SetsAsync());

        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await EmbedAsync()).Embedded);
    }

    [Fact]
    public async Task A_job_that_kills_its_worker_every_time_is_abandoned_after_its_attempts()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 4));
        await ExtractAsync();

        for (var attempt = 1; attempt <= BrandPolicy.EmbeddingMaxAttempts; attempt++)
        {
            await using (var scope = _provider.CreateAsyncScope())
            {
                var claim = await scope.ServiceProvider.GetRequiredService<BrandSourceEmbeddingClaimRepository>()
                    .ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

                Assert.NotNull(claim);
            }

            _clock.Advance(BrandPolicy.EmbeddingLeaseDuration + TimeSpan.FromMinutes(1));
            await MaintainAsync();
            _clock.Advance(TimeSpan.FromMinutes(11));
        }

        var operation = Assert.Single(await OperationsAsync());

        Assert.Equal(BrandSourceEmbeddingOperationStatus.Failed, operation.Status);
        Assert.Equal(BrandSourceEmbeddingFailureCategory.LeaseAbandoned, operation.FailureCategory);
    }

    [Fact]
    public async Task A_building_set_nobody_is_finishing_is_deleted_after_a_day_but_not_while_a_job_is_live()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 190));
        await ExtractAsync();
        _generator.FailCall = call => call == 2;
        await EmbedAsync();

        var building = Assert.Single(await SetsAsync());

        // A day on, but its operation is still waiting to resume it: not abandoned.
        _clock.Advance(BrandPolicy.AbandonedBuildAge + TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await MaintainAsync()).Retired);
        Assert.Contains(await SetsAsync(), set => set.Id == building.Id);

        // The operation is given up on; now nothing will finish the set.
        await MutateOperationAsync(operation =>
        {
            operation.Status = BrandSourceEmbeddingOperationStatus.Cancelled;
            operation.CompletedAt = _clock.UtcNow;
        });

        Assert.Equal(1, (await MaintainAsync()).Retired);
        Assert.Empty(await SetsAsync());
        Assert.Empty(await AllChunksAsync());
    }

    // ---- isolation -------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_workspaces_are_embedded_without_sharing_a_batch_a_set_or_a_chunk()
    {
        await UploadAsync(WorkspaceA, Text("workspace-a-secret", 30));
        await UploadAsync(WorkspaceB, Text("workspace-b-secret", 30));
        await ExtractAsync();

        var summary = await EmbedAsync();

        Assert.Equal(2, summary.Embedded);

        // No provider request mixed workspaces: each carried passages of one alone.
        Assert.All(_generator.Calls, call =>
        {
            var inA = call.Count(text => text.Contains("workspace-a-secret", StringComparison.Ordinal));
            var inB = call.Count(text => text.Contains("workspace-b-secret", StringComparison.Ordinal));

            Assert.True(inA == call.Count || inB == call.Count, "a batch mixed two workspaces' passages");
        });

        foreach (var (workspace, own, foreign) in new[]
        {
            (WorkspaceA, "workspace-a-secret", "workspace-b-secret"),
            (WorkspaceB, "workspace-b-secret", "workspace-a-secret"),
        })
        {
            var sets = await SetsAsync(workspace);
            var set = Assert.Single(sets);
            var chunks = await ChunksAsync(set.Id, workspace);

            Assert.Equal(workspace, set.WorkspaceId);
            Assert.All(chunks, chunk =>
            {
                Assert.Equal(workspace, chunk.WorkspaceId);
                Assert.Contains(own, chunk.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(foreign, chunk.Text, StringComparison.Ordinal);
            });

            Assert.Single(await OperationsAsync(workspace));
        }
    }

    [Fact]
    public async Task A_claim_run_under_another_workspace_finds_nothing_and_writes_nothing()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();

        BrandSourceEmbeddingClaim? claim;

        await using (var scope = _provider.CreateAsyncScope())
        {
            claim = await scope.ServiceProvider.GetRequiredService<BrandSourceEmbeddingClaimRepository>()
                .ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(claim);
        Assert.Equal(WorkspaceA, claim.WorkspaceId);

        // A tampered claim, or a worker that resolved the wrong workspace: the row is workspace-owned, so B
        // cannot see it. Finding a row is not authorization, and here there is nothing to find.
        await using var wrong = ScopeFor(WorkspaceB);
        var outcome = await wrong.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingFacade>()
            .ExecuteAsync(claim.OperationId, claim.LeaseToken, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceEmbeddingRunOutcome.Skipped, outcome);
        Assert.Empty(_generator.Calls);
        Assert.Empty(await SetsAsync(WorkspaceA));
        Assert.Empty(await SetsAsync(WorkspaceB));
        Assert.Empty(await AllChunksAsync());

        // B changed nothing about A's operation: still running under the lease it was claimed with, and
        // invisible to B altogether.
        var untouched = Assert.Single(await OperationsAsync(WorkspaceA));
        Assert.Equal(BrandSourceEmbeddingOperationStatus.Running, untouched.Status);
        Assert.Equal(claim.LeaseToken, untouched.LeasedBy);
        Assert.Equal(1, untouched.Attempts);
        Assert.Empty(await OperationsAsync(WorkspaceB));
    }

    [Fact]
    public async Task One_workspace_cannot_enqueue_retire_or_see_another_workspaces_embedding_state()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 12));
        await ExtractAsync();
        await EmbedAsync();

        var extraction = Assert.Single(await ExtractionsAsync());
        var set = Assert.Single(await SetsAsync());
        await MutateSetAsync(set.Id, row =>
        {
            row.Status = BrandSourceChunkSetStatus.Superseded;
            row.SupersededAt = null;
        });

        await using var other = ScopeFor(WorkspaceB);
        var facade = other.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingFacade>();

        Assert.False(await facade.EnqueueAsync(extraction.Id, TestContext.Current.CancellationToken));
        Assert.False(await facade.RetireSetAsync(set.Id, TestContext.Current.CancellationToken));

        // A's set is still there: B's attempt to retire it found no row to retire.
        Assert.Single(await SetsAsync(WorkspaceA));
        Assert.NotEmpty(await ChunksAsync(set.Id, WorkspaceA));
    }

    [Fact]
    public async Task The_sweep_names_the_owning_workspace_for_every_artifact_it_backfills()
    {
        await UploadAsync(WorkspaceA, Text("alpha", 12));
        await UploadAsync(WorkspaceB, Text("beta", 12));
        await ExtractAsync();
        await EmbedAsync();

        _generator.ModelId = "model-b";

        await using var scope = _provider.CreateAsyncScope();
        var due = await scope.ServiceProvider.GetRequiredService<BrandSourceEmbeddingClaimRepository>()
            .FindBackfillAsync("model-b", TestContext.Current.CancellationToken);

        Assert.Equal(2, due.Count);
        Assert.Equal([WorkspaceA, WorkspaceB], due.Select(row => row.WorkspaceId).Order());

        var extractions = new[] { WorkspaceA, WorkspaceB }
            .ToDictionary(workspace => workspace, workspace => ExtractionsAsync(workspace).Result.Single().Id);

        Assert.All(due, row => Assert.Equal(extractions[row.WorkspaceId], row.ExtractionId));
    }

    // ---- schema ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_refuses_to_queue_an_embedding_for_an_extraction_that_produced_no_text()
    {
        await UploadAsync(WorkspaceA, BrandSourceSampleFiles.Png(), "reference.png");
        await ExtractAsync();

        var unsupported = Assert.Single(await ExtractionsAsync());
        Assert.Equal(BrandSourceExtractionStatus.Unsupported, unsupported.Status);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var document = await db.BrandSourceDocuments.SingleAsync(TestContext.Current.CancellationToken);

        db.BrandSourceEmbeddingOperations.Add(BrandSourceEmbeddingQueue.For(
            WorkspaceA, document.Id, unsupported.Id, unsupported.BrandSourceDocumentVersionId, _clock.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private sealed record Seeded(Guid DocumentId, Guid VersionId);

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "acct");

        return scope;
    }

    /// <summary>Distinct, long-enough paragraphs, so the chunker has real boundaries to choose between.</summary>
    private static byte[] Text(string marker, int paragraphs) =>
        Encoding.UTF8.GetBytes(string.Join(
            "\n\n",
            Enumerable.Range(1, paragraphs).Select(index =>
                $"{marker} paragraph {index}. "
                + string.Concat(Enumerable.Repeat("Warm, plain and never breathless. ", 7)))));

    private async Task<Seeded> UploadAsync(Guid workspaceId, byte[] bytes, string fileName = "notes.txt")
    {
        await using var scope = ScopeFor(workspaceId);

        var uploaded = await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>().UploadAsync(
            "user",
            new UploadBrandSourceDocumentViewModel
            {
                Title = "House style",
                DocumentType = BrandSourceDocumentType.StyleGuide,
                Purpose = BrandSourcePurpose.Voice,
            },
            new BrandSourceUploadFile(new MemoryStream(bytes), fileName),
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.True(uploaded.Result.Succeeded, uploaded.Result.Error?.Message);

        return new Seeded(uploaded.Result.Value!.Id, uploaded.Result.Value.CurrentVersion.Id);
    }

    private async Task ExtractAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBrandSourceExtractionWorker>()
            .RunPendingAsync(TestContext.Current.CancellationToken);
    }

    private async Task<BrandSourceEmbeddingPassSummary> EmbedAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingWorker>()
            .RunPendingAsync(TestContext.Current.CancellationToken);
    }

    private async Task<BrandSourceEmbeddingMaintenanceSummary> MaintainAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IBrandSourceEmbeddingWorker>()
            .RunMaintenanceAsync(TestContext.Current.CancellationToken);
    }

    private async Task CorrectAsync(Seeded seeded, Guid expectedExtractionId, string text)
    {
        await using var scope = ScopeFor(WorkspaceA);

        var outcome = await scope.ServiceProvider.GetRequiredService<IBrandSourceExtractionReviewFacade>()
            .CorrectAsync(
                "user",
                seeded.DocumentId,
                versionNumber: 1,
                new CorrectBrandSourceExtractionViewModel
                {
                    ExpectedExtractionId = expectedExtractionId,
                    Text = text,
                    Reason = "Fixed a word.",
                },
                Guid.NewGuid().ToString("N"),
                TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
    }

    private async Task RemoveAsync(Seeded seeded)
    {
        await using var scope = ScopeFor(WorkspaceA);
        var documents = scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>();
        var detail = await documents.GetAsync(seeded.DocumentId, TestContext.Current.CancellationToken);

        var removed = await documents.TransitionAsync(
            "user",
            seeded.DocumentId,
            BrandSourceDocumentLifecycleCommand.Remove,
            new BrandSourceDocumentLifecycleViewModel { ExpectedConcurrencyToken = detail.Value!.ConcurrencyToken },
            TestContext.Current.CancellationToken);

        Assert.True(removed.Succeeded, removed.Error?.Message);
    }

    private async Task<List<BrandSourceEmbeddingOperation>> OperationsAsync(Guid? workspaceId = null)
    {
        await using var scope = ScopeFor(workspaceId ?? WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandSourceEmbeddingOperations
            .AsNoTracking()
            .OrderBy(operation => operation.QueuedAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<BrandSourceExtraction>> ExtractionsAsync(Guid? workspaceId = null)
    {
        await using var scope = ScopeFor(workspaceId ?? WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandSourceExtractions
            .AsNoTracking()
            .OrderBy(extraction => extraction.Ordinal)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<BrandSourceChunkSet>> SetsAsync(Guid? workspaceId = null, bool includeBuilding = true)
    {
        await using var scope = ScopeFor(workspaceId ?? WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandSourceChunkSets
            .AsNoTracking()
            .Where(set => includeBuilding || set.Status != BrandSourceChunkSetStatus.Building)
            .OrderBy(set => set.CreatedAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<BrandSourceChunk>> ChunksAsync(Guid setId, Guid? workspaceId = null)
    {
        await using var scope = ScopeFor(workspaceId ?? WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandSourceChunks
            .AsNoTracking()
            .Where(chunk => chunk.BrandSourceChunkSetId == setId)
            .OrderBy(chunk => chunk.Ordinal)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every chunk in the database, across workspaces — to prove one does not exist at all.</summary>
    private async Task<List<BrandSourceChunk>> AllChunksAsync()
    {
        var all = new List<BrandSourceChunk>();

        foreach (var workspace in new[] { WorkspaceA, WorkspaceB })
        {
            await using var scope = ScopeFor(workspace);
            all.AddRange(await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .BrandSourceChunks.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        }

        return all;
    }

    private async Task<byte[]> ArtifactBytesAsync(BrandSourceExtraction extraction)
    {
        await using var stored = await _store.OpenReadAsync(
            BrandSourceObjectKey.Container, extraction.ExtractedTextObjectKey!, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        using var buffer = new MemoryStream();
        await stored.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);

        return buffer.ToArray();
    }

    private async Task MutateSetAsync(Guid setId, Action<BrandSourceChunkSet> change)
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var set = await db.BrandSourceChunkSets.SingleAsync(row => row.Id == setId, TestContext.Current.CancellationToken);

        change(set);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task MutateOperationAsync(Action<BrandSourceEmbeddingOperation> change)
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operation = await db.BrandSourceEmbeddingOperations.SingleAsync(TestContext.Current.CancellationToken);

        change(operation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    /// A deterministic embedding deployment. A passage's vector is a unit vector on an axis its text hashes to,
    /// so a stored vector is checked by recomputing it, and no passage is ever seen by anything but this fake.
    /// </summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public string? ModelId { get; set; } = "model-a";

        public int Width { get; set; } = BrandPolicy.EmbeddingDimension;

        /// <summary>Throws on the call (1-based) this returns true for, the way an unreachable provider does.</summary>
        public Func<int, bool>? FailCall { get; set; }

        /// <summary>What a failing call throws. An unreachable provider by default.</summary>
        public Func<Exception> FailWith { get; set; } = () => new HttpRequestException("The fake provider is unavailable.");

        /// <summary>Runs on every call, before the answer, with the 1-based call number.</summary>
        public Action<int>? OnCall { get; set; }

        /// <summary>The passages of every request made, in order.</summary>
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public static float[] VectorFor(string text, int width = BrandPolicy.EmbeddingDimension)
        {
            var vector = new float[width];
            var axis = (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text))) % (uint)width);
            vector[axis] = 1f;

            return vector;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var texts = values.ToList();
            Calls.Add(texts);
            var call = Calls.Count;

            OnCall?.Invoke(call);

            if (FailCall?.Invoke(call) == true)
            {
                throw FailWith();
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                texts.Select(text => new Embedding<float>(VectorFor(text, Width))).ToList()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is not null)
            {
                return null;
            }

            if (serviceType == typeof(EmbeddingGeneratorMetadata))
            {
                return ModelId is null ? null : new EmbeddingGeneratorMetadata("fake", null, ModelId, Width);
            }

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
