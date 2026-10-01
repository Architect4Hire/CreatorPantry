using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand-source chunk against a real SQL Server, for the three things SQLite structurally cannot show: a
/// native <c>vector(1536)</c> column that accepts and returns the same floats, <c>VECTOR_DISTANCE</c> ranking
/// actual neighbours, and that a nearest-neighbour search is still unable to leave its workspace when the true
/// nearest vector in the table belongs to someone else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The isolation case is the one that matters.</strong> Workspace B is given the search vector exactly
/// — distance zero, the unambiguous best match in the whole table — and workspace A's search must not see it.
/// A single-workspace search proves nothing here: an exact k-nearest-neighbour scan reads every row it is
/// allowed to, so the only question worth asking is what "allowed" means, and that answer comes from the
/// global query filter rather than from anything the query itself says.
/// </para>
/// <para>
/// <strong>No vector index, deliberately, and this is where that shows.</strong> <c>VECTOR_DISTANCE</c> is
/// always exact and never uses an index. Approximate search would mean <c>CREATE VECTOR INDEX</c>, which in
/// SQL Server 2025 is a preview feature needing <c>PREVIEW_FEATURES</c>, a clustered primary key on an
/// <c>int</c> column, and at least a hundred rows — and which would make every workspace's vectors candidates
/// that are filtered after retrieval rather than never considered. See <c>BrandSourceChunkConfiguration</c>.
/// </para>
/// <para>
/// <c>Migrate()</c> rather than <c>EnsureCreated()</c>, for the reason <c>SqlServerRecipeFixture</c> gives: the
/// vector column's DDL is only worth testing as the migration actually writes it, and a model that disagrees
/// with the migration is visible here rather than in a deployment.
/// </para>
/// </remarks>
public sealed class BrandSourceChunkSqlServerTests : IAsyncLifetime
{
    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private const string Model = "text-embedding-3-small";

    private const string Chunker = "text/paragraph-1600c-200o@1";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // Workspaces are not workspace-owned, so they seed before any context is resolved.
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "Workspace A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "Workspace B", Slug = "workspace-b", CreatedAt = Now });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "test-account");

        return scope;
    }

    private static CreatorPantryDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

    /// <summary>
    /// A deterministic vector: <paramref name="weight"/> at <paramref name="axis"/>, zero elsewhere. Unit
    /// vectors on distinct axes are orthogonal, so cosine distance between any two of them is exactly 1 and
    /// between a vector and itself exactly 0 — every assertion below can be worked out by hand.
    /// </summary>
    private static SqlVector<float> Axis(int axis, float weight = 1f)
    {
        var values = new float[BrandPolicy.EmbeddingDimension];
        values[axis] = weight;

        return new SqlVector<float>(values);
    }

    /// <summary>A vector leaning mostly on one axis and a little on another: a near but not exact match.</summary>
    private static SqlVector<float> Mixed(int dominant, int secondary)
    {
        var values = new float[BrandPolicy.EmbeddingDimension];
        values[dominant] = 0.9f;
        values[secondary] = 0.1f;

        return new SqlVector<float>(values);
    }

    /// <summary>
    /// Seeds a document, a version, a succeeded extraction and a current chunk set, then one chunk per
    /// supplied vector. Returns the set.
    /// </summary>
    private async Task<BrandSourceChunkSet> SeedChunksAsync(Guid workspaceId, params SqlVector<float>[] embeddings)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = Db(scope);

        var document = new BrandSourceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = "House style",
            DocumentType = BrandSourceDocumentType.StyleGuide,
            Purpose = BrandSourcePurpose.Voice,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        };

        var version = new BrandSourceDocumentVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = document.Id,
            VersionNumber = 1,
            MediaType = "application/pdf",
            SizeBytes = 2048,
            ContentChecksum = Checksum,
            OriginalFileName = "house-style.pdf",
            ObjectKey = $"brand-sources/{document.Id:N}/1",
            CreatedByMembershipId = Guid.NewGuid(),
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
            ExtractedTextObjectKey = $"brand-sources/{version.Id:N}/text",
            ContentChecksum = Checksum,
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
            ChunkerId = Chunker,
            EmbeddingModel = Model,
            EmbeddingDimension = BrandPolicy.EmbeddingDimension,
            ChunkCount = embeddings.Length,
            CreatedAt = Now,
            EmbeddedAt = Now,
        };

        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(version);
        db.BrandSourceExtractions.Add(extraction);
        db.BrandSourceChunkSets.Add(set);

        for (var i = 0; i < embeddings.Length; i++)
        {
            db.BrandSourceChunks.Add(new BrandSourceChunk
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandSourceChunkSetId = set.Id,
                Ordinal = i + 1,
                StartByteOffset = i * 1400,
                ByteLength = 1600,
                ContentChecksum = Checksum,
                Text = $"We write like a friend who happens to cook. Passage {i + 1}.",
                Embedding = embeddings[i],
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return set;
    }

    [Fact]
    public async Task A_fixed_vector_round_trips_through_the_native_column()
    {
        await SeedChunksAsync(WorkspaceA, Axis(42, 0.5f));

        await using var scope = ScopeFor(WorkspaceA);
        var chunk = await Db(scope).BrandSourceChunks.SingleAsync(TestContext.Current.CancellationToken);
        var values = chunk.Embedding.Memory.ToArray();

        Assert.False(chunk.Embedding.IsNull);
        Assert.Equal(BrandPolicy.EmbeddingDimension, chunk.Embedding.Length);
        Assert.Equal(0.5f, values[42]);
        Assert.Equal(0f, values[41]);
        Assert.Equal(0f, values[BrandPolicy.EmbeddingDimension - 1]);
    }

    [Fact]
    public async Task Cosine_distance_ranks_the_nearer_passage_first()
    {
        // Ordinal 1 is orthogonal to the query, ordinal 2 leans the query's way, ordinal 3 is the query.
        await SeedChunksAsync(WorkspaceA, Axis(1), Mixed(0, 1), Axis(0));

        await using var scope = ScopeFor(WorkspaceA);
        var query = Axis(0);

        var ranked = await Db(scope).BrandSourceChunks
            .OrderBy(chunk => EF.Functions.VectorDistance("cosine", chunk.Embedding, query))
            .Select(chunk => chunk.Ordinal)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([3, 2, 1], ranked);
    }

    [Fact]
    public async Task A_search_cannot_return_another_workspaces_nearest_passage()
    {
        // Workspace A holds only an orthogonal passage; workspace B holds the query vector itself, which is
        // the best match in the table by any metric.
        await SeedChunksAsync(WorkspaceA, Axis(1));
        var exact = await SeedChunksAsync(WorkspaceB, Axis(0));

        await using var scope = ScopeFor(WorkspaceA);
        var query = Axis(0);

        var nearest = await Db(scope).BrandSourceChunks
            .OrderBy(chunk => EF.Functions.VectorDistance("cosine", chunk.Embedding, query))
            .ToListAsync(TestContext.Current.CancellationToken);

        var found = Assert.Single(nearest);

        Assert.Equal(WorkspaceA, found.WorkspaceId);
        Assert.NotEqual(exact.Id, found.BrandSourceChunkSetId);

        // The exact match is in the table and is still there afterwards: the search did not fail to find it
        // because it was missing, but because it was never a candidate.
        await using var ownerScope = ScopeFor(WorkspaceB);

        Assert.Equal(
            1,
            await Db(ownerScope).BrandSourceChunks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_current_set_for_one_extraction_and_model_is_refused_by_the_database()
    {
        var set = await SeedChunksAsync(WorkspaceA, Axis(0));

        await using var scope = ScopeFor(WorkspaceA);
        var db = Db(scope);

        db.BrandSourceChunkSets.Add(new BrandSourceChunkSet
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceA,
            BrandSourceDocumentId = set.BrandSourceDocumentId,
            BrandSourceDocumentVersionId = set.BrandSourceDocumentVersionId,
            BrandSourceExtractionId = set.BrandSourceExtractionId,
            SourceStatus = BrandSourceExtractionStatus.Succeeded,
            Status = BrandSourceChunkSetStatus.Current,
            ChunkerId = Chunker,
            EmbeddingModel = Model,
            EmbeddingDimension = BrandPolicy.EmbeddingDimension,
            ChunkCount = 1,
            CreatedAt = Now,
            EmbeddedAt = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retiring_a_set_leaves_the_replacement_as_the_only_current_one()
    {
        var original = await SeedChunksAsync(WorkspaceA, Axis(0));

        await using var scope = ScopeFor(WorkspaceA);
        var db = Db(scope);

        // The swap as the embedding worker will perform it: the replacement is built while the original is
        // still serving reads, and both move in one save.
        var replacement = new BrandSourceChunkSet
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceA,
            BrandSourceDocumentId = original.BrandSourceDocumentId,
            BrandSourceDocumentVersionId = original.BrandSourceDocumentVersionId,
            BrandSourceExtractionId = original.BrandSourceExtractionId,
            SourceStatus = BrandSourceExtractionStatus.Succeeded,
            Status = BrandSourceChunkSetStatus.Building,
            ChunkerId = "text/paragraph-1600c-200o@2",
            EmbeddingModel = Model,
            EmbeddingDimension = BrandPolicy.EmbeddingDimension,
            ChunkCount = 0,
            CreatedAt = Now,
        };

        db.BrandSourceChunkSets.Add(replacement);
        db.BrandSourceChunks.Add(new BrandSourceChunk
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceA,
            BrandSourceChunkSetId = replacement.Id,
            Ordinal = 1,
            StartByteOffset = 0,
            ByteLength = 1600,
            ContentChecksum = Checksum,
            Text = "We write like a friend who happens to cook. Rechunked.",
            Embedding = Axis(2),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await db.BrandSourceChunkSets
            .SingleAsync(candidate => candidate.Id == original.Id, TestContext.Current.CancellationToken);
        stored.Status = BrandSourceChunkSetStatus.Superseded;

        replacement.Status = BrandSourceChunkSetStatus.Current;
        replacement.ChunkCount = 1;
        replacement.EmbeddedAt = Now;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var current = await db.BrandSourceChunkSets
            .Where(candidate => candidate.Status == BrandSourceChunkSetStatus.Current)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(replacement.Id, Assert.Single(current).Id);
    }
}
