using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand-source chunk schema and isolation, over the same two-workspace SQLite fixture the rest of this
/// module uses. Proves the EF configuration — the composite key that pins a chunk set to a succeeded
/// extraction, the one-current-set rule, ordinal uniqueness, the cascade, the query filter.
/// </summary>
/// <remarks>
/// <strong>Nothing here is a claim about vectors.</strong> Under SQLite the embedding is a list of floats in a
/// text column (see <see cref="SqliteModelCustomizer"/>), so there is no <c>VECTOR_DISTANCE</c> and no
/// similarity to assert. The vectors here are fixed so a write and a read can be compared exactly; ranking,
/// distance and the native column belong to <see cref="BrandSourceChunkSqlServerTests"/>.
/// </remarks>
public sealed class BrandSourceChunkAggregateTests : IDisposable
{
    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private const string Model = "text-embedding-3-small";

    private const string Chunker = "text/paragraph-1600c-200o@1";

    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid A = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid B = RecipeAggregateFixture.WorkspaceB;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// A deterministic vector: one at <paramref name="axis"/> and zero everywhere else. Fixed rather than
    /// random so a round trip is an equality assertion, and unit so that a distance assertion over the same
    /// helper has an answer that can be worked out by hand.
    /// </summary>
    private static SqlVector<float> Unit(int axis)
    {
        var values = new float[BrandPolicy.EmbeddingDimension];
        values[axis] = 1f;

        return new SqlVector<float>(values);
    }

    private static BrandSourceDocument NewDocument(Guid workspaceId) => new()
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

    private static BrandSourceDocumentVersion NewVersion(Guid workspaceId, Guid documentId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandSourceDocumentId = documentId,
        VersionNumber = 1,
        MediaType = "application/pdf",
        SizeBytes = 1024,
        ContentChecksum = Checksum,
        OriginalFileName = "house-style.pdf",
        ObjectKey = $"brand-sources/{documentId:N}/1",
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now,
    };

    private static BrandSourceExtraction NewExtraction(
        Guid workspaceId,
        Guid versionId,
        BrandSourceExtractionStatus status = BrandSourceExtractionStatus.Succeeded) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandSourceDocumentVersionId = versionId,
        Ordinal = 1,
        Status = status,
        Origin = BrandSourceExtractionOrigin.Extracted,
        ExtractedTextObjectKey = status == BrandSourceExtractionStatus.Succeeded
            ? $"brand-sources/{versionId:N}/text"
            : null,
        ContentChecksum = status == BrandSourceExtractionStatus.Succeeded ? Checksum : null,
        CreatedAt = Now,
    };

    private static BrandSourceChunkSet NewSet(
        Guid workspaceId,
        BrandSourceDocument document,
        BrandSourceDocumentVersion version,
        BrandSourceExtraction extraction,
        BrandSourceChunkSetStatus status = BrandSourceChunkSetStatus.Current,
        string model = Model,
        int chunkCount = 1) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandSourceDocumentId = document.Id,
        BrandSourceDocumentVersionId = version.Id,
        BrandSourceExtractionId = extraction.Id,
        SourceStatus = extraction.Status,
        Status = status,
        ChunkerId = Chunker,
        EmbeddingModel = model,
        EmbeddingDimension = BrandPolicy.EmbeddingDimension,
        ChunkCount = chunkCount,
        CreatedAt = Now,
        EmbeddedAt = status == BrandSourceChunkSetStatus.Building ? null : Now,
    };

    private static BrandSourceChunk NewChunk(Guid workspaceId, Guid setId, int ordinal = 1, int axis = 0) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandSourceChunkSetId = setId,
        Ordinal = ordinal,
        StartByteOffset = (ordinal - 1) * 1400,
        ByteLength = 1600,
        ContentChecksum = Checksum,
        Text = $"We write like a friend who happens to cook. Passage {ordinal}.",
        Embedding = Unit(axis),
    };

    /// <summary>Seeds the document, version and extraction a chunk set hangs off, in one workspace.</summary>
    private async Task<SeededSource> SeedSourceAsync(
        Guid workspaceId,
        BrandSourceExtractionStatus status = BrandSourceExtractionStatus.Succeeded)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var document = NewDocument(workspaceId);
        var version = NewVersion(workspaceId, document.Id);
        var extraction = NewExtraction(workspaceId, version.Id, status);

        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(version);
        db.BrandSourceExtractions.Add(extraction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new SeededSource(document, version, extraction);
    }

    private sealed record SeededSource(
        BrandSourceDocument Document,
        BrandSourceDocumentVersion Version,
        BrandSourceExtraction Extraction);

    [Fact]
    public async Task A_set_and_its_chunks_are_workspace_owned_and_filtered()
    {
        await using var scope = _fixture.ScopeFor(A);
        var model = RecipeAggregateFixture.Db(scope).Model;

        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(BrandSourceChunkSet)));
        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(BrandSourceChunk)));

        foreach (var clrType in (Type[])[typeof(BrandSourceChunkSet), typeof(BrandSourceChunk)])
        {
            var entityType = model.FindEntityType(clrType)!;

            Assert.False(entityType.FindProperty(nameof(IWorkspaceOwned.WorkspaceId))!.IsNullable);
            Assert.NotEmpty(entityType.GetDeclaredQueryFilters());
        }
    }

    [Fact]
    public async Task A_chunk_is_derived_data_rather_than_history()
    {
        // Deliberately not IImmutableRecord: a re-embedding replaces chunks, and a row the interceptor refused
        // to delete could never be retired.
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(typeof(BrandSourceChunk)));
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(typeof(BrandSourceChunkSet)));

        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction);
        db.BrandSourceChunkSets.Add(set);
        db.BrandSourceChunks.Add(NewChunk(A, set.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.BrandSourceChunks.RemoveRange(
            await db.BrandSourceChunks.ToListAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.BrandSourceChunks.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_fixed_embedding_round_trips_through_the_harness()
    {
        var source = await SeedSourceAsync(A);

        await using var write = _fixture.ScopeFor(A);
        var writing = RecipeAggregateFixture.Db(write);

        var set = NewSet(A, source.Document, source.Version, source.Extraction);
        writing.BrandSourceChunkSets.Add(set);
        writing.BrandSourceChunks.Add(NewChunk(A, set.Id, axis: 7));
        await writing.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read = _fixture.ScopeFor(A);
        var stored = await RecipeAggregateFixture.Db(read).BrandSourceChunks
            .SingleAsync(TestContext.Current.CancellationToken);
        var values = stored.Embedding.Memory.ToArray();

        Assert.Equal(BrandPolicy.EmbeddingDimension, stored.Embedding.Length);
        Assert.Equal(1f, values[7]);
        Assert.Equal(0f, values[6]);
    }

    [Fact]
    public async Task Chunks_cannot_hang_off_an_extraction_that_produced_no_text()
    {
        var source = await SeedSourceAsync(A, BrandSourceExtractionStatus.Unsupported);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        // Stating the extraction's real status trips the check constraint: only Succeeded carries text.
        db.BrandSourceChunkSets.Add(NewSet(A, source.Document, source.Version, source.Extraction));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Claiming_a_succeeded_source_that_did_not_succeed_has_no_key_to_resolve_against()
    {
        var source = await SeedSourceAsync(A, BrandSourceExtractionStatus.Failed);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        // Lying about the status to get past the check constraint instead fails the foreign key: there is no
        // (workspace, extraction, version, Succeeded) row to point at.
        var set = NewSet(A, source.Document, source.Version, source.Extraction);
        set.SourceStatus = BrandSourceExtractionStatus.Succeeded;
        db.BrandSourceChunkSets.Add(set);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_set_cannot_claim_a_version_its_extraction_never_read()
    {
        var first = await SeedSourceAsync(A);
        var second = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        // One document's extraction, another document's version: the composite key refuses the pairing.
        db.BrandSourceChunkSets.Add(NewSet(A, first.Document, second.Version, first.Extraction));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_one_set_per_extraction_and_model_may_be_current()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        db.BrandSourceChunkSets.Add(NewSet(A, source.Document, source.Version, source.Extraction));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.BrandSourceChunkSets.Add(NewSet(A, source.Document, source.Version, source.Extraction));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_replacement_may_be_built_beside_the_set_it_will_replace()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        db.BrandSourceChunkSets.Add(NewSet(A, source.Document, source.Version, source.Extraction));
        db.BrandSourceChunkSets.Add(NewSet(
            A,
            source.Document,
            source.Version,
            source.Extraction,
            BrandSourceChunkSetStatus.Building,
            chunkCount: 0));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.BrandSourceChunkSets.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Any_number_of_superseded_sets_may_wait_to_be_swept()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        for (var i = 0; i < 2; i++)
        {
            db.BrandSourceChunkSets.Add(NewSet(
                A,
                source.Document,
                source.Version,
                source.Extraction,
                BrandSourceChunkSetStatus.Superseded,
                chunkCount: 0));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.BrandSourceChunkSets.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_model_embeds_the_same_extraction_without_colliding()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        db.BrandSourceChunkSets.Add(NewSet(A, source.Document, source.Version, source.Extraction));
        db.BrandSourceChunkSets.Add(NewSet(
            A, source.Document, source.Version, source.Extraction, model: "text-embedding-ada-002"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.BrandSourceChunkSets.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_current_set_cannot_be_empty()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        db.BrandSourceChunkSets.Add(
            NewSet(A, source.Document, source.Version, source.Extraction, chunkCount: 0));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_set_still_being_built_has_no_embedded_timestamp()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        var set = NewSet(
            A,
            source.Document,
            source.Version,
            source.Extraction,
            BrandSourceChunkSetStatus.Building,
            chunkCount: 0);
        set.EmbeddedAt = Now;
        db.BrandSourceChunkSets.Add(set);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_set_records_the_width_the_column_holds()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction);
        set.EmbeddingDimension = 768;
        db.BrandSourceChunkSets.Add(set);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_chunk_per_ordinal_in_a_set()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction, chunkCount: 2);
        db.BrandSourceChunkSets.Add(set);
        db.BrandSourceChunks.Add(NewChunk(A, set.Id, ordinal: 1));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.BrandSourceChunks.Add(NewChunk(A, set.Id, ordinal: 1));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_chunk_cannot_span_more_bytes_than_a_chunk_may()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction);
        db.BrandSourceChunkSets.Add(set);

        var chunk = NewChunk(A, set.Id);
        chunk.ByteLength = BrandPolicy.ChunkMaxBytes + 1;
        db.BrandSourceChunks.Add(chunk);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_chunk_cannot_be_attached_to_another_workspaces_set()
    {
        var source = await SeedSourceAsync(A);

        await using var ownerScope = _fixture.ScopeFor(A);
        var owner = RecipeAggregateFixture.Db(ownerScope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction);
        owner.BrandSourceChunkSets.Add(set);
        owner.BrandSourceChunks.Add(NewChunk(A, set.Id));
        await owner.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var intruderScope = _fixture.ScopeFor(B);
        var intruder = RecipeAggregateFixture.Db(intruderScope);

        // The set id is real; the workspace is not its owner's, so the composite key has nothing to resolve.
        intruder.BrandSourceChunks.Add(NewChunk(B, set.Id, ordinal: 2));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => intruder.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Neither_sets_nor_chunks_cross_the_workspace_boundary()
    {
        var source = await SeedSourceAsync(A);

        await using var ownerScope = _fixture.ScopeFor(A);
        var owner = RecipeAggregateFixture.Db(ownerScope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction, chunkCount: 2);
        owner.BrandSourceChunkSets.Add(set);
        owner.BrandSourceChunks.Add(NewChunk(A, set.Id, ordinal: 1, axis: 0));
        owner.BrandSourceChunks.Add(NewChunk(A, set.Id, ordinal: 2, axis: 1));
        await owner.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await owner.BrandSourceChunks.CountAsync(TestContext.Current.CancellationToken));

        await using var otherScope = _fixture.ScopeFor(B);
        var other = RecipeAggregateFixture.Db(otherScope);

        Assert.Empty(await other.BrandSourceChunkSets.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await other.BrandSourceChunks.ToListAsync(TestContext.Current.CancellationToken));

        // Naming the set directly, in case an unfiltered read is hiding behind a collection that happens to
        // be empty for another reason.
        Assert.Null(await other.BrandSourceChunks.FirstOrDefaultAsync(
            chunk => chunk.BrandSourceChunkSetId == set.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removing_a_set_removes_the_chunks_in_it()
    {
        var source = await SeedSourceAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        var set = NewSet(A, source.Document, source.Version, source.Extraction, chunkCount: 2);
        db.BrandSourceChunkSets.Add(set);
        db.BrandSourceChunks.Add(NewChunk(A, set.Id, ordinal: 1));
        db.BrandSourceChunks.Add(NewChunk(A, set.Id, ordinal: 2));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.BrandSourceChunkSets.Remove(
            await db.BrandSourceChunkSets.SingleAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.BrandSourceChunks.ToListAsync(TestContext.Current.CancellationToken));
    }
}
