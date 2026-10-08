using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.24's request seam and its read, over the real AI operation store and the real brand module.
/// </summary>
/// <remarks>
/// The handler's own tests cover what the two provider calls do. These cover what happens before one and after
/// both: which requests are refused and when, what is pinned into the stored inputs, and how six stored rows
/// become the three pairs a screen reads.
/// </remarks>
public sealed class AiBrandStyleTestDriveBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly AiTaskOptions _tasks = new();

    public AiBrandStyleTestDriveBusinessTests()
    {
        _connection.Open();
        _tasks.Enabled.Add(AiTaskCatalog.BrandStyleTestDrive);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A real key: the options validator refuses anything shorter, and a facade that could not be
                // constructed would fail every test here for a reason none of them is about.
                ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddApplicationTime()
            .AddContentChannelCatalog()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(_tasks)
            .AddBrandModule()

            // The brand profile facade asks the Media module whether a submitted logo is in this workspace's
            // library (12.10k), so that lookup has to be resolvable wherever the brand module is composed.
            .AddMediaModule()
            .AddScoped<IBrandContextAssembler, BrandContextAssembler>()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiBrandStyleTestDriveBusiness, AiBrandStyleTestDriveBusiness>()
            .AddAiUsageModule()
            .AddIdempotency(configuration)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- what is refused before anything is queued ----

    /// <summary>
    /// Two generations are not free, so the capability ships dark. Being deployed is not being switched on, and
    /// a refused request writes no operation at all.
    /// </summary>
    [Fact]
    public async Task A_deployment_that_has_not_enabled_the_task_refuses_and_queues_nothing()
    {
        _tasks.Enabled.Clear();
        var guideId = await SeedGuideAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, guideId);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiBrandStyleTestDriveRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
        Assert.Equal(0, await OperationCountAsync());
    }

    /// <summary>
    /// A version with nothing written in it would produce two identical columns. Refused at the edge, so the
    /// creator is told synchronously rather than paying for two calls to find out.
    /// </summary>
    [Fact]
    public async Task A_version_with_nothing_written_is_refused_before_anything_is_queued()
    {
        var guideId = await SeedGuideAsync(WorkspaceA, sections: false);

        var outcome = await RequestAsync(WorkspaceA, guideId);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiBrandStyleTestDriveRequestErrors.GuideHasNoGuidance, outcome.Result.Error!.Code);
        Assert.Equal(0, await OperationCountAsync());
    }

    /// <summary>A guide that was never created and one belonging to a neighbour answer identically.</summary>
    [Fact]
    public async Task A_guide_in_another_workspace_is_answered_exactly_as_one_that_does_not_exist()
    {
        var theirs = await SeedGuideAsync(WorkspaceB);

        var crossing = await RequestAsync(WorkspaceA, theirs);
        var missing = await RequestAsync(WorkspaceA, Guid.NewGuid());

        Assert.Equal(AiBrandStyleTestDriveRequestErrors.GuideNotFound, crossing.Result.Error!.Code);
        Assert.Equal(crossing.Result.Error.Code, missing.Result.Error!.Code);
        Assert.Equal(crossing.Result.Error.Message, missing.Result.Error.Message);
        Assert.Equal(0, await OperationCountAsync());
    }

    /// <summary>A version number the guide does not have is the same answer: it is not a version to test.</summary>
    [Fact]
    public async Task A_version_the_guide_does_not_have_is_not_found()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, guideId, versionNumber: 7);

        Assert.Equal(AiBrandStyleTestDriveRequestErrors.GuideNotFound, outcome.Result.Error!.Code);
    }

    // ---- what is queued ----

    /// <summary>
    /// The row says what the operation is: a brand guide's, with no recipe and a scope that permits no change.
    /// Both are fixed server-side, so a row cannot say something untrue about what it will do.
    /// </summary>
    [Fact]
    public async Task An_accepted_request_queues_one_operation_naming_no_recipe()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, guideId);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        Assert.False(outcome.Replayed);

        var operation = await SingleOperationAsync();
        Assert.Equal(AiTaskType.BrandStyleTestDrive, operation.TaskType);
        Assert.Equal(AiOperationScope.NotApplicable, operation.Scope);
        Assert.Null(operation.RecipeId);
        Assert.Null(operation.RecipeVersionId);
        Assert.Equal(AiOperationStatus.Requested, operation.Status);

        // Nothing is comparable yet, and the reply says so rather than publishing an empty comparison.
        Assert.Null(outcome.Result.Value!.Comparison);
    }

    /// <summary>
    /// The guide and version are pinned into the inputs, so the worker grounds on the version the creator
    /// picked rather than on whatever is active by the time it runs.
    /// </summary>
    [Fact]
    public async Task The_guide_and_version_are_pinned_into_the_stored_inputs()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, guideId);

        var inputs = Inputs(await SingleOperationAsync());

        Assert.Equal(guideId.ToString("D"), inputs[BrandContextRequestInputs.BrandGuideId]);
        Assert.Equal("1", inputs[BrandContextRequestInputs.BrandGuideVersionNumber]);
    }

    /// <summary>
    /// The subject is written only when the creator named one, so the stored inputs record their choice rather
    /// than the default the product happened to carry on the day they asked.
    /// </summary>
    [Fact]
    public async Task The_subject_is_stored_only_when_the_creator_named_one()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, guideId);
        var defaulted = Inputs(await SingleOperationAsync());

        Assert.DoesNotContain(BrandStyleTestDriveInputs.Subject, defaulted.Keys);
        Assert.Equal(
            BrandStyleTestDriveSubject.Default,
            BrandStyleTestDriveInputs.ReadSubject(defaulted));

        var second = await RequestAsync(WorkspaceA, guideId, subject: "  a brown-butter plum cake  ");
        var named = Inputs(await OperationAsync(second.Result.Value!.RequestId));

        Assert.Equal("a brown-butter plum cake", named[BrandStyleTestDriveInputs.Subject]);
    }

    // ---- idempotency ----

    /// <summary>
    /// A test drive is two generations, so a retried request returns the first answer rather than buying a
    /// second pair.
    /// </summary>
    [Fact]
    public async Task The_same_key_returns_the_first_request_rather_than_queueing_a_second()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var key = Guid.NewGuid().ToString("N");

        var first = await RequestAsync(WorkspaceA, guideId, idempotencyKey: key);
        var again = await RequestAsync(WorkspaceA, guideId, idempotencyKey: key);

        Assert.True(again.Replayed);
        Assert.Equal(first.Result.Value!.RequestId, again.Result.Value!.RequestId);
        Assert.Equal(1, await OperationCountAsync());
    }

    [Fact]
    public async Task The_same_key_with_a_different_subject_is_refused()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var key = Guid.NewGuid().ToString("N");

        await RequestAsync(WorkspaceA, guideId, idempotencyKey: key);
        var different = await RequestAsync(WorkspaceA, guideId, subject: "something else", idempotencyKey: key);

        Assert.False(different.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, different.Result.Error!.Code);
        Assert.Equal(1, await OperationCountAsync());
    }

    // ---- reading it back ----

    /// <summary>Three conditions, one absence: no such request, another workspace's, and another task's.</summary>
    [Fact]
    public async Task An_unknown_request_another_workspaces_and_another_tasks_all_read_as_absent()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var mine = await RequestAsync(WorkspaceA, guideId);
        var requestId = mine.Result.Value!.RequestId;

        var unknown = await GetAsync(WorkspaceA, Guid.NewGuid());
        var crossing = await GetAsync(WorkspaceB, requestId);

        Assert.Equal(AiBrandStyleTestDriveRequestErrors.RequestNotFound, unknown.Error!.Code);
        Assert.Equal(unknown.Error.Code, crossing.Error!.Code);
        Assert.Equal(unknown.Error.Message, crossing.Error.Message);
    }

    /// <summary>
    /// The six stored rows read back as three pairs, each naming its sample, with the guide version that
    /// produced the right-hand column and the rules it asks for in the creator's own words.
    /// </summary>
    [Fact]
    public async Task Six_stored_rows_read_back_as_three_pairs_with_the_rules_that_applied()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var requestId = await SeedCompletedAsync(WorkspaceA, guideId);

        var result = await GetAsync(WorkspaceA, requestId);

        Assert.True(result.Succeeded, result.Error?.Message);
        var comparison = result.Value!.Comparison!;

        Assert.Equal(["blogIntro", "socialCaption", "imagePrompt"], comparison.Samples.Select(pair => pair.Sample));
        Assert.All(comparison.Samples, pair =>
        {
            Assert.StartsWith("plain", pair.WithoutGuide, StringComparison.Ordinal);
            Assert.StartsWith("guided", pair.WithGuide, StringComparison.Ordinal);
        });

        Assert.Equal(guideId, comparison.GuideId);
        Assert.Equal(1, comparison.GuideVersionNumber);
        Assert.Equal(BrandStyleTestDriveSubject.Default, comparison.Subject);

        // Named, not asserted: the screen says which of the creator's own rules produced the right-hand column.
        var voice = Assert.Single(comparison.AppliedRules, rule => rule.Label == "Voice");
        Assert.Equal("Warm, direct, never fussy.", voice.Summary);
    }

    /// <summary>
    /// A caution the model attached to one sample reads against that sample and says which column it is about;
    /// a caution about the grounding reads against the whole comparison.
    /// </summary>
    [Fact]
    public async Task A_note_names_its_column_and_a_grounding_notice_names_no_sample()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var requestId = await SeedCompletedAsync(WorkspaceA, guideId, withNotes: true);

        var comparison = (await GetAsync(WorkspaceA, requestId)).Value!.Comparison!;

        var blog = comparison.Samples.Single(pair => pair.Sample == "blogIntro");
        var note = Assert.Single(blog.Notes);

        Assert.True(note.WithGuide);
        Assert.Equal(AiWarningKind.Assumption, note.Kind);

        var notice = Assert.Single(comparison.Notices);
        Assert.Contains("thin", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(notice.ChangeId);
    }

    /// <summary>
    /// Why re-deriving the rules is safe at all: a guide version's sections are immutable, so the wording named
    /// beside the samples is the wording that produced them. The guide is edited by writing a new version, and
    /// the proposal names the one it used.
    /// </summary>
    [Fact]
    public async Task A_guide_versions_own_wording_cannot_change_under_the_samples()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var requestId = await SeedCompletedAsync(WorkspaceA, guideId);

        var comparison = (await GetAsync(WorkspaceA, requestId)).Value!.Comparison!;
        Assert.False(comparison.GroundingChangedSince);

        var rewriting = await Record.ExceptionAsync(
            () => RewordGuideAsync(WorkspaceA, guideId, "Dry, exact, a little stern."));

        Assert.IsType<InvalidOperationException>(rewriting);
        Assert.Contains("immutable", rewriting.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What <em>can</em> move is the rest of the grounding — a new example in the library changes what the
    /// guided call would be sent now. Reported rather than hidden, and the samples are untouched: they are what
    /// the model wrote.
    /// </summary>
    [Fact]
    public async Task A_new_example_in_the_library_changes_the_grounding_and_is_reported()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var requestId = await SeedCompletedAsync(WorkspaceA, guideId);

        var before = (await GetAsync(WorkspaceA, requestId)).Value!.Comparison!;
        Assert.False(before.GroundingChangedSince);

        await SeedExampleAsync(WorkspaceA);

        var after = (await GetAsync(WorkspaceA, requestId)).Value!.Comparison!;

        Assert.True(after.GroundingChangedSince);
        Assert.Equal(
            before.Samples.Select(pair => pair.WithGuide),
            after.Samples.Select(pair => pair.WithGuide));
    }

    // ---- harness ----

    private async Task<IdempotentOutcome<BrandStyleTestDriveServiceModel>> RequestAsync(
        Guid workspaceId,
        Guid guideId,
        int versionNumber = 1,
        string? subject = null,
        string? idempotencyKey = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiBrandStyleTestDriveBusiness>().RequestAsync(
            new RequestBrandStyleTestDriveViewModel
            {
                GuideId = guideId,
                VersionNumber = versionNumber,
                Subject = subject,
            },
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
    }

    private async Task<OperationResult<BrandStyleTestDriveServiceModel>> GetAsync(Guid workspaceId, Guid requestId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiBrandStyleTestDriveBusiness>()
            .GetAsync(requestId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An operation and the proposal a successful run would have stored, seeded directly.
    /// </summary>
    /// <remarks>
    /// Direct rather than through the worker: this is a read test, and driving a claim, a lease, an admission
    /// and a settlement to arrange six rows would be testing the lifecycle those have their own tests for.
    /// </remarks>
    private async Task<Guid> SeedCompletedAsync(Guid workspaceId, Guid guideId, bool withNotes = false)
    {
        var requested = await RequestAsync(workspaceId, guideId);
        var requestId = requested.Result.Value!.RequestId;

        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var versionId = await db.BrandStyleGuideVersions
            .Where(version => version.BrandStyleGuideId == guideId && version.VersionNumber == 1)
            .Select(version => version.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var operation = await db.AiOperations.SingleAsync(
            row => row.Id == requestId, TestContext.Current.CancellationToken);
        operation.Status = AiOperationStatus.Proposed;
        operation.StatusChangedAt = Now;

        var proposal = new AiProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            AiOperationId = requestId,
            OutputSchemaVersion = "brand.style-test-drive.v1",
            PromptTemplateId = AiTaskCatalog.BrandStyleTestDrive,
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:0",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
            BrandContext = new AiProposalBrandContext
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandGuideId = guideId,
                BrandGuideVersionId = versionId,
                BrandGuideVersionNumber = 1,
                GuideWasActiveVersion = false,
                Checksum = await ChecksumAsync(workspaceId, guideId),
                EstimatedTokens = 10,
                GuidanceSectionCount = 1,
                RuleCount = 0,
                AssembledAt = Now,
            },
        };

        var order = 0;

        foreach (var sample in AiBrandStyleSampleCatalog.All)
        {
            foreach (var guided in (bool[])[false, true])
            {
                proposal.Changes.Add(new AiStructuredChange
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    ChangeKind = AiChangeKind.Set,
                    TargetKind = guided
                        ? AiChangeTargetKind.BrandStyleSampleWithGuide
                        : AiChangeTargetKind.BrandStyleSampleWithoutGuide,
                    FieldName = AiBrandStyleSampleCatalog.ToWire(sample),
                    AfterValue = $"{(guided ? "guided" : "plain")}: a sample of {sample}.",
                    SortOrder = order++,
                    Disposition = AiChangeDisposition.Pending,
                });
            }
        }

        if (withNotes)
        {
            var guidedBlog = proposal.Changes.Single(change =>
                change.FieldName == "blogIntro"
                && change.TargetKind is AiChangeTargetKind.BrandStyleSampleWithGuide);

            proposal.Warnings.Add(new AiWarning
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Kind = AiWarningKind.Assumption,
                Message = "Assumed a bone-in thigh, which the subject did not say.",
                AiStructuredChangeId = guidedBlog.Id,
                SortOrder = 0,
            });

            proposal.Warnings.Add(new AiWarning
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Kind = AiWarningKind.Limitation,
                Message = "[brand_context.sparse_evidence] Your examples are thin, so this leans on your answers.",
                AiStructuredChangeId = null,
                SortOrder = 1,
            });
        }

        db.AiProposals.Add(proposal);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return requestId;
    }

    /// <summary>The checksum the assembler computes now, so a seeded proposal starts in step with the guide.</summary>
    private async Task<string> ChecksumAsync(Guid workspaceId, Guid guideId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);

        var assembled = await scope.ServiceProvider.GetRequiredService<IBrandContextAssembler>().AssembleAsync(
            new BrandContextRequest(
                AiTaskType.BrandStyleTestDrive, Guide: new BrandGuideSelection(guideId, 1)),
            TestContext.Current.CancellationToken);

        return assembled.Value!.Checksum;
    }

    private async Task<Guid> SeedGuideAsync(
        Guid workspaceId, bool sections = true, string body = "Warm, direct, never fussy.")
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();

        var guide = new BrandStyleGuide
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            DisplayName = "House voice",
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        };

        var version = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandStyleGuideId = guide.Id,
            VersionNumber = 1,
            CreatedByMembershipId = member,
            CreatedAt = Now,
        };

        if (sections)
        {
            version.Sections.Add(new BrandStyleGuideSection
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandStyleGuideVersionId = version.Id,
                SectionKey = BrandStyleGuideSectionKey.Voice,
                ChannelKey = string.Empty,
                Body = body,
            });
        }

        db.BrandStyleGuides.Add(guide);
        db.BrandStyleGuideVersions.Add(version);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return guide.Id;
    }

    /// <summary>
    /// Attempts to rewrite the version's one section in place, which the store refuses. Kept as a helper
    /// because the refusal is the thing being asserted.
    /// </summary>
    private async Task RewordGuideAsync(Guid workspaceId, Guid guideId, string body)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var section = await db.BrandStyleGuideSections
            .Where(row => db.BrandStyleGuideVersions
                .Any(version => version.Id == row.BrandStyleGuideVersionId
                    && version.BrandStyleGuideId == guideId))
            .SingleAsync(TestContext.Current.CancellationToken);

        section.Body = body;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// One example in the library, with indexed passages, so the assembler has something new to pick up.
    /// </summary>
    private async Task SeedExampleAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();
        const string checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

        var document = new BrandSourceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = "An older post",
            DocumentType = BrandSourceDocumentType.PublishedPost,
            Purpose = BrandSourcePurpose.Voice,
            CurrentVersionNumber = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        };

        var version = new BrandSourceDocumentVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = document.Id,
            VersionNumber = 1,
            MediaType = "text/plain",
            SizeBytes = 512,
            ContentChecksum = checksum,
            OriginalFileName = "older-post.txt",
            ObjectKey = $"brand-sources/{document.Id:N}/1",
            CreatedByMembershipId = member,
            CreatedAt = Now,
        };

        var extraction = new BrandSourceExtraction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentVersionId = version.Id,
            Ordinal = 1,
            Status = BrandSourceExtractionStatus.Succeeded,
            Origin = BrandSourceExtractionOrigin.Extracted,
            ExtractedTextObjectKey = $"brand-sources/text/{version.Id:N}/1",
            ContentChecksum = checksum,
            CreatedAt = Now,
        };

        var set = new BrandSourceChunkSet
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = document.Id,
            BrandSourceDocumentVersionId = version.Id,
            BrandSourceExtractionId = extraction.Id,
            SourceStatus = BrandSourceExtractionStatus.Succeeded,
            Status = BrandSourceChunkSetStatus.Current,
            ChunkerId = "text/paragraph-1600c-200o@1",
            EmbeddingModel = "text-embedding-3-small",
            EmbeddingDimension = BrandPolicy.EmbeddingDimension,
            ChunkCount = 1,
            CreatedAt = Now,
            EmbeddedAt = Now,
        };

        var embedding = new float[BrandPolicy.EmbeddingDimension];
        embedding[0] = 1f;

        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(version);
        db.BrandSourceExtractions.Add(extraction);
        db.BrandSourceChunkSets.Add(set);
        db.BrandSourceChunks.Add(new BrandSourceChunk
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceChunkSetId = set.Id,
            Ordinal = 1,
            StartByteOffset = 0,
            ByteLength = 512,
            ContentChecksum = checksum,
            Text = "Steady, unhurried, a little dry. The pan does the work.",
            Embedding = new SqlVector<float>(embedding),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> OperationCountAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiOperations.CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AiOperation> SingleOperationAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiOperations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// One named operation. By id rather than "the latest": the clock is stopped, so two requests share a
    /// timestamp and ordering by it would pick either one.
    /// </summary>
    private async Task<AiOperation> OperationAsync(Guid requestId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiOperations.AsNoTracking()
            .SingleAsync(operation => operation.Id == requestId, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Inputs(AiOperation operation) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;

    private static void Resolve(AsyncServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "acct");

    /// <summary>A fixed clock, so a stored timestamp is an equality assertion rather than a range.</summary>
    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
