using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// Brand-context provenance through the queue that stores it and the read that publishes it (11A.20): one row per
/// proposal, committed with the proposal, visible in the detail, and invisible across the workspace boundary.
/// </summary>
/// <remarks>
/// SQLite, like the rest of this module's tests, and what it cannot prove is the DDL: SQL Server refuses a table
/// with two cascade paths into it and SQLite does not enforce that rule, so the migration is validated separately
/// against the SQL Server fixture. Why the brand ids carry no foreign keys is recorded in
/// <c>AiProposalBrandContextConfiguration</c>.
/// </remarks>
public sealed class AiProposalBrandContextPersistenceTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Membership = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid GuideId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GuideVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PassageId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DocumentId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    public AiProposalBrandContextPersistenceTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddSingleton<IClock>(_clock)
            .AddAudit()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "a", CreatedAt = _clock.UtcNow },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "b", CreatedAt = _clock.UtcNow });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_grounded_proposal_commits_its_provenance_with_itself()
    {
        var operationId = await StoreAsync(WorkspaceA, Package());

        var loaded = await ReadAsync(WorkspaceA, operationId);
        var context = loaded.Proposal!.BrandContext;

        Assert.NotNull(context);
        Assert.Equal(WorkspaceA, context.WorkspaceId);
        Assert.Equal(GuideId, context.BrandGuideId);
        Assert.Equal(4, context.BrandGuideVersionNumber);
        Assert.True(context.GuideWasActiveVersion);
        Assert.Equal(7, context.BrandProfileRevision);

        var source = Assert.Single(context.Sources);

        Assert.Equal(DocumentId, source.BrandSourceDocumentId);
        Assert.Equal(PassageId, source.BrandSourcePassageId);
        Assert.Equal(3, source.DocumentVersionNumber);
        Assert.Equal(WorkspaceA, source.WorkspaceId);
    }

    /// <summary>
    /// A generation that asked for nothing records nothing, which is what keeps "I turned brand voice off"
    /// distinguishable from "it had nothing to give".
    /// </summary>
    [Fact]
    public async Task A_proposal_that_asked_for_no_brand_context_stores_no_row()
    {
        var operationId = await StoreAsync(WorkspaceA, brandContext: null);

        var loaded = await ReadAsync(WorkspaceA, operationId);

        Assert.Null(loaded.Proposal!.BrandContext);

        using var scope = _provider.CreateScope();

        Assert.Equal(
            0,
            await Db(scope, WorkspaceA).AiProposalBrandContexts
                .IgnoreQueryFilters()
                .CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The read the published detail is mapped from loads these rows, because a detail reporting "no brand
    /// context" for a generation that had one would be worse than one reporting nothing at all.
    /// </summary>
    [Fact]
    public async Task The_published_detail_names_the_guide_version_that_was_used()
    {
        var operationId = await StoreAsync(WorkspaceA, Package());

        var loaded = await ReadAsync(WorkspaceA, operationId);
        var described = AiOperationDescription.Describe(loaded.Operation, loaded.Proposal);
        var context = described.Proposal!.BrandContext;

        Assert.NotNull(context);
        Assert.Equal(GuideId, context.GuideId);
        Assert.Equal(GuideVersionId, context.GuideVersionId);
        Assert.Equal(4, context.GuideVersionNumber);
        Assert.True(context.GuideWasActiveVersion);
        Assert.Equal(7, context.BrandProfileRevision);
        Assert.Equal("weeknight cooks", context.Audience);
        Assert.Equal(BrandContextOrigin.Request, context.AudienceOrigin);
        Assert.Equal(1, context.GuidanceSectionCount);
        Assert.Equal(1, context.RuleCount);
        Assert.Equal(410, context.EstimatedTokens);

        var source = Assert.Single(context.Sources);

        Assert.Equal(DocumentId, source.DocumentId);
        Assert.Equal(PassageId, source.PassageId);
        Assert.Equal(12, source.Ordinal);
    }

    /// <summary>
    /// Neither the guide text nor the sample text is copied into this module. The checksum is what proves which
    /// words travelled, and it is published exactly as it was computed.
    /// </summary>
    [Fact]
    public async Task The_detail_publishes_the_checksum_it_was_given()
    {
        var package = Package();
        var operationId = await StoreAsync(WorkspaceA, package);

        var loaded = await ReadAsync(WorkspaceA, operationId);
        var described = AiOperationDescription.Describe(loaded.Operation, loaded.Proposal);

        Assert.Equal(package.Checksum, described.Proposal!.BrandContext!.Checksum);
    }

    /// <summary>
    /// Two workspaces grounded on the same guide id. Neither row is visible from the other side, and the query
    /// filter on the interior rows is what makes that true without either read naming a workspace.
    /// </summary>
    [Fact]
    public async Task Provenance_does_not_cross_the_workspace_boundary()
    {
        var inA = await StoreAsync(WorkspaceA, Package());
        var inB = await StoreAsync(WorkspaceB, Package());

        Assert.Null(await ReadOrNullAsync(WorkspaceB, inA));
        Assert.Null(await ReadOrNullAsync(WorkspaceA, inB));

        using var scope = _provider.CreateScope();
        var db = Db(scope, WorkspaceA);

        var visible = await db.AiProposalBrandContexts.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(visible);
        Assert.Equal(WorkspaceA, visible[0].WorkspaceId);
        Assert.Single(await db.AiProposalBrandSources.ToListAsync(TestContext.Current.CancellationToken));
    }

    // ---- harness ---------------------------------------------------------------------------------------

    /// <summary>Requests an operation, claims it, and stores a proposal carrying the given package.</summary>
    private async Task<Guid> StoreAsync(Guid workspaceId, BrandContextPackage? brandContext)
    {
        Guid operationId;

        using (var scope = _provider.CreateScope())
        {
            var requested = await DataLayer(scope, workspaceId).RequestAsync(
                new AiOperation
                {
                    WorkspaceId = workspaceId,
                    TaskType = AiTaskType.EditorialPackage,
                    Scope = AiOperationScope.Advisory,
                    Status = AiOperationStatus.Requested,
                    IdempotencyKey = $"key-{workspaceId}",
                    RequestedByMembershipId = Membership,
                    RequestedAt = _clock.UtcNow,
                    StatusChangedAt = _clock.UtcNow,
                    AvailableAt = _clock.UtcNow,
                },
                TestContext.Current.CancellationToken);

            operationId = requested.Operation!.Id;
        }

        AiOperationClaim claim;

        using (var scope = _provider.CreateScope())
        {
            claim = await scope.ServiceProvider.GetRequiredService<AiOperationClaimRepository>().ClaimNextAsync(
                Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("The queue should have had work to claim.");
        }

        var assembly = AiProposalAssembler.Assemble(
            workspaceId,
            claim.OperationId,
            pinnedVersionId: null,
            currentVersionId: null,
            new AiOutputDocument { SchemaVersion = "fixture.v1" },
            [],
            new AiProposalProvenance(
                "fixture.v1", "content.editorial-package", "1.0.0", "sha256:abc", "test-provider", "test-model", null),
            _clock.UtcNow,
            brandContext);

        Assert.True(assembly.Succeeded);

        using (var scope = _provider.CreateScope())
        {
            var outcome = await DataLayer(scope, workspaceId).StoreProposalAsync(
                claim.OperationId,
                claim.LeaseToken,
                assembly.Proposal!,
                [Attempt()],
                reservationId: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(AiOperationWriteOutcome.Applied, outcome);
        }

        return claim.OperationId;
    }

    private async Task<AiOperationWithProposal> ReadAsync(Guid workspaceId, Guid operationId) =>
        await ReadOrNullAsync(workspaceId, operationId)
            ?? throw new InvalidOperationException("The operation should have been readable in its own workspace.");

    private async Task<AiOperationWithProposal?> ReadOrNullAsync(Guid workspaceId, Guid operationId)
    {
        using var scope = _provider.CreateScope();

        return await DataLayer(scope, workspaceId).GetWithProposalAsync(
            operationId, TestContext.Current.CancellationToken);
    }

    private static IAiOperationDataLayer DataLayer(IServiceScope scope, Guid workspaceId)
    {
        Resolve(scope, workspaceId);

        return scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
    }

    private static CreatorPantryDbContext Db(IServiceScope scope, Guid workspaceId)
    {
        Resolve(scope, workspaceId);

        return scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
    }

    /// <summary>What a worker does before touching anything: resolve and validate the workspace it will serve.</summary>
    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            Membership,
            WorkspaceRole.Owner,
            "test-account");

    private AiAttemptRecord Attempt() => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = "test-model",
        PromptTemplateId = "content.editorial-package",
        PromptTemplateVersion = "1.0.0",
        StartedAt = _clock.UtcNow,
        CompletedAt = _clock.UtcNow,
        LatencyMilliseconds = 10,
        CorrelationId = Guid.NewGuid(),
    };

    private static BrandContextPackage Package() => new(
        AiTaskType.EditorialPackage,
        ChannelKey: null,
        "weeknight cooks",
        BrandContextOrigin.Request,
        new BrandContextProfile("Sam's Kitchen", null, "home cooks", "en-GB", [], 7),
        GuideId,
        GuideVersionId,
        4,
        GuideIsActiveVersion: true,
        [new BrandContextGuidance(BrandStyleGuideSectionKey.Voice, null, "Warm, never breezy.", BrandContextOrigin.GuideSection)],
        [new BrandContextRule(BrandStyleGuideRuleKind.Dont, "Never say moist.")],
        [new BrandContextExcerpt(PassageId, DocumentId, 3, 12, "Salt it the night before.")],
        [],
        [],
        410,
        "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Fixed, unlike the queue tests' movable one: nothing here turns on time passing, and a clock that can move
    /// would invite a lease expiry into a test about what a row records.
    /// </summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
