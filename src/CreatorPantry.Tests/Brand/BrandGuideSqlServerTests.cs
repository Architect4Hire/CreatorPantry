using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// Brand source documents and style guides against a real SQL Server, for what SQLite cannot show: that the
/// server accepts the migration's cascade layout and check constraints, and that erasing a workspace removes
/// every row even though history is held by restricting foreign keys.
/// </summary>
public sealed class BrandGuideSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.MigrateAsync();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
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

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == WorkspaceA ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        return scope;
    }

    private sealed record Seeded(Guid DocumentId, Guid SourceVersionId, Guid GuideId, Guid DraftVersionId, Guid ApprovedVersionId);

    /// <summary>One row in every one of the twelve tables, plus a second, unapproved guide version.</summary>
    private async Task<Seeded> SeedEverythingAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();

        var document = new BrandSourceDocument
        {
            Id = Guid.NewGuid(), Title = "House style", DocumentType = BrandSourceDocumentType.StyleGuide,
            Purpose = BrandSourcePurpose.Voice, CreatedAt = Now, UpdatedAt = Now,
            CreatedByMembershipId = member, UpdatedByMembershipId = member,
        };
        var source = new BrandSourceDocumentVersion
        {
            Id = Guid.NewGuid(), BrandSourceDocumentId = document.Id, VersionNumber = 1, MediaType = "application/pdf",
            SizeBytes = 1024, ContentChecksum = "sha256:" + new string('0', 64), OriginalFileName = "house-style.pdf",
            ObjectKey = $"brand-sources/{document.Id:N}/1", CreatedByMembershipId = member, CreatedAt = Now,
        };
        var extraction = new BrandSourceExtraction
        {
            Id = Guid.NewGuid(), BrandSourceDocumentVersionId = source.Id, Ordinal = 1,
            Status = BrandSourceExtractionStatus.Succeeded, Origin = BrandSourceExtractionOrigin.Extracted,
            ExtractedTextObjectKey = $"brand-sources/text/{source.Id:N}/1", ContentChecksum = "sha256:" + new string('1', 64), CreatedAt = Now,
        };
        var tag = new BrandSourceTag { Id = Guid.NewGuid(), Name = "Launch", NormalizedName = "launch", CreatedAt = Now };
        document.Tags.Add(new BrandSourceDocumentTag { BrandSourceDocumentId = document.Id, BrandSourceTagId = tag.Id });

        var guide = new BrandStyleGuide
        {
            Id = Guid.NewGuid(), DisplayName = "Everyday voice", CreatedAt = Now, UpdatedAt = Now,
            CreatedByMembershipId = member, UpdatedByMembershipId = member,
        };
        var approved = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(), BrandStyleGuideId = guide.Id, VersionNumber = 1, CreatedByMembershipId = member, CreatedAt = Now,
        };
        approved.Sections.Add(new BrandStyleGuideSection { Id = Guid.NewGuid(), BrandStyleGuideVersionId = approved.Id, SectionKey = BrandStyleGuideSectionKey.Voice, Body = "Warm and plain." });
        approved.Sections.Add(new BrandStyleGuideSection { Id = Guid.NewGuid(), BrandStyleGuideVersionId = approved.Id, SectionKey = BrandStyleGuideSectionKey.ChannelVariant, ChannelKey = "instagram", Body = "Shorter." });
        approved.Rules.Add(new BrandStyleGuideRule { Id = Guid.NewGuid(), BrandStyleGuideVersionId = approved.Id, Kind = BrandStyleGuideRuleKind.Do, Text = "Say \"you\".", SortOrder = 0 });
        approved.SourceLinks.Add(new BrandStyleGuideSourceLink { BrandStyleGuideVersionId = approved.Id, BrandSourceDocumentVersionId = source.Id });
        var draft = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(), BrandStyleGuideId = guide.Id, VersionNumber = 2, ParentVersionId = approved.Id,
            CreatedByMembershipId = member, CreatedAt = Now,
        };

        db.AddRange(document, source, extraction, tag, guide, approved, draft);
        db.BrandStyleGuideApprovals.Add(new BrandStyleGuideApproval { BrandStyleGuideVersionId = approved.Id, ApprovedByMembershipId = member, ApprovedAt = Now });
        db.BrandStyleGuideDefaults.Add(new BrandStyleGuideDefault { WorkspaceId = workspaceId, BrandStyleGuideVersionId = approved.Id, ActivatedByMembershipId = member, ActivatedAt = Now });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new Seeded(document.Id, source.Id, guide.Id, draft.Id, approved.Id);
    }

    private static async Task<int> RowCountAsync(CreatorPantryDbContext db, CancellationToken cancellation) =>
        await db.BrandSourceDocuments.CountAsync(cancellation)
        + await db.BrandSourceDocumentVersions.CountAsync(cancellation)
        + await db.BrandSourceExtractions.CountAsync(cancellation)
        + await db.BrandSourceTags.CountAsync(cancellation)
        + await db.BrandSourceDocumentTags.CountAsync(cancellation)
        + await db.BrandStyleGuides.CountAsync(cancellation)
        + await db.BrandStyleGuideVersions.CountAsync(cancellation)
        + await db.BrandStyleGuideSections.CountAsync(cancellation)
        + await db.BrandStyleGuideRules.CountAsync(cancellation)
        + await db.BrandStyleGuideSourceLinks.CountAsync(cancellation)
        + await db.BrandStyleGuideApprovals.CountAsync(cancellation)
        + await db.BrandStyleGuideDefaults.CountAsync(cancellation);

    [Fact]
    public async Task Erasing_a_workspace_removes_every_brand_row_and_leaves_the_other_workspace_whole()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedEverythingAsync(WorkspaceA);
        await SeedEverythingAsync(WorkspaceB);

        int before;
        await using (var scope = ScopeFor(WorkspaceB))
        {
            before = await RowCountAsync(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>(), cancellation);
        }

        // Fourteen rows: one per table, plus the second guide version and the second section.
        Assert.Equal(14, before);

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            await db.Database.ExecuteSqlAsync($"DELETE FROM Workspaces WHERE Id = {WorkspaceA}", cancellation);

            Assert.Equal(0, await RowCountAsync(db, cancellation));
        }

        await using (var scope = ScopeFor(WorkspaceB))
        {
            Assert.Equal(before, await RowCountAsync(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>(), cancellation));
        }
    }

    [Fact]
    public async Task No_ordinary_delete_takes_blob_backed_history_or_an_approved_version()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedEverythingAsync(WorkspaceA);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Raw SQL, so these are the server's foreign keys refusing and not the application's interceptor.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"DELETE FROM BrandSourceDocuments WHERE Id = {seeded.DocumentId}", cancellation));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"DELETE FROM BrandSourceDocumentVersions WHERE Id = {seeded.SourceVersionId}", cancellation));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"DELETE FROM BrandStyleGuides WHERE Id = {seeded.GuideId}", cancellation));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"DELETE FROM BrandStyleGuideVersions WHERE Id = {seeded.ApprovedVersionId}", cancellation));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"DELETE FROM BrandStyleGuideApprovals WHERE BrandStyleGuideVersionId = {seeded.ApprovedVersionId}", cancellation));

        Assert.Equal(14, await RowCountAsync(db, cancellation));
    }

    [Fact]
    public async Task The_server_refuses_a_draft_default_a_second_voice_section_and_a_url_object_key()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedEverythingAsync(WorkspaceA);

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            (await db.BrandStyleGuideDefaults.SingleAsync(cancellation)).BrandStyleGuideVersionId = seeded.DraftVersionId;

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));
        }

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.BrandStyleGuideSections.Add(new BrandStyleGuideSection
            {
                Id = Guid.NewGuid(), BrandStyleGuideVersionId = seeded.ApprovedVersionId,
                SectionKey = BrandStyleGuideSectionKey.Voice, Body = "A second voice.",
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));
        }

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.BrandSourceDocumentVersions.Add(new BrandSourceDocumentVersion
            {
                Id = Guid.NewGuid(), BrandSourceDocumentId = seeded.DocumentId, VersionNumber = 2, MediaType = "application/pdf",
                SizeBytes = 1, ContentChecksum = "sha256:" + new string('2', 64), OriginalFileName = "a.pdf",
                ObjectKey = "https://account.blob.core.windows.net/brand/a", CreatedByMembershipId = Guid.NewGuid(), CreatedAt = Now,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));
        }
    }

    [Fact]
    public async Task Two_failed_extractions_with_no_text_do_not_collide_on_the_object_key_index()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedEverythingAsync(WorkspaceA);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.BrandSourceExtractions.AddRange(
            new BrandSourceExtraction { Id = Guid.NewGuid(), BrandSourceDocumentVersionId = seeded.SourceVersionId, Ordinal = 2, Status = BrandSourceExtractionStatus.Failed, Origin = BrandSourceExtractionOrigin.Extracted, CreatedAt = Now },
            new BrandSourceExtraction { Id = Guid.NewGuid(), BrandSourceDocumentVersionId = seeded.SourceVersionId, Ordinal = 3, Status = BrandSourceExtractionStatus.Unsupported, Origin = BrandSourceExtractionOrigin.Extracted, CreatedAt = Now });

        await db.SaveChangesAsync(cancellation);
    }
}
