using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// 11A.17's request seam over a real SQLite-backed data layer and the real brand module: the enabled gate, the
/// guide check, the channel check, what is pinned into the operation, and two-workspace isolation.
/// </summary>
/// <remarks>
/// What this capability alone has, and what most of these cover: it names no recipe, it pins a <em>guide</em>
/// version rather than a recipe version, and it resolves each selected document to that document's current
/// version so the proposal records the exact text it was grounded in.
/// </remarks>
public sealed class AiBrandGuideProposalRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly AiTaskOptions _tasks = new();

    public AiBrandGuideProposalRequestBusinessTests()
    {
        _connection.Open();
        _tasks.Enabled.Add(AiTaskCatalog.BrandGuideProposal);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddBrandModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(_tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiBrandGuideProposalRequestBusiness, AiBrandGuideProposalRequestBusiness>()
            .AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
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

    [Fact]
    public async Task A_request_is_queued_with_no_recipe_and_a_scope_the_client_did_not_choose()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = guideId });

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        var operation = await OperationAsync(WorkspaceA);
        Assert.Equal(AiTaskType.BrandGuideProposal, operation.TaskType);

        // Neither is a brand guide's, so both stay null -- and the scope says why nothing can be applied.
        Assert.Null(operation.RecipeId);
        Assert.Null(operation.RecipeVersionId);
        Assert.Equal(AiOperationScope.NotApplicable, operation.Scope);
        Assert.Empty(AiPolicy.AllowedTargets(operation.Scope));
    }

    [Fact]
    public async Task The_guides_working_version_is_pinned_so_a_later_edit_cannot_re_explain_the_answer()
    {
        var guideId = await SeedGuideAsync(WorkspaceA, versionNumber: 3);

        await RequestAsync(WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = guideId });

        Assert.Equal("3", await InputAsync(WorkspaceA, AiBrandGuideProposalInputs.GuideVersionNumber));
    }

    [Fact]
    public async Task Each_selected_document_is_pinned_to_its_current_version()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var document = await SeedDocumentAsync(WorkspaceA, currentVersionNumber: 2);

        await RequestAsync(
            WorkspaceA,
            new RequestBrandGuideProposalViewModel { GuideId = guideId, SourceDocumentIds = [document] });

        // The request named a document; the server decided which version of it, and recorded that.
        Assert.Equal(
            $"{document:N}:2",
            await InputAsync(WorkspaceA, AiBrandGuideProposalInputs.SourceVersions));
    }

    [Fact]
    public async Task Every_dimension_but_channel_is_asked_for_when_none_is_named()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = guideId });

        var dimensions = (await InputAsync(WorkspaceA, AiBrandGuideProposalInputs.Dimensions))!.Split(',');

        // Channel is left out because it needs channel keys to mean anything; including it by default would turn
        // an empty request into a refusal about a field the caller never mentioned.
        Assert.DoesNotContain("channel", dimensions);
        Assert.Equal(AiBrandGuideDimensionCatalog.Names.Count - 1, dimensions.Length);
    }

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_written()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        _tasks.Enabled.Clear();

        var outcome = await RequestAsync(WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = guideId });

        Assert.Equal(AiBrandGuideProposalRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
        await AssertNothingQueuedAsync(WorkspaceA);
    }

    [Fact]
    public async Task An_unknown_guide_is_refused()
    {
        await SeedGuideAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = Guid.NewGuid() });

        Assert.Equal(AiBrandGuideProposalRequestErrors.GuideNotFound, outcome.Result.Error!.Code);
        await AssertNothingQueuedAsync(WorkspaceA);
    }

    [Fact]
    public async Task A_source_document_that_does_not_resolve_refuses_the_whole_request()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var real = await SeedDocumentAsync(WorkspaceA);

        // Dropping the one that failed would ground the proposal on less than the creator chose while the stored
        // inputs claimed otherwise, and they would have no way to tell.
        var outcome = await RequestAsync(
            WorkspaceA,
            new RequestBrandGuideProposalViewModel { GuideId = guideId, SourceDocumentIds = [real, Guid.NewGuid()] });

        Assert.Equal(AiBrandGuideProposalRequestErrors.SourceUnprocessable, outcome.Result.Error!.Code);
        await AssertNothingQueuedAsync(WorkspaceA);
    }

    [Fact]
    public async Task Channel_guidance_without_a_channel_is_refused_and_channels_without_it_too()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);

        var noKeys = await RequestAsync(
            WorkspaceA,
            new RequestBrandGuideProposalViewModel { GuideId = guideId, Dimensions = ["channel"] });
        Assert.Equal(AiBrandGuideProposalRequestErrors.ChannelInvalid, noKeys.Result.Error!.Code);

        var unknownKey = await RequestAsync(
            WorkspaceA,
            new RequestBrandGuideProposalViewModel
            {
                GuideId = guideId,
                Dimensions = ["channel"],
                ChannelKeys = ["not-a-channel"],
            });
        Assert.Equal(AiBrandGuideProposalRequestErrors.ChannelInvalid, unknownKey.Result.Error!.Code);

        // Keys with no channel dimension to apply to would be silently ignored, which is worse than refused.
        var strayKeys = await RequestAsync(
            WorkspaceA,
            new RequestBrandGuideProposalViewModel
            {
                GuideId = guideId,
                Dimensions = ["voice"],
                ChannelKeys = ["instagram"],
            });
        Assert.Equal(AiBrandGuideProposalRequestErrors.ChannelInvalid, strayKeys.Result.Error!.Code);

        await AssertNothingQueuedAsync(WorkspaceA);
    }

    [Fact]
    public async Task One_key_replayed_returns_the_first_request_rather_than_queueing_a_second()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var model = new RequestBrandGuideProposalViewModel { GuideId = guideId };

        var first = await RequestAsync(WorkspaceA, model, key: "key-1");
        var second = await RequestAsync(WorkspaceA, model, key: "key-1");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(
            first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);

        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal(1, await db.AiOperations.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_workspace_cannot_request_a_proposal_about_the_others_guide()
    {
        var mine = await SeedGuideAsync(WorkspaceA);
        var theirs = await SeedGuideAsync(WorkspaceB);

        var outcome = await RequestAsync(
            WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = theirs });

        // The same answer a guide that was never created gets.
        Assert.Equal(AiBrandGuideProposalRequestErrors.GuideNotFound, outcome.Result.Error!.Code);
        await AssertNothingQueuedAsync(WorkspaceA);

        // And A's own guide still works, so this was isolation rather than a broken seam.
        Assert.True((await RequestAsync(WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = mine }))
            .Result.Succeeded);
    }

    [Fact]
    public async Task One_workspace_cannot_ground_a_proposal_in_the_others_document()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var theirs = await SeedDocumentAsync(WorkspaceB);

        var outcome = await RequestAsync(
            WorkspaceA,
            new RequestBrandGuideProposalViewModel { GuideId = guideId, SourceDocumentIds = [theirs] });

        // Refused as unusable, with the same code an id that was never issued gets: a caller must not be able to
        // learn that a document exists somewhere it cannot see it.
        Assert.Equal(AiBrandGuideProposalRequestErrors.SourceUnprocessable, outcome.Result.Error!.Code);
        await AssertNothingQueuedAsync(WorkspaceA);
    }

    [Fact]
    public async Task One_idempotency_key_does_not_carry_across_workspaces()
    {
        var mine = await SeedGuideAsync(WorkspaceA);
        var theirs = await SeedGuideAsync(WorkspaceB);

        // The operation's key is unique per (workspace, key), so the same key in two workspaces is two requests
        // rather than one replay. A caller in B must never be handed the operation A created.
        var inA = await RequestAsync(
            WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = mine }, key: "shared-key");
        var inB = await RequestAsync(
            WorkspaceB, new RequestBrandGuideProposalViewModel { GuideId = theirs }, key: "shared-key");

        Assert.True(inA.Result.Succeeded, inA.Result.Error?.Message);
        Assert.True(inB.Result.Succeeded, inB.Result.Error?.Message);
        Assert.False(inB.Replayed);
        Assert.NotEqual(inA.Result.Value!.AiProposalRequestId, inB.Result.Value!.AiProposalRequestId);

        // One operation visible in each, not two in either.
        Assert.Equal(mine, await GuideInputAsync(WorkspaceA));
        Assert.Equal(theirs, await GuideInputAsync(WorkspaceB));
    }

    [Fact]
    public async Task A_request_from_the_other_workspace_is_not_visible_through_the_read()
    {
        var guideId = await SeedGuideAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, new RequestBrandGuideProposalViewModel { GuideId = guideId });
        var requestId = queued.Result.Value!.AiProposalRequestId;

        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceB);
        var business = scope.ServiceProvider.GetRequiredService<IAiBrandGuideProposalRequestBusiness>();

        var read = await business.GetAsync(requestId, TestContext.Current.CancellationToken);

        Assert.False(read.Succeeded);
        Assert.Equal(AiBrandGuideProposalRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- Harness ----

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId, RequestBrandGuideProposalViewModel model, string? key = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiBrandGuideProposalRequestBusiness>()
            .RequestAsync(model, key ?? Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
    }

    private async Task<Domain.Modules.Ai.Data.Entities.AiOperation> OperationAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The guide the one operation visible in this workspace names. Asserts there is exactly one.</summary>
    private async Task<Guid> GuideInputAsync(Guid workspaceId) =>
        Guid.Parse((await InputAsync(workspaceId, AiBrandGuideProposalInputs.GuideId))!);

    private async Task<string?> InputAsync(Guid workspaceId, string key)
    {
        var operation = await OperationAsync(workspaceId);
        var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;

        return inputs.GetValueOrDefault(key);
    }

    private async Task AssertNothingQueuedAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Empty(await db.AiOperations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    private async Task<Guid> SeedGuideAsync(Guid workspaceId, int versionNumber = 1)
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

        // Every version up to the working one, because "working" means the highest number there is.
        for (var number = 1; number <= versionNumber; number++)
        {
            var version = new BrandStyleGuideVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandStyleGuideId = guide.Id,
                VersionNumber = number,
                CreatedByMembershipId = member,
                CreatedAt = Now,
            };
            version.Sections.Add(new BrandStyleGuideSection
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandStyleGuideVersionId = version.Id,
                SectionKey = BrandStyleGuideSectionKey.Voice,
                ChannelKey = string.Empty,
                Body = $"Warm, revision {number}.",
            });

            db.BrandStyleGuideVersions.Add(version);
        }

        db.BrandStyleGuides.Add(guide);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return guide.Id;
    }

    private async Task<Guid> SeedDocumentAsync(Guid workspaceId, int currentVersionNumber = 1)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();

        var document = new BrandSourceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = "House style",
            DocumentType = BrandSourceDocumentType.StyleGuide,
            Purpose = BrandSourcePurpose.Voice,
            CurrentVersionNumber = currentVersionNumber,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        };
        db.BrandSourceDocuments.Add(document);

        for (var number = 1; number <= currentVersionNumber; number++)
        {
            db.BrandSourceDocumentVersions.Add(new BrandSourceDocumentVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandSourceDocumentId = document.Id,
                VersionNumber = number,
                MediaType = "application/pdf",
                SizeBytes = 1024,
                ContentChecksum = Checksum,
                OriginalFileName = "house-style.pdf",
                ObjectKey = $"brand-sources/{document.Id:N}/{number}",
                CreatedByMembershipId = member,
                CreatedAt = Now,
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document.Id;
    }

    private static void Resolve(AsyncServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "acct");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
