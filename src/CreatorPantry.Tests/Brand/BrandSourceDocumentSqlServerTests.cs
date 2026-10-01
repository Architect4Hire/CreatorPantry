using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// Replacing a brand source document against a real SQL Server, for the two things SQLite cannot show: a row
/// version that actually moves, and the unique index that catches a race the row version somehow did not.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BrandSourceDocumentReplaceEndpointTests"/> covers the contract — statuses, refusals, replay,
/// compensation, isolation — over SQLite, where <c>RowVersion</c> is filled on insert and never moves. That
/// harness can prove a token the application rejects; it cannot prove a writer losing at the save, because
/// nothing there makes a token go stale. This file is only those cases.
/// </para>
/// <para>
/// The race is made deterministic rather than timed, as the brand profile's is: two scopes each read the
/// document at the same row version, then write one after the other, so the second is always the loser and
/// the test never depends on scheduling.
/// </para>
/// </remarks>
public sealed class BrandSourceDocumentSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private readonly InMemoryPrivateObjectStore _store = new();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection()

            // The source document gateway and data layer both take a logger; the brand profile does not,
            // which is why its own SQL Server fixture gets away without this.
            .AddLogging()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // A real key, because an upload and a replacement both declare themselves idempotent and
                    // the fingerprint is keyed. Any 32 bytes will do; it never leaves this fixture.
                    ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build())
            .AddBrandModule()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()));

        // The module registers a store that refuses everything and a scanner that clears nothing; neither is
        // what these tests are about, so both are replaced with doubles that behave.
        services.RemoveAll<IPrivateObjectStore>();
        services.AddSingleton<IPrivateObjectStore>(_store);
        services.RemoveAll<IMalwareScanGateway>();
        services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());

        _provider = services.BuildServiceProvider(validateScopes: true);

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

    [Fact]
    public async Task The_row_version_moves_on_a_replacement_so_the_token_that_authorised_it_is_spent()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(WorkspaceA);

        string secondToken;
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var replaced = await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>().ReplaceAsync(
                "user",
                seeded.DocumentId,
                new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = seeded.Token },
                FileOf("two"),
                Guid.NewGuid().ToString("N"),
                cancellation);

            Assert.True(replaced.Result.Succeeded, replaced.Result.Error?.Message);
            Assert.Equal(2, replaced.Result.Value!.CurrentVersion.VersionNumber);
            secondToken = replaced.Result.Value.ConcurrencyToken;
        }

        // The real server bumped it, which SQLite does not: the token that authorised the replacement is not
        // the token the document now carries.
        Assert.NotEqual(seeded.Token, secondToken);

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var stale = await scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentFacade>().ReplaceAsync(
                "user",
                seeded.DocumentId,
                new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = seeded.Token },
                FileOf("three"),
                Guid.NewGuid().ToString("N"),
                cancellation);

            Assert.Equal(BrandErrorCodes.SourceConflict, stale.Result.Error?.Code);
        }

        // Two versions and two objects: the refused third attempt left neither a row nor a blob.
        await using var check = ScopeFor(WorkspaceA);
        var db = check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal([1, 2], (await db.BrandSourceDocumentVersions.OrderBy(row => row.VersionNumber).ToListAsync(cancellation))
            .Select(row => row.VersionNumber));
        Assert.Equal(2, _store.Keys.Count);
    }

    [Fact]
    public async Task Of_two_replacements_that_read_the_same_version_the_second_loses_and_its_object_is_removed()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(WorkspaceA);

        await using var first = ScopeFor(WorkspaceA);
        await using var second = ScopeFor(WorkspaceA);
        var firstLayer = first.ServiceProvider.GetRequiredService<IBrandSourceDocumentDataLayer>();
        var secondLayer = second.ServiceProvider.GetRequiredService<IBrandSourceDocumentDataLayer>();

        // Both read the document at version 1, before either has written.
        var firstCopy = (await firstLayer.FindForUpdateAsync(seeded.DocumentId, cancellation))!;
        var secondCopy = (await secondLayer.FindForUpdateAsync(seeded.DocumentId, cancellation))!;
        Assert.Equal(1, firstCopy.CurrentVersionNumber);
        Assert.Equal(1, secondCopy.CurrentVersionNumber);

        var winner = NextVersionFor(firstCopy, "winner");
        var loser = NextVersionFor(secondCopy, "loser");

        var won = await firstLayer.ReplaceAsync(firstCopy, winner, AuditOf(firstCopy), StreamOf("winner"), cancellation);
        var lost = await secondLayer.ReplaceAsync(secondCopy, loser, AuditOf(secondCopy), StreamOf("loser"), cancellation);

        Assert.Equal(BrandSourceReplaceOutcome.Replaced, won.Outcome);
        Assert.Equal(BrandSourceReplaceOutcome.Conflict, lost.Outcome);

        // Nothing staged for the next save on the losing scope to commit.
        Assert.Empty(second.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().ChangeTracker.Entries());

        await using var check = ScopeFor(WorkspaceA);
        var db = check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var versions = await db.BrandSourceDocumentVersions.OrderBy(row => row.VersionNumber).ToListAsync(cancellation);

        Assert.Equal([1, 2], versions.Select(row => row.VersionNumber));
        Assert.Equal(winner.Id, versions[1].Id);
        Assert.Equal(2, (await db.BrandSourceDocuments.SingleAsync(cancellation)).CurrentVersionNumber);

        // A conflict is a write that lost, not a write that never happened: the object it had already put
        // down is gone, and the winner's and the original's are both still there.
        Assert.Equal(2, _store.Keys.Count);
        Assert.DoesNotContain(_store.Keys, key => key.Contains(loser.Id.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(_store.Keys, key => key.Contains(winner.Id.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(_store.Keys, key => key.Contains(seeded.VersionId.ToString("N"), StringComparison.Ordinal));
    }

    /// <summary>
    /// The second guard, on its own. The row version is what normally decides a race; this is what would
    /// catch one that somehow got past it, and it only means anything on a server that enforces the index.
    /// </summary>
    [Fact]
    public async Task The_unique_index_refuses_a_second_version_with_a_number_the_document_already_has()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(WorkspaceB);

        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.BrandSourceDocumentVersions.Add(new BrandSourceDocumentVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceB,
            BrandSourceDocumentId = seeded.DocumentId,

            // The number the document already has, which is what two writers racing on version 1 would both
            // try to claim.
            VersionNumber = 1,
            MediaType = "application/pdf",
            SizeBytes = 10,
            ContentChecksum = "sha256:" + new string('b', 64),
            OriginalFileName = "collision.pdf",
            ObjectKey = BrandSourceObjectKey.ForOriginal(WorkspaceB, seeded.DocumentId, Guid.NewGuid()),
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));

        db.ChangeTracker.Clear();
        Assert.Single(await db.BrandSourceDocumentVersions.ToListAsync(cancellation));
    }

    [Fact]
    public async Task One_workspace_cannot_read_the_others_document_for_replacement()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(WorkspaceA);

        await using var scope = ScopeFor(WorkspaceB);
        var layer = scope.ServiceProvider.GetRequiredService<IBrandSourceDocumentDataLayer>();

        // The query filter, on a real server: B's scope cannot even load the row to write to it.
        Assert.Null(await layer.FindForUpdateAsync(seeded.DocumentId, cancellation));
    }

    // ---- Helpers ----

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == WorkspaceA ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        return scope;
    }

    private static MemoryStream StreamOf(string body) => new(BrandSourceSampleFiles.Pdf(body));

    private static BrandSourceUploadFile FileOf(string body) => new(StreamOf(body), "house-style.pdf");

    /// <summary>The next version of a document just read for replacement, with its counter already advanced.</summary>
    private static BrandSourceDocumentVersion NextVersionFor(BrandSourceDocument document, string body)
    {
        var bytes = BrandSourceSampleFiles.Pdf(body);
        var version = new BrandSourceDocumentVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = document.WorkspaceId,
            BrandSourceDocumentId = document.Id,
            VersionNumber = document.CurrentVersionNumber + 1,
            MediaType = "application/pdf",
            SizeBytes = bytes.Length,
            ContentChecksum = ChecksumOf(bytes),
            OriginalFileName = "house-style.pdf",
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

        document.CurrentVersionNumber = version.VersionNumber;
        document.UpdatedAt = Now;
        document.UpdatedByMembershipId = version.CreatedByMembershipId;

        return version;
    }

    /// <summary>What the store will measure, so the data layer's size and checksum check agrees with it.</summary>
    private static string ChecksumOf(byte[] bytes) =>
        "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static AuditEntry AuditOf(BrandSourceDocument document) => new(
        "user",
        BrandAuditActions.SourceDocumentReplaced,
        BrandAuditActions.SourceDocumentResourceType,
        document.Id.ToString("D"),
        Guid.NewGuid(),
        "test",
        "1",
        document.CurrentVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>One uploaded document at version 1, through the real seam.</summary>
    private async Task<SeededDocument> SeedAsync(Guid workspaceId)
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
            FileOf("one"),
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.True(uploaded.Result.Succeeded, uploaded.Result.Error?.Message);
        var document = uploaded.Result.Value!;

        return new SeededDocument(document.Id, document.CurrentVersion.Id, document.ConcurrencyToken);
    }

    private sealed record SeededDocument(Guid DocumentId, Guid VersionId, string Token);
}
