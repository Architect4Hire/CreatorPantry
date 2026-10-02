using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.18's acceptance against a real SQL Server, for the one thing SQLite cannot show: that the explicit
/// transaction the acceptance opens, with the brand module's own <c>SaveChanges</c> inside it, is accepted by
/// the retrying execution strategy a real connection uses.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AiBrandGuideAcceptanceTests"/> covers the contract — full and partial acceptance, rejection,
/// rewrites, staleness, replay, rollback, isolation — over SQLite, whose provider has no retrying strategy, so
/// a user-initiated transaction there is never checked against one. On SQL Server it is: a
/// <c>BeginTransaction</c> outside <c>CreateExecutionStrategy().ExecuteAsync</c> throws, and the two writes
/// landing in separate transactions would be the atomicity failure the whole seam exists to prevent.
/// </para>
/// <para>
/// There is <strong>no migration in this change</strong> — no new table, column or index — so the schema these
/// rows go into is already covered by <c>BrandGuideSqlServerTests</c>. What is new is a second version of a
/// guide being written by something other than creation, inside a transaction another module opened; that is
/// what these two cases are, and no more.
/// </para>
/// </remarks>
public sealed class AiBrandGuideAcceptanceSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddOutbox()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(new AiTaskOptions())
            .AddIdempotency(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build())
            .AddBrandModule()
            .AddAiUsageModule()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiBrandGuideAcceptanceBusiness, AiBrandGuideAcceptanceBusiness>()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.MigrateAsync();

        db.Workspaces.Add(new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// The whole point of the fixture: one transaction spanning two modules, on a provider that checks.
    /// </summary>
    [Fact]
    public async Task An_acceptance_commits_the_guide_version_and_the_decision_together()
    {
        var seeded = await SeedAsync();

        var result = await AcceptAsync(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = [seeded.ItemId],
            WasHelpful = true,
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(2, result.Value!.Written!.GuideVersionNumber);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var version = await db.BrandStyleGuideVersions.AsNoTracking()
            .Include(row => row.Sections)
            .SingleAsync(row => row.VersionNumber == 2, TestContext.Current.CancellationToken);

        Assert.Equal(seeded.GuideVersionId, version.ParentVersionId);
        Assert.Equal(
            "Warm, but never fussy.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Tone).Body);

        var operation = await db.AiOperations.AsNoTracking()
            .SingleAsync(row => row.Id == seeded.OperationId, TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationStatus.Accepted, operation.Status);

        var change = await db.AiStructuredChanges.AsNoTracking()
            .SingleAsync(row => row.Id == seeded.ItemId, TestContext.Current.CancellationToken);

        Assert.Equal(AiChangeDisposition.Accepted, change.Disposition);

        // Both audit entries, from both modules, in the one transaction.
        var actions = await db.AuditLogs.AsNoTracking()
            .Select(row => row.Action)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(BrandAuditActions.StyleGuideVersionCreatedFromProposal, actions);
        Assert.Contains(
            AiOperationTransitionPolicy.AuditAction(AiOperationStatus.Proposed, AiOperationStatus.Accepted),
            actions);
    }

    /// <summary>
    /// The rollback half, on the provider where a half-committed transaction would actually be possible: the
    /// guide's refusal has to take the decision and both audit entries down with it.
    /// </summary>
    [Fact]
    public async Task A_refusal_rolls_back_the_version_the_decision_and_the_audit_entries()
    {
        var seeded = await SeedAsync();

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var guide = await db.BrandStyleGuides.SingleAsync(
                row => row.Id == seeded.GuideId, TestContext.Current.CancellationToken);

            guide.Status = BrandStyleGuideStatus.Archived;
            guide.ArchivedAt = Now;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await AcceptAsync(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = [seeded.ItemId],
            WasHelpful = true,
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(BrandErrorCodes.GuideArchivedConflict, result.Error!.Code);

        await using var after = ScopeFor(WorkspaceA);
        var db2 = after.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Equal(
            1,
            await db2.BrandStyleGuideVersions.CountAsync(TestContext.Current.CancellationToken));

        var operation = await db2.AiOperations.AsNoTracking()
            .SingleAsync(row => row.Id == seeded.OperationId, TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationStatus.Proposed, operation.Status);

        Assert.Equal(0, await db2.AiProposalFeedback.CountAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            BrandAuditActions.StyleGuideVersionCreatedFromProposal,
            await db2.AuditLogs.AsNoTracking().Select(row => row.Action)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<Domain.Managers.Results.OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAsync(
        AcceptBrandGuideProposalViewModel model, Guid operationId)
    {
        await using var scope = ScopeFor(WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<IAiBrandGuideAcceptanceBusiness>()
            .AcceptAsync(UserId, operationId, model, TestContext.Current.CancellationToken);
    }

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        return scope;
    }

    private sealed record Seeded(Guid GuideId, Guid GuideVersionId, Guid OperationId, Guid ItemId);

    /// <summary>
    /// A guide with version 1 and a proposal holding one acceptable section. No source document: citations are
    /// the SQLite suite's subject, and a version citing nothing still exercises the transaction.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();

        var guide = new BrandStyleGuide
        {
            Id = Guid.NewGuid(),
            DisplayName = "House voice",
            Status = BrandStyleGuideStatus.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        };

        var version = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(),
            BrandStyleGuideId = guide.Id,
            VersionNumber = 1,
            CreatedByMembershipId = member,
            CreatedAt = Now,
            Sections =
            [
                new BrandStyleGuideSection
                {
                    Id = Guid.NewGuid(),
                    SectionKey = BrandStyleGuideSectionKey.Voice,
                    Body = "Plain and unhurried.",
                },
            ],
        };

        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var item = new AiStructuredChange
        {
            Id = Guid.NewGuid(),
            AiProposalId = proposalId,
            ChangeKind = AiChangeKind.Add,
            TargetKind = AiChangeTargetKind.BrandGuideSection,
            TargetId = targetId,
            AfterValue = "Warm, but never fussy.",
            ProposedPosition = 0,
            SortOrder = 0,
        };

        AiStructuredChange Set(string field, string value, int order) => new()
        {
            Id = Guid.NewGuid(),
            AiProposalId = proposalId,
            ChangeKind = AiChangeKind.Set,
            TargetKind = AiChangeTargetKind.BrandGuideSection,
            TargetId = targetId,
            FieldName = field,
            AfterValue = value,
            SortOrder = order,
        };

        db.BrandStyleGuides.Add(guide);
        db.BrandStyleGuideVersions.Add(version);

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            TaskType = AiTaskType.BrandGuideProposal,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            TaskInputsJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AiBrandGuideProposalInputs.GuideId] = guide.Id.ToString("D"),
                    [AiBrandGuideProposalInputs.GuideVersionNumber] = "1",
                    [AiBrandGuideProposalInputs.Dimensions] = "tone",
                }),
            IdempotencyKey = $"guide-proposal-{operationId}",
            RequestedByMembershipId = member,
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            AiOperationId = operationId,
            OutputSchemaVersion = "brand.guide-proposal.v1",
            PromptTemplateId = AiTaskCatalog.BrandGuideProposal,
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
        });

        db.AiStructuredChanges.AddRange(
            item,
            Set(AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section, 1),
            Set(AiBrandGuideFields.Dimension, "tone", 2),
            Set(AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Questionnaire.ToString(), 3));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new Seeded(guide.Id, version.Id, operationId, item.Id);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
