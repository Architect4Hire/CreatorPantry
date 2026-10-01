using System.Text;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The document extraction queue end to end: what an upload queues, what a worker pass does with it, what it
/// refuses to retry, how a lost lease and a dead worker are recovered, and that none of it crosses a workspace.
/// </summary>
/// <remarks>
/// <para>
/// Over SQLite, so these run without a container. The consequence to know is the one
/// <see cref="SqliteModelCustomizer"/> documents: <c>RowVersion</c> is filled on insert and never moves, so a
/// writer losing at the save is not reproducible here. Every lease assertion in this file is therefore made
/// against the application's own token check rather than against a concurrency exception — which is the guard
/// that matters anyway, since it is the one that fires before any work is wasted.
/// </para>
/// <para>
/// The clock is movable and nothing sleeps: a lapsed lease is a clock that moved, not a test that waited.
/// </para>
/// </remarks>
public sealed class BrandSourceExtractionQueueTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly ServiceProvider _provider;

    private bool _refuseExtractedWrites;

    public BrandSourceExtractionQueueTests()
    {
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
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

        // The module registers a store that refuses everything and a scanner that clears nothing; neither is
        // what these tests are about.
        services.RemoveAll<IPrivateObjectStore>();
        services.AddSingleton<IPrivateObjectStore>(
            new SelectivelyUnavailableStore(_store, () => _refuseExtractedWrites));
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

    // ---- what an upload queues -------------------------------------------------------------------------

    [Fact]
    public async Task An_upload_queues_exactly_one_due_extraction_for_its_version()
    {
        var seeded = await UploadAsync(WorkspaceA);

        var operations = await OperationsAsync();
        var operation = Assert.Single(operations);

        Assert.Equal(WorkspaceA, operation.WorkspaceId);
        Assert.Equal(seeded.VersionId, operation.BrandSourceDocumentVersionId);
        Assert.Equal(seeded.DocumentId, operation.BrandSourceDocumentId);
        Assert.Equal(BrandSourceExtractionOperationStatus.Queued, operation.Status);
        Assert.Equal(0, operation.Attempts);

        // Due immediately: the version has committed and nothing else has to happen first.
        Assert.Equal(_clock.UtcNow, operation.AvailableAt);
        Assert.Null(operation.StartedAt);
        Assert.Null(operation.CompletedAt);
        Assert.Null(operation.LeasedBy);
    }

    [Fact]
    public async Task A_replayed_upload_queues_one_extraction_rather_than_two()
    {
        var key = Guid.NewGuid().ToString("N");

        var first = await UploadAsync(WorkspaceA, idempotencyKey: key);
        var replayed = await UploadAsync(WorkspaceA, idempotencyKey: key);

        Assert.Equal(first.VersionId, replayed.VersionId);
        Assert.Single(await OperationsAsync());
    }

    [Fact]
    public async Task A_replacement_queues_an_extraction_for_the_new_version_and_leaves_the_old_one_alone()
    {
        var seeded = await UploadAsync(WorkspaceA);
        await RunPassAsync();

        var replaced = await ReplaceAsync(WorkspaceA, seeded.DocumentId, await TokenAsync(WorkspaceA, seeded.DocumentId));

        var operations = await OperationsAsync();

        // One per version, and the clock has not moved, so this is a set rather than a sequence.
        Assert.Equal(2, operations.Count);
        Assert.Equal(
            new HashSet<Guid> { seeded.VersionId, replaced.VersionId },
            operations.Select(row => row.BrandSourceDocumentVersionId).ToHashSet());

        // The first version's extraction already ran and stays Completed: a guide version that cited that text
        // still cites it, because a citation pins a version rather than a document.
        var completed = Assert.Single(operations, row => row.Status is BrandSourceExtractionOperationStatus.Completed);
        Assert.Equal(seeded.VersionId, completed.BrandSourceDocumentVersionId);

        var queued = Assert.Single(operations, row => row.Status is BrandSourceExtractionOperationStatus.Queued);
        Assert.Equal(replaced.VersionId, queued.BrandSourceDocumentVersionId);
    }

    // ---- a worker pass ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_pass_extracts_a_pdf_and_records_the_pointer_status_and_provenance()
    {
        var seeded = await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm, plain, never breathless."));

        var summary = await RunPassAsync();

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(1, summary.Extracted);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, operation.Status);
        Assert.Equal("pdf/pdfpig@0.1.16", operation.ExtractorId);
        Assert.Equal(1, operation.Attempts);
        Assert.NotNull(operation.StartedAt);
        Assert.Equal(_clock.UtcNow, operation.CompletedAt);
        Assert.Null(operation.LeasedBy);
        Assert.Null(operation.FailureCategory);

        var extraction = Assert.Single(await ExtractionsAsync());
        Assert.Equal(operation.BrandSourceExtractionId, extraction.Id);
        Assert.Equal(WorkspaceA, extraction.WorkspaceId);
        Assert.Equal(seeded.VersionId, extraction.BrandSourceDocumentVersionId);
        Assert.Equal(1, extraction.Ordinal);
        Assert.Equal(BrandSourceExtractionStatus.Succeeded, extraction.Status);
        Assert.Equal(BrandSourceExtractionOrigin.Extracted, extraction.Origin);
        Assert.StartsWith("sha256:", extraction.ContentChecksum);
        Assert.Null(extraction.Reason);

        // A worker's attempt belongs to no member.
        Assert.Null(extraction.CreatedByMembershipId);

        // The pointer is a key under this workspace's own prefix, and the bytes are there.
        Assert.Equal(
            BrandSourceObjectKey.ForExtractedText(WorkspaceA, seeded.DocumentId, seeded.VersionId, 1),
            extraction.ExtractedTextObjectKey);
        Assert.Contains("Warm, plain, never breathless.", await StoredTextAsync(extraction.ExtractedTextObjectKey!));

        // And the library now says so.
        Assert.Equal(BrandSourceExtractionState.Succeeded, (await DetailAsync(WorkspaceA, seeded.DocumentId)).Extraction.State);
    }

    [Fact]
    public async Task The_audit_entry_names_the_version_and_the_parser_and_no_actor()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));
        await RunPassAsync();

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var entry = await db.AuditLogs.SingleAsync(
            row => row.Action == BrandAuditActions.SourceDocumentExtracted, TestContext.Current.CancellationToken);

        Assert.Null(entry.ActorUserId);
        Assert.Contains("Version 1", entry.Summary);
        Assert.Contains("pdf/pdfpig", entry.Summary);

        // Never a word of the document.
        Assert.DoesNotContain("Warm and plain", entry.Summary);
    }

    [Fact]
    public async Task An_image_is_reviewed_rather_than_left_looking_unextracted()
    {
        var seeded = await UploadAsync(WorkspaceA, BrandSourceSampleFiles.Png(), "reference.png");

        var summary = await RunPassAsync();

        Assert.Equal(1, summary.Reviewed);
        Assert.Equal(0, summary.Extracted);

        var extraction = Assert.Single(await ExtractionsAsync());
        Assert.Equal(BrandSourceExtractionStatus.Unsupported, extraction.Status);
        Assert.Equal(BrandSourceExtractionReasons.Image, extraction.Reason);

        // The schema's own rule: a review state carries no pointer, because there are no bytes to point at.
        Assert.Null(extraction.ExtractedTextObjectKey);
        Assert.Null(extraction.ContentChecksum);
        Assert.DoesNotContain(_store.Keys, key => key.Contains("/extracted/", StringComparison.Ordinal));

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, operation.Status);
        Assert.Equal("image/none@1", operation.ExtractorId);
        Assert.Equal(
            BrandSourceExtractionState.Unsupported, (await DetailAsync(WorkspaceA, seeded.DocumentId)).Extraction.State);
    }

    [Fact]
    public async Task A_corrupt_document_completes_its_operation_and_is_not_retried()
    {
        // The sample the upload accepts on its header and trailer alone; a real parser cannot open it.
        var seeded = await UploadAsync(WorkspaceA, BrandSourceSampleFiles.Pdf());

        Assert.Equal(1, (await RunPassAsync()).Reviewed);

        var extraction = Assert.Single(await ExtractionsAsync());
        Assert.Equal(BrandSourceExtractionStatus.Failed, extraction.Status);
        Assert.NotNull(extraction.Reason);

        // Completed, not Failed: the work was done and the answer is a fact about the document. Retrying the
        // same bytes would give the same answer, which is why this must not sit on the retry path.
        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, operation.Status);
        Assert.Null(operation.FailureCategory);
        Assert.Equal(
            BrandSourceExtractionState.Failed, (await DetailAsync(WorkspaceA, seeded.DocumentId)).Extraction.State);
    }

    [Fact]
    public async Task Markdown_html_and_plain_text_each_reach_a_stored_artifact()
    {
        var markdown = await UploadAsync(WorkspaceA, BrandSourceSampleFiles.Text(), "house-style.md");
        var html = await UploadAsync(
            WorkspaceA, Encoding.UTF8.GetBytes("<h1>Voice</h1><p>Warm and plain.</p>"), "voice.html");
        var text = await UploadAsync(WorkspaceA, Encoding.UTF8.GetBytes("Evergreen first.\n"), "notes.txt");

        var summary = await RunPassAsync();

        Assert.Equal(3, summary.Claimed);
        Assert.Equal(3, summary.Extracted);

        var extractions = await ExtractionsAsync();
        Assert.Equal(3, extractions.Count);
        Assert.All(extractions, extraction => Assert.Equal(BrandSourceExtractionStatus.Succeeded, extraction.Status));

        Assert.Contains("# House style", await TextForAsync(markdown));
        Assert.Equal("# Voice\n\nWarm and plain.\n", await TextForAsync(html));
        Assert.Equal("Evergreen first.\n", await TextForAsync(text));
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_because_every_ending_is_terminal()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));
        await RunPassAsync();

        var second = await RunPassAsync();

        Assert.Equal(0, second.Claimed);
        Assert.Single(await ExtractionsAsync());
    }

    // ---- transient faults ------------------------------------------------------------------------------

    [Fact]
    public async Task A_source_that_cannot_be_read_requeues_with_backoff_and_writes_no_extraction()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        _store.Unavailable = true;
        var summary = await RunPassAsync();
        _store.Unavailable = false;

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(1, summary.Requeued);
        Assert.Empty(await ExtractionsAsync());

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Queued, operation.Status);
        Assert.Equal(1, operation.Attempts);
        Assert.Equal(_clock.UtcNow + BrandPolicy.ExtractionBackoffFor(1), operation.AvailableAt);
        Assert.Null(operation.LeasedBy);
        Assert.Null(operation.FailureCategory);

        // And it is not claimable again until the backoff has passed, which is the point of setting one.
        Assert.Equal(0, (await RunPassAsync()).Claimed);

        _clock.Advance(BrandPolicy.ExtractionBackoffFor(1));
        Assert.Equal(1, (await RunPassAsync()).Extracted);
    }

    [Fact]
    public async Task Extracted_text_that_cannot_be_stored_requeues_and_leaves_no_half_written_row()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        _refuseExtractedWrites = true;
        var summary = await RunPassAsync();
        _refuseExtractedWrites = false;

        Assert.Equal(1, summary.Requeued);

        // Nothing committed: no row naming bytes that are not there, and no bytes no row will name.
        Assert.Empty(await ExtractionsAsync());
        Assert.DoesNotContain(_store.Keys, key => key.Contains("/extracted/", StringComparison.Ordinal));

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Queued, operation.Status);
    }

    [Fact]
    public async Task A_transient_fault_at_the_attempt_bound_fails_rather_than_requeueing_again()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));
        await MutateAsync(operation => operation.Attempts = BrandPolicy.ExtractionMaxAttempts - 1);

        _store.Unavailable = true;
        var summary = await RunPassAsync();
        _store.Unavailable = false;

        Assert.Equal(1, summary.Failed);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Failed, operation.Status);
        Assert.Equal(BrandSourceExtractionFailureCategory.Storage, operation.FailureCategory);
        Assert.NotNull(operation.FailureSummary);
        Assert.Equal(_clock.UtcNow, operation.CompletedAt);
        Assert.Empty(await ExtractionsAsync());
    }

    [Fact]
    public async Task A_committed_version_whose_object_has_vanished_fails_outright()
    {
        var seeded = await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        // The upload writes the object before the row, so this can only be something outside the application.
        await _store.DeleteAsync(
            BrandSourceObjectKey.Container,
            BrandSourceObjectKey.ForOriginal(WorkspaceA, seeded.DocumentId, seeded.VersionId),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, (await RunPassAsync()).Failed);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Failed, operation.Status);
        Assert.Equal(BrandSourceExtractionFailureCategory.SourceMissing, operation.FailureCategory);
        Assert.Empty(await ExtractionsAsync());
    }

    // ---- leases, crashes and restarts ------------------------------------------------------------------

    [Fact]
    public async Task Two_workers_cannot_claim_the_same_extraction()
    {
        await UploadAsync(WorkspaceA);

        await using var first = _provider.CreateAsyncScope();
        await using var second = _provider.CreateAsyncScope();

        var won = await Claims(first).ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        var lost = await Claims(second).ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

        Assert.NotNull(won);
        Assert.Null(lost);
    }

    [Fact]
    public async Task A_claim_carries_identifiers_and_has_nowhere_to_put_anything_else()
    {
        await UploadAsync(WorkspaceA);

        await using var scope = _provider.CreateAsyncScope();
        var claim = await Claims(scope).ClaimNextAsync(
            Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

        Assert.NotNull(claim);

        // The condition tenancy.md attaches to the queue-claim carve-out, asserted on the type rather than on
        // one instance: a record with a string on it could carry a title or a filename across the boundary.
        Assert.All(
            claim.GetType().GetProperties(),
            property => Assert.True(
                property.PropertyType == typeof(Guid) || property.PropertyType == typeof(int),
                $"{property.Name} is {property.PropertyType.Name}; a claim carries identifiers only"));
    }

    [Fact]
    public async Task A_worker_whose_lease_was_taken_writes_nothing()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        await using var scope = _provider.CreateAsyncScope();
        var claim = await Claims(scope).ClaimNextAsync(
            Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

        // Another worker now holds it: a recovery sweep reclaimed it while this one was between steps.
        await MutateAsync(operation => operation.LeasedBy = Guid.NewGuid());

        await using var running = ScopeFor(WorkspaceA);
        var outcome = await running.ServiceProvider.GetRequiredService<IBrandSourceExtractionFacade>()
            .ExecuteAsync(claim!.OperationId, claim.LeaseToken, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceExtractionRunOutcome.Skipped, outcome);
        Assert.Empty(await ExtractionsAsync());

        // The holder's row is left exactly as it was, not completed by the worker that lost it.
        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Running, operation.Status);
        Assert.Null(operation.BrandSourceExtractionId);
    }

    [Fact]
    public async Task A_lapsed_lease_returns_the_extraction_to_the_queue_with_backoff()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await Claims(scope).ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        }

        _clock.Advance(BrandPolicy.ExtractionLeaseDuration + TimeSpan.FromSeconds(1));
        var maintenance = await RunMaintenanceAsync();

        Assert.Equal(1, maintenance.Requeued);
        Assert.Equal(0, maintenance.Abandoned);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Queued, operation.Status);
        Assert.Null(operation.LeasedBy);
        Assert.Null(operation.LeaseExpiresAt);
        Assert.Equal(_clock.UtcNow + BrandPolicy.ExtractionBackoffFor(1), operation.AvailableAt);

        // StartedAt stays set: the operation has genuinely started before, and clearing it would erase the
        // evidence that a worker ever picked it up.
        Assert.NotNull(operation.StartedAt);
    }

    [Fact]
    public async Task A_lapsed_lease_at_the_attempt_bound_is_abandoned_rather_than_requeued_forever()
    {
        await UploadAsync(WorkspaceA);
        await MutateAsync(operation => operation.Attempts = BrandPolicy.ExtractionMaxAttempts - 1);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await Claims(scope).ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        }

        _clock.Advance(BrandPolicy.ExtractionLeaseDuration + TimeSpan.FromSeconds(1));
        var maintenance = await RunMaintenanceAsync();

        Assert.Equal(0, maintenance.Requeued);
        Assert.Equal(1, maintenance.Abandoned);

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Failed, operation.Status);
        Assert.Equal(BrandSourceExtractionFailureCategory.LeaseAbandoned, operation.FailureCategory);
        Assert.Equal(BrandPolicy.ExtractionMaxAttempts, operation.Attempts);
        Assert.Empty(await ExtractionsAsync());
    }

    [Fact]
    public async Task A_worker_that_died_mid_run_leaves_the_version_extracted_exactly_once_after_recovery()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        // Claimed and then nothing: the process went away holding the lease.
        await using (var scope = _provider.CreateAsyncScope())
        {
            await Claims(scope).ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        }

        _clock.Advance(BrandPolicy.ExtractionLeaseDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await RunMaintenanceAsync()).Requeued);

        _clock.Advance(BrandPolicy.ExtractionBackoffFor(1));
        Assert.Equal(1, (await RunPassAsync()).Extracted);

        // One extraction, at ordinal 1, and one artifact. The restart did not produce a second of either.
        var extraction = Assert.Single(await ExtractionsAsync());
        Assert.Equal(1, extraction.Ordinal);
        Assert.Single(_store.Keys.Where(key => key.Contains("/extracted/", StringComparison.Ordinal)));

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, operation.Status);
        Assert.Equal(2, operation.Attempts);
    }

    [Fact]
    public async Task A_retry_after_a_failed_commit_replaces_its_own_litter_rather_than_keeping_it()
    {
        var seeded = await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        // An artifact at the ordinal this operation is about to offer, left by an attempt that stored bytes and
        // then failed to commit. Nothing committed names it, so the retry must replace it.
        var key = BrandSourceObjectKey.ForExtractedText(WorkspaceA, seeded.DocumentId, seeded.VersionId, 1);
        await _store.PutAsync(
            BrandSourceObjectKey.Container,
            key,
            new MemoryStream(Encoding.UTF8.GetBytes("litter from an attempt that never committed")),
            "text/plain; charset=utf-8",
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, (await RunPassAsync()).Extracted);

        var extraction = Assert.Single(await ExtractionsAsync());
        Assert.Equal(key, extraction.ExtractedTextObjectKey);

        var stored = await StoredTextAsync(key);
        Assert.Contains("Warm and plain.", stored);
        Assert.DoesNotContain("litter", stored);
    }

    [Fact]
    public async Task A_document_removed_before_its_extraction_ran_is_cancelled_rather_than_failed()
    {
        var seeded = await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var removed = await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>().TransitionAsync(
                "user",
                seeded.DocumentId,
                BrandSourceDocumentLifecycleCommand.Remove,
                new BrandSourceDocumentLifecycleViewModel
                {
                    ExpectedConcurrencyToken = await TokenAsync(WorkspaceA, seeded.DocumentId),
                },
                TestContext.Current.CancellationToken);

            Assert.True(removed.Succeeded, removed.Error?.Message);
        }

        var summary = await RunPassAsync();

        Assert.Equal(1, summary.Cancelled);
        Assert.Empty(await ExtractionsAsync());

        var operation = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Cancelled, operation.Status);

        // Not a failure, so nothing reads as though something went wrong.
        Assert.Null(operation.FailureCategory);
        Assert.Equal(_clock.UtcNow, operation.CompletedAt);
    }

    [Fact]
    public async Task An_archived_document_is_still_extracted()
    {
        // Archiving is a shelf, not a deletion: the document still reads and still downloads, so reading it as
        // text is not something an archive should prevent.
        var seeded = await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        await using (var scope = ScopeFor(WorkspaceA))
        {
            await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>().TransitionAsync(
                "user",
                seeded.DocumentId,
                BrandSourceDocumentLifecycleCommand.Archive,
                new BrandSourceDocumentLifecycleViewModel
                {
                    ExpectedConcurrencyToken = await TokenAsync(WorkspaceA, seeded.DocumentId),
                },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, (await RunPassAsync()).Extracted);
        Assert.Equal(BrandSourceExtractionStatus.Succeeded, Assert.Single(await ExtractionsAsync()).Status);
    }

    // ---- corrections on top of a parser's work ---------------------------------------------------------

    [Fact]
    public async Task A_correction_appends_to_the_parser_s_artifact_and_leaves_its_operation_alone()
    {
        var seeded = await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plian."));
        await RunPassAsync();

        var parsed = Assert.Single(await ExtractionsAsync());
        var operationBefore = Assert.Single(await OperationsAsync());

        var corrected = await CorrectAsync(seeded, parsed.Id, "# House style\n\nWarm and plain.\n");

        Assert.Equal(2, corrected.Ordinal);
        Assert.Equal(BrandSourceExtractionOrigin.Corrected, corrected.Origin);
        Assert.Equal(BrandSourceExtractionState.Succeeded, corrected.State);

        // Two artifacts, and the parser's is byte for byte what it was: a chunk or a guide version citing it
        // still resolves to the same text.
        var artifacts = await ExtractionsAsync();
        Assert.Equal([1, 2], artifacts.Select(row => row.Ordinal));
        Assert.Equal(BrandSourceExtractionOrigin.Extracted, artifacts[0].Origin);
        Assert.Contains("plian", await StoredTextAsync(artifacts[0].ExtractedTextObjectKey!));
        Assert.Equal("# House style\n\nWarm and plain.\n", await StoredTextAsync(artifacts[1].ExtractedTextObjectKey!));

        // The uploaded file is untouched, and so is the operation row: it records what that run did, not what
        // the version's current text is.
        Assert.Contains(
            BrandSourceObjectKey.ForOriginal(WorkspaceA, seeded.DocumentId, seeded.VersionId), _store.Keys);

        var operationAfter = Assert.Single(await OperationsAsync());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, operationAfter.Status);
        Assert.Equal(parsed.Id, operationAfter.BrandSourceExtractionId);
        Assert.Equal(operationBefore.ExtractorId, operationAfter.ExtractorId);
        Assert.Equal(operationBefore.CompletedAt, operationAfter.CompletedAt);

        // And a second pass still finds nothing: a correction does not re-open the queue.
        Assert.Equal(0, (await RunPassAsync()).Claimed);
    }

    [Fact]
    public async Task Correcting_an_image_s_review_state_makes_the_library_read_as_corrected_text()
    {
        var seeded = await UploadAsync(WorkspaceA, BrandSourceSampleFiles.Png(), "reference.png");
        await RunPassAsync();

        var reviewed = Assert.Single(await ExtractionsAsync());
        Assert.Equal(BrandSourceExtractionStatus.Unsupported, reviewed.Status);

        await CorrectAsync(seeded, reviewed.Id, "Evergreen on warm paper, hand-lettered.\n");

        // The whole point of the route: a picture with no text becomes a document with text, and the library
        // surfaces say so through the state they already publish.
        var detail = await DetailAsync(WorkspaceA, seeded.DocumentId);
        Assert.Equal(BrandSourceExtractionState.Succeeded, detail.Extraction.State);
        Assert.Equal(BrandSourceExtractionOrigin.Corrected, detail.Extraction.Origin);

        // The review state it replaced is still on the record, carrying why there was no text to begin with.
        var artifacts = await ExtractionsAsync();
        Assert.Equal(BrandSourceExtractionReasons.Image, artifacts[0].Reason);
        Assert.Null(artifacts[0].ExtractedTextObjectKey);
        Assert.NotNull(artifacts[1].CreatedByMembershipId);
    }

    // ---- the workspace boundary ------------------------------------------------------------------------

    [Fact]
    public async Task One_pass_over_two_workspaces_keeps_every_row_and_every_artifact_on_its_own_side()
    {
        var a = await UploadAsync(WorkspaceA, PdfBytes("A house style", "Workspace A voice."));
        var b = await UploadAsync(WorkspaceB, PdfBytes("B house style", "Workspace B voice."));

        var summary = await RunPassAsync();

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(2, summary.Extracted);

        var fromA = await ExtractionsAsync(WorkspaceA);
        var fromB = await ExtractionsAsync(WorkspaceB);

        // Each workspace sees one extraction: its own. The global filter is what makes the other absent.
        Assert.Equal(a.VersionId, Assert.Single(fromA).BrandSourceDocumentVersionId);
        Assert.Equal(b.VersionId, Assert.Single(fromB).BrandSourceDocumentVersionId);

        // And each artifact is under its own workspace's prefix, with the other's text nowhere in it.
        Assert.StartsWith($"workspaces/{WorkspaceA:N}/", fromA[0].ExtractedTextObjectKey);
        Assert.StartsWith($"workspaces/{WorkspaceB:N}/", fromB[0].ExtractedTextObjectKey);
        Assert.Contains("Workspace A voice.", await StoredTextAsync(fromA[0].ExtractedTextObjectKey!));
        Assert.DoesNotContain("Workspace B", await StoredTextAsync(fromA[0].ExtractedTextObjectKey!));
    }

    [Fact]
    public async Task A_run_resolved_to_the_wrong_workspace_reads_nothing_and_writes_nothing()
    {
        await UploadAsync(WorkspaceA, PdfBytes("House style", "Warm and plain."));

        await using var scope = _provider.CreateAsyncScope();
        var claim = await Claims(scope).ClaimNextAsync(
            Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

        // The claim is A's. This is what a tampered claim, or a worker that resolved the wrong workspace, would
        // amount to: the operation row is workspace-owned, so B simply cannot see it. Finding a row is not
        // authorization, and here there is not even a row to find.
        await using var wrong = ScopeFor(WorkspaceB);
        var outcome = await wrong.ServiceProvider.GetRequiredService<IBrandSourceExtractionFacade>()
            .ExecuteAsync(claim!.OperationId, claim.LeaseToken, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceExtractionRunOutcome.Skipped, outcome);
        Assert.Empty(await ExtractionsAsync());
        Assert.DoesNotContain(_store.Keys, key => key.Contains("/extracted/", StringComparison.Ordinal));
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

    private static BrandSourceExtractionClaimRepository Claims(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<BrandSourceExtractionClaimRepository>();

    private async Task<BrandSourceExtractionPassSummary> RunPassAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IBrandSourceExtractionWorker>()
            .RunPendingAsync(TestContext.Current.CancellationToken);
    }

    private async Task<BrandSourceExtractionMaintenanceSummary> RunMaintenanceAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IBrandSourceExtractionWorker>()
            .RunMaintenanceAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Seeded> UploadAsync(
        Guid workspaceId, byte[]? bytes = null, string fileName = "house-style.pdf", string? idempotencyKey = null)
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
            new BrandSourceUploadFile(
                new MemoryStream(bytes ?? BrandSourceSampleFiles.Pdf()), fileName),
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.True(uploaded.Result.Succeeded, uploaded.Result.Error?.Message);
        var document = uploaded.Result.Value!;

        return new Seeded(document.Id, document.CurrentVersion.Id);
    }

    private async Task<Seeded> ReplaceAsync(Guid workspaceId, Guid documentId, string token)
    {
        await using var scope = ScopeFor(workspaceId);

        var replaced = await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>().ReplaceAsync(
            "user",
            documentId,
            new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = token },
            new BrandSourceUploadFile(new MemoryStream(BrandSourceSampleFiles.Pdf("second")), "house-style.pdf"),
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.True(replaced.Result.Succeeded, replaced.Result.Error?.Message);

        return new Seeded(documentId, replaced.Result.Value!.CurrentVersion.Id);
    }

    /// <summary>Corrects a version's current text through the creator-facing seam, as a creator would.</summary>
    private async Task<BrandSourceExtractionServiceModel> CorrectAsync(
        Seeded seeded, Guid expectedExtractionId, string text, string reason = "Fixed a word.")
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
                    Reason = reason,
                },
                Guid.NewGuid().ToString("N"),
                TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        return outcome.Result.Value!;
    }

    private async Task<BrandSourceDocumentDetailServiceModel> DetailAsync(Guid workspaceId, Guid documentId)
    {
        await using var scope = ScopeFor(workspaceId);
        var detail = await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>()
            .GetAsync(documentId, TestContext.Current.CancellationToken);

        Assert.True(detail.Succeeded, detail.Error?.Message);

        return detail.Value!;
    }

    private async Task<string> TokenAsync(Guid workspaceId, Guid documentId) =>
        (await DetailAsync(workspaceId, documentId)).ConcurrencyToken;

    private async Task<List<BrandSourceExtractionOperation>> OperationsAsync(Guid? workspaceId = null)
    {
        await using var scope = ScopeFor(workspaceId ?? WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandSourceExtractionOperations
            .AsNoTracking()
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

    /// <summary>Changes the one queued operation directly, to set up a state a test cannot reach by waiting.</summary>
    private async Task MutateAsync(Action<BrandSourceExtractionOperation> change)
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operation = await db.BrandSourceExtractionOperations.SingleAsync(TestContext.Current.CancellationToken);

        change(operation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> TextForAsync(Seeded seeded)
    {
        var extraction = (await ExtractionsAsync())
            .Single(row => row.BrandSourceDocumentVersionId == seeded.VersionId);

        return await StoredTextAsync(extraction.ExtractedTextObjectKey!);
    }

    private async Task<string> StoredTextAsync(string objectKey)
    {
        await using var stored = await _store.OpenReadAsync(
            BrandSourceObjectKey.Container, objectKey, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        using var reader = new StreamReader(stored.Content, Encoding.UTF8);

        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A real PDF, written by the renderer this repository already ships.</summary>
    private static byte[] PdfBytes(string heading, string body)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(container => container.Page(page =>
            {
                page.Margin(36);
                page.Content().Column(column =>
                {
                    column.Item().Text(heading).FontSize(18);
                    column.Item().Text(body);
                });
            }))
            .GeneratePdf();
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    /// The in-memory store, with the ability to fail writes of extracted text alone.
    /// </summary>
    /// <remarks>
    /// Narrower than the store's own <c>Unavailable</c> switch on purpose. Turning the whole store off exercises
    /// the failure to <em>read</em> the source, which is a different branch: this one lets a parse succeed and
    /// then refuses to store what it produced, which is the only way to prove nothing is committed when the
    /// artifact cannot be written.
    /// </remarks>
    private sealed class SelectivelyUnavailableStore(IPrivateObjectStore inner, Func<bool> refuseExtractedWrites)
        : IPrivateObjectStore
    {
        public Task<ObjectWriteResult> PutAsync(
            string container, string key, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken)
        {
            if (refuseExtractedWrites() && key.Contains("/extracted/", StringComparison.Ordinal))
            {
                throw new ObjectStoreUnavailableException("The test store refuses extracted-text writes.");
            }

            return inner.PutAsync(container, key, content, mediaType, maxBytes, cancellationToken);
        }

        public Task<StoredObjectContent?> OpenReadAsync(string container, string key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(container, key, cancellationToken);

        public Task<bool> DeleteAsync(string container, string key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(container, key, cancellationToken);
    }
}
