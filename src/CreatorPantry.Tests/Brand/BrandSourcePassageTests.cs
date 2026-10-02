using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The grounding read a brand-guide proposal is built from: which passages one workspace can be given for the
/// document versions it selected, which selections could supply none, and that another workspace's passages are
/// never among them.
/// </summary>
/// <remarks>
/// <strong>Nothing here is a claim about similarity.</strong> The read is deliberately in reading order — the
/// embedding exists and is not consulted — so these are assertions about scoping, budgeting and reporting. The
/// native vector column and <c>VECTOR_DISTANCE</c> belong to <see cref="BrandSourceChunkSqlServerTests"/>.
/// </remarks>
public sealed class BrandSourcePassageTests : IDisposable
{
    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid A = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid B = RecipeAggregateFixture.WorkspaceB;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Passages_come_back_in_reading_order_for_the_version_that_was_named()
    {
        var document = await SeedAsync(A, versionNumber: 1, chunks: 3);

        var supplied = await ReadAsync(A, [new BrandSourcePassageSelector(document.DocumentId, 1)]);

        Assert.Empty(supplied.Unavailable);
        Assert.Equal([1, 2, 3], supplied.Passages.Select(passage => passage.Ordinal));
        Assert.All(supplied.Passages, passage =>
        {
            Assert.Equal(document.DocumentId, passage.DocumentId);
            Assert.Equal(1, passage.VersionNumber);
            Assert.NotEqual(Guid.Empty, passage.PassageId);
            Assert.NotEmpty(passage.Text);
        });
    }

    [Fact]
    public async Task A_version_with_no_current_chunk_set_supplies_nothing_and_is_reported()
    {
        // Text extracted but never embedded: the honest answer is that this document contributed nothing, which
        // is what lets a proposal say its evidence was thin instead of looking fully sourced.
        var document = await SeedAsync(A, versionNumber: 1, chunks: 0);

        var supplied = await ReadAsync(A, [new BrandSourcePassageSelector(document.DocumentId, 1)]);

        Assert.Empty(supplied.Passages);
        Assert.Equal(
            [new BrandSourcePassageSelector(document.DocumentId, 1)],
            supplied.Unavailable);
    }

    [Theory]
    [InlineData(BrandSourceChunkSetStatus.Building)]
    [InlineData(BrandSourceChunkSetStatus.Superseded)]
    public async Task Only_a_current_chunk_set_is_read(BrandSourceChunkSetStatus status)
    {
        // Building is incomplete and Superseded describes text the document has moved past. Grounding on either
        // would cite a passage the creator can no longer see.
        var document = await SeedAsync(A, versionNumber: 1, chunks: 2, status: status);

        var supplied = await ReadAsync(A, [new BrandSourcePassageSelector(document.DocumentId, 1)]);

        Assert.Empty(supplied.Passages);
        Assert.Single(supplied.Unavailable);
    }

    [Fact]
    public async Task A_version_number_the_document_does_not_have_supplies_nothing()
    {
        var document = await SeedAsync(A, versionNumber: 1, chunks: 2);

        var supplied = await ReadAsync(A, [new BrandSourcePassageSelector(document.DocumentId, 2)]);

        Assert.Empty(supplied.Passages);
        Assert.Single(supplied.Unavailable);
    }

    [Fact]
    public async Task One_document_cannot_spend_the_whole_budget_on_itself()
    {
        // Per-version cap times version cap exceeds the overall budget, so something must be trimmed. Taking the
        // first N would drop the later documents entirely while reporting nothing unavailable — a proposal
        // grounded on part of the selection whose caller believed it read all of it.
        var first = await SeedAsync(A, versionNumber: 1, chunks: BrandPolicy.MaxGroundingPassagesPerVersion);
        var second = await SeedAsync(A, versionNumber: 1, chunks: BrandPolicy.MaxGroundingPassagesPerVersion);
        var third = await SeedAsync(A, versionNumber: 1, chunks: BrandPolicy.MaxGroundingPassagesPerVersion);
        var fourth = await SeedAsync(A, versionNumber: 1, chunks: BrandPolicy.MaxGroundingPassagesPerVersion);

        var supplied = await ReadAsync(
            A,
            [
                new BrandSourcePassageSelector(first.DocumentId, 1),
                new BrandSourcePassageSelector(second.DocumentId, 1),
                new BrandSourcePassageSelector(third.DocumentId, 1),
                new BrandSourcePassageSelector(fourth.DocumentId, 1),
            ]);

        Assert.Equal(BrandPolicy.MaxGroundingPassages, supplied.Passages.Count);
        Assert.Empty(supplied.Unavailable);

        // Every document is represented, which is the property the round-robin exists for.
        Assert.Equal(
            4,
            supplied.Passages.Select(passage => passage.DocumentId).Distinct().Count());

        // And the result is still in document and reading order, so a citation is easy to find.
        Assert.Equal(
            supplied.Passages
                .OrderBy(passage => passage.DocumentId)
                .ThenBy(passage => passage.VersionNumber)
                .ThenBy(passage => passage.Ordinal)
                .ToList(),
            supplied.Passages);
    }

    [Fact]
    public async Task A_selection_beyond_the_version_cap_is_reported_rather_than_dropped()
    {
        // One more document than the cap allows. The extra one is not supplied -- and must not be silently
        // absent either, or a caller would ground a proposal on fewer documents than it chose while believing it
        // had them all.
        var documents = new List<Guid>();

        for (var index = 0; index <= BrandPolicy.MaxGroundingSourceVersions; index++)
        {
            documents.Add((await SeedAsync(A, versionNumber: 1, chunks: 1)).DocumentId);
        }

        var supplied = await ReadAsync(
            A, [.. documents.Select(id => new BrandSourcePassageSelector(id, 1))]);

        Assert.Equal(BrandPolicy.MaxGroundingSourceVersions, supplied.Passages.Count);
        Assert.Single(supplied.Unavailable);

        // Everything either came back or was reported. Nothing fell between the two.
        Assert.Equal(
            documents.Count,
            supplied.Passages.Select(passage => passage.DocumentId)
                .Concat(supplied.Unavailable.Select(selector => selector.DocumentId))
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task A_document_named_twice_does_not_get_twice_the_budget()
    {
        var document = await SeedAsync(A, versionNumber: 1, chunks: 3);
        var selector = new BrandSourcePassageSelector(document.DocumentId, 1);

        var supplied = await ReadAsync(A, [selector, selector]);

        Assert.Equal(3, supplied.Passages.Count);
        Assert.Equal(3, supplied.Passages.Select(passage => passage.PassageId).Distinct().Count());
    }

    [Fact]
    public async Task No_selection_at_all_is_a_legitimate_read()
    {
        // A proposal from the creator's own guide answers alone is a real request, not a malformed one.
        var supplied = await ReadAsync(A, []);

        Assert.Empty(supplied.Passages);
        Assert.Empty(supplied.Unavailable);
    }

    [Fact]
    public async Task One_workspace_can_never_be_given_the_others_passages()
    {
        var mine = await SeedAsync(A, versionNumber: 1, chunks: 2);
        var theirs = await SeedAsync(B, versionNumber: 1, chunks: 2);

        // A names its own document and B's. B's supplies nothing and is reported unavailable, exactly as a
        // document that was never created would be — the reasons are deliberately indistinguishable.
        var supplied = await ReadAsync(
            A,
            [
                new BrandSourcePassageSelector(mine.DocumentId, 1),
                new BrandSourcePassageSelector(theirs.DocumentId, 1),
            ]);

        Assert.All(supplied.Passages, passage => Assert.Equal(mine.DocumentId, passage.DocumentId));
        Assert.Equal(
            [new BrandSourcePassageSelector(theirs.DocumentId, 1)],
            supplied.Unavailable);

        var neverIssued = await ReadAsync(A, [new BrandSourcePassageSelector(Guid.NewGuid(), 1)]);
        Assert.Empty(neverIssued.Passages);
        Assert.Single(neverIssued.Unavailable);

        // B's passages are still there: A could not see them, rather than them not existing.
        var owner = await ReadAsync(B, [new BrandSourcePassageSelector(theirs.DocumentId, 1)]);
        Assert.Equal(2, owner.Passages.Count);
    }

    [Fact]
    public async Task The_read_fails_closed_when_no_workspace_is_resolved()
    {
        await SeedAsync(A, versionNumber: 1, chunks: 2);

        // The property the repository's "the query filter supplies the workspace" comment rests on. Without a
        // resolved workspace the filter has nothing to apply, and the answer must be a throw rather than an
        // empty page or — far worse — every workspace's passages.
        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        IBrandSourcePassageFacade facade = new BrandSourcePassageFacade(
            new BrandSourcePassageBusiness(new BrandSourcePassageDataLayer(
                new BrandSourcePassageRepository(db),
                scope.ServiceProvider.GetRequiredService<IWorkspaceContext>())));

        await Assert.ThrowsAnyAsync<Exception>(() => facade.ListPassagesAsync(
            [new BrandSourcePassageSelector(Guid.NewGuid(), 1)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Documents_with_text_are_found_across_batches_and_only_their_ids_are_returned()
    {
        // More versions than one grounding read may take, so a single read would silently stop at the first ten.
        var withText = new List<SeededDocument>();

        for (var i = 0; i < BrandPolicy.MaxGroundingSourceVersions + 5; i++)
        {
            withText.Add(await SeedAsync(A, versionNumber: 1, chunks: 2));
        }

        var without = await SeedAsync(A, versionNumber: 1, chunks: 0);
        var selectors = withText.Append(without).Select(document => new BrandSourcePassageSelector(document.DocumentId, 1)).ToList();

        var found = await ListDocumentsWithTextAsync(A, selectors);

        Assert.Equal(withText.Select(document => document.DocumentId).Order(), found.Order());
        Assert.DoesNotContain(without.DocumentId, found);
    }

    [Fact]
    public async Task Another_workspaces_documents_are_never_reported_as_having_text()
    {
        var mine = await SeedAsync(A, versionNumber: 1, chunks: 1);
        var theirs = await SeedAsync(B, versionNumber: 1, chunks: 1);

        var found = await ListDocumentsWithTextAsync(
            A,
            [new BrandSourcePassageSelector(mine.DocumentId, 1), new BrandSourcePassageSelector(theirs.DocumentId, 1)]);

        Assert.Equal([mine.DocumentId], found);
    }

    // ---- Harness ----

    private sealed record SeededDocument(Guid DocumentId, Guid VersionId);

    /// <summary>
    /// Composes the passage seam over one scope's DbContext, rather than through a container: the fixture
    /// registers tenancy and the context and nothing else, and the seam is four constructors deep.
    /// </summary>
    private async Task<BrandSourcePassageSetServiceModel> ReadAsync(
        Guid workspaceId, IReadOnlyList<BrandSourcePassageSelector> selectors)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        IBrandSourcePassageFacade facade = new BrandSourcePassageFacade(
            new BrandSourcePassageBusiness(new BrandSourcePassageDataLayer(
                new BrandSourcePassageRepository(db),
                scope.ServiceProvider.GetRequiredService<IWorkspaceContext>())));

        return await facade.ListPassagesAsync(selectors, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> ListDocumentsWithTextAsync(
        Guid workspaceId, IReadOnlyList<BrandSourcePassageSelector> selectors)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        IBrandSourcePassageFacade facade = new BrandSourcePassageFacade(
            new BrandSourcePassageBusiness(new BrandSourcePassageDataLayer(
                new BrandSourcePassageRepository(db),
                scope.ServiceProvider.GetRequiredService<IWorkspaceContext>())));

        return await facade.ListDocumentsWithTextAsync(selectors, TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a document, one version, its extraction, and a chunk set with <paramref name="chunks"/> passages.</summary>
    private async Task<SeededDocument> SeedAsync(
        Guid workspaceId,
        int versionNumber,
        int chunks,
        BrandSourceChunkSetStatus status = BrandSourceChunkSetStatus.Current)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var member = Guid.NewGuid();

        var document = new BrandSourceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = "House style",
            DocumentType = BrandSourceDocumentType.StyleGuide,
            Purpose = BrandSourcePurpose.Voice,
            CurrentVersionNumber = versionNumber,
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
            VersionNumber = versionNumber,
            MediaType = "application/pdf",
            SizeBytes = 1024,
            ContentChecksum = Checksum,
            OriginalFileName = "house-style.pdf",
            ObjectKey = $"brand-sources/{document.Id:N}/{versionNumber}",
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
            ContentChecksum = Checksum,
            CreatedAt = Now,
        };

        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(version);
        db.BrandSourceExtractions.Add(extraction);

        if (chunks > 0)
        {
            var set = new BrandSourceChunkSet
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                BrandSourceDocumentId = document.Id,
                BrandSourceDocumentVersionId = version.Id,
                BrandSourceExtractionId = extraction.Id,
                SourceStatus = BrandSourceExtractionStatus.Succeeded,
                Status = status,
                ChunkerId = "text/paragraph-1600c-200o@1",
                EmbeddingModel = "text-embedding-3-small",
                EmbeddingDimension = BrandPolicy.EmbeddingDimension,
                ChunkCount = chunks,
                CreatedAt = Now,
                EmbeddedAt = status is BrandSourceChunkSetStatus.Building ? null : Now,
                SupersededAt = status is BrandSourceChunkSetStatus.Superseded ? Now : null,
            };

            db.BrandSourceChunkSets.Add(set);

            for (var ordinal = 1; ordinal <= chunks; ordinal++)
            {
                db.BrandSourceChunks.Add(new BrandSourceChunk
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    BrandSourceChunkSetId = set.Id,
                    Ordinal = ordinal,
                    StartByteOffset = (ordinal - 1) * 1400,
                    ByteLength = 1600,
                    ContentChecksum = Checksum,
                    Text = $"We write like a friend who happens to cook. Passage {ordinal}.",
                    Embedding = Unit(ordinal % BrandPolicy.EmbeddingDimension),
                });
            }
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new SeededDocument(document.Id, version.Id);
    }

    /// <summary>A fixed vector, so nothing here depends on a value that could differ between runs.</summary>
    private static SqlVector<float> Unit(int axis)
    {
        var values = new float[BrandPolicy.EmbeddingDimension];
        values[axis] = 1f;

        return new SqlVector<float>(values);
    }
}
