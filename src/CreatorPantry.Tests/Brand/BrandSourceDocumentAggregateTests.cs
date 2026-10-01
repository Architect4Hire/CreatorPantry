using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand source-document schema and isolation, over the same two-workspace SQLite fixture the recipe
/// aggregate uses. Proves the EF configuration — keys, check constraints, delete behavior, immutability, the
/// query filter — not that SQL Server accepts the DDL, which the migration's own verification covers.
/// </summary>
public sealed class BrandSourceDocumentAggregateTests : IDisposable
{
    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid A = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid B = RecipeAggregateFixture.WorkspaceB;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static BrandSourceDocument NewDocument(Guid workspaceId, string title = "House style") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        Title = title,
        DocumentType = BrandSourceDocumentType.StyleGuide,
        Purpose = BrandSourcePurpose.Voice,
        CreatedAt = Now,
        UpdatedAt = Now,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
    };

    private static BrandSourceDocumentVersion NewVersion(Guid workspaceId, Guid documentId, int number = 1, string? objectKey = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandSourceDocumentId = documentId,
        VersionNumber = number,
        MediaType = "application/pdf",
        SizeBytes = 1024,
        ContentChecksum = Checksum,
        OriginalFileName = "house-style.pdf",
        ObjectKey = objectKey ?? $"brand-sources/{documentId:N}/{number}",
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now,
    };

    private static BrandSourceExtraction NewExtraction(Guid workspaceId, Guid versionId, int ordinal = 1) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandSourceDocumentVersionId = versionId,
        Ordinal = ordinal,
        Status = BrandSourceExtractionStatus.Succeeded,
        Origin = BrandSourceExtractionOrigin.Extracted,
        ExtractedTextObjectKey = $"brand-sources/text/{versionId:N}/{ordinal}",
        ContentChecksum = Checksum,
        CreatedAt = Now,
    };

    private static BrandSourceTag NewTag(Guid workspaceId, string name = "Launch") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        CreatedAt = Now,
    };

    /// <summary>A document with its first version, saved from its own workspace's scope.</summary>
    private async Task<(BrandSourceDocument Document, BrandSourceDocumentVersion Version)> SeedAsync(Guid workspaceId, string title = "House style")
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var document = NewDocument(workspaceId, title);
        var version = NewVersion(workspaceId, document.Id);
        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(version);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (document, version);
    }

    private async Task AssertRefusedAsync(Guid workspaceId, Action<CreatorPantryDbContext> arrange)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        arrange(db);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_workspace_holds_its_own_documents_and_cannot_see_the_others()
    {
        // The same title, object key shape, checksum and version number in both: none is unique across workspaces.
        var a = await SeedAsync(A);
        var b = await SeedAsync(B);
        var ct = TestContext.Current.CancellationToken;

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        Assert.Equal(a.Document.Id, (await db.BrandSourceDocuments.SingleAsync(ct)).Id);
        Assert.Equal(a.Version.Id, (await db.BrandSourceDocumentVersions.SingleAsync(ct)).Id);
        Assert.Null(await db.BrandSourceDocuments.FirstOrDefaultAsync(d => d.Id == b.Document.Id, ct));
        Assert.Null(await db.BrandSourceDocumentVersions.FirstOrDefaultAsync(v => v.Id == b.Version.Id, ct));
    }

    [Fact]
    public async Task Extractions_and_tags_are_scoped_to_their_workspace()
    {
        var a = await SeedAsync(A);
        var b = await SeedAsync(B);
        var ct = TestContext.Current.CancellationToken;

        foreach (var (workspaceId, seeded) in new[] { (A, a), (B, b) })
        {
            await using var seedScope = _fixture.ScopeFor(workspaceId);
            var seedDb = RecipeAggregateFixture.Db(seedScope);
            var tag = NewTag(workspaceId);
            seedDb.BrandSourceTags.Add(tag);
            seedDb.BrandSourceDocumentTags.Add(new BrandSourceDocumentTag { WorkspaceId = workspaceId, BrandSourceDocumentId = seeded.Document.Id, BrandSourceTagId = tag.Id });
            seedDb.BrandSourceExtractions.Add(NewExtraction(workspaceId, seeded.Version.Id));
            await seedDb.SaveChangesAsync(ct);
        }

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        Assert.Equal(a.Version.Id, (await db.BrandSourceExtractions.SingleAsync(ct)).BrandSourceDocumentVersionId);
        Assert.Equal(A, (await db.BrandSourceTags.SingleAsync(ct)).WorkspaceId);
        Assert.Equal(a.Document.Id, (await db.BrandSourceDocumentTags.SingleAsync(ct)).BrandSourceDocumentId);
    }

    [Fact]
    public async Task A_version_cannot_point_at_another_workspaces_document()
    {
        var b = await SeedAsync(B);

        await AssertRefusedAsync(A, db => db.BrandSourceDocumentVersions.Add(NewVersion(A, b.Document.Id, number: 2)));
    }

    [Fact]
    public async Task An_extraction_cannot_point_at_another_workspaces_version()
    {
        var b = await SeedAsync(B);

        await AssertRefusedAsync(A, db => db.BrandSourceExtractions.Add(NewExtraction(A, b.Version.Id)));
    }

    [Fact]
    public async Task A_document_cannot_carry_another_workspaces_tag()
    {
        var a = await SeedAsync(A);
        var tagB = NewTag(B);
        await using (var scopeB = _fixture.ScopeFor(B))
        {
            var dbB = RecipeAggregateFixture.Db(scopeB);
            dbB.BrandSourceTags.Add(tagB);
            await dbB.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await AssertRefusedAsync(A, db => db.BrandSourceDocumentTags.Add(
            new BrandSourceDocumentTag { WorkspaceId = A, BrandSourceDocumentId = a.Document.Id, BrandSourceTagId = tagB.Id }));
    }

    [Fact]
    public async Task A_row_stamped_with_another_workspace_from_this_scope_is_refused()
    {
        var a = await SeedAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandSourceDocumentVersions.Add(NewVersion(B, a.Document.Id, number: 2));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unresolved_scope_cannot_read_documents()
    {
        await SeedAsync(A);

        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        await Assert.ThrowsAnyAsync<Exception>(() => db.BrandSourceDocuments.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_source_entity_is_workspace_owned_and_filtered()
    {
        using var scope = _fixture.UnresolvedScope();
        var model = RecipeAggregateFixture.Db(scope).Model;

        foreach (var type in new[]
                 {
                     typeof(BrandSourceDocument), typeof(BrandSourceDocumentVersion), typeof(BrandSourceExtraction),
                     typeof(BrandSourceTag), typeof(BrandSourceDocumentTag),
                 })
        {
            Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(type), $"{type.Name} is not IWorkspaceOwned");
            Assert.NotEmpty(model.FindEntityType(type)!.GetDeclaredQueryFilters());
        }
    }

    [Fact]
    public async Task A_version_cannot_be_edited_or_deleted()
    {
        await SeedAsync(A);
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var version = await db.BrandSourceDocumentVersions.SingleAsync(ct);
            version.ObjectKey = "brand-sources/overwritten";
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(ct));
        }

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.BrandSourceDocumentVersions.Remove(await db.BrandSourceDocumentVersions.SingleAsync(ct));
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(ct));
        }
    }

    [Fact]
    public async Task An_extraction_cannot_be_edited()
    {
        var a = await SeedAsync(A);
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.BrandSourceExtractions.Add(NewExtraction(A, a.Version.Id));
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var extraction = await db.BrandSourceExtractions.SingleAsync(ct);
            extraction.Reason = "rewritten";
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(ct));
        }
    }

    [Fact]
    public async Task Deleting_a_document_does_not_take_its_versions_with_it()
    {
        var a = await SeedAsync(A);
        var ct = TestContext.Current.CancellationToken;

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandSourceDocuments.Remove(await db.BrandSourceDocuments.SingleAsync(ct));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(ct));

        await using var verify = _fixture.ScopeFor(A);
        Assert.Equal(a.Version.Id, (await RecipeAggregateFixture.Db(verify).BrandSourceDocumentVersions.SingleAsync(ct)).Id);
    }

    [Fact]
    public async Task Version_numbers_and_object_keys_are_unique()
    {
        var a = await SeedAsync(A);

        await AssertRefusedAsync(A, db => db.BrandSourceDocumentVersions.Add(NewVersion(A, a.Document.Id, number: 1, objectKey: "brand-sources/other")));
        await AssertRefusedAsync(A, db => db.BrandSourceDocumentVersions.Add(NewVersion(A, a.Document.Id, number: 2, objectKey: a.Version.ObjectKey)));
    }

    [Theory]
    [InlineData("https://account.blob.core.windows.net/brand/doc")]
    [InlineData("   ")]
    public async Task An_object_key_that_is_a_url_or_blank_is_refused(string objectKey)
    {
        var a = await SeedAsync(A);

        await AssertRefusedAsync(A, db => db.BrandSourceDocumentVersions.Add(NewVersion(A, a.Document.Id, number: 2, objectKey: objectKey)));
    }

    [Fact]
    public async Task A_version_needs_bytes_and_a_sha256_checksum()
    {
        var a = await SeedAsync(A);

        await AssertRefusedAsync(A, db =>
        {
            var version = NewVersion(A, a.Document.Id, number: 2);
            version.SizeBytes = 0;
            db.BrandSourceDocumentVersions.Add(version);
        });
        await AssertRefusedAsync(A, db =>
        {
            var version = NewVersion(A, a.Document.Id, number: 2);
            version.ContentChecksum = "md5:abc";
            db.BrandSourceDocumentVersions.Add(version);
        });
    }

    [Fact]
    public async Task A_blank_title_or_unspecified_classification_is_refused()
    {
        await AssertRefusedAsync(A, db => db.BrandSourceDocuments.Add(NewDocument(A, "   ")));
        await AssertRefusedAsync(A, db =>
        {
            var document = NewDocument(A);
            document.DocumentType = 0;
            db.BrandSourceDocuments.Add(document);
        });
        await AssertRefusedAsync(A, db =>
        {
            var document = NewDocument(A);
            document.Purpose = 0;
            db.BrandSourceDocuments.Add(document);
        });
    }

    [Fact]
    public async Task Archived_and_removed_states_carry_their_facts_and_active_carries_none()
    {
        // Archived without a timestamp.
        await AssertRefusedAsync(A, db =>
        {
            var document = NewDocument(A);
            document.Status = BrandSourceDocumentStatus.Archived;
            db.BrandSourceDocuments.Add(document);
        });

        // Removed without saying when and by whom.
        await AssertRefusedAsync(A, db =>
        {
            var document = NewDocument(A);
            document.Status = BrandSourceDocumentStatus.Removed;
            db.BrandSourceDocuments.Add(document);
        });

        // A removal timestamp on a document that is not removed.
        await AssertRefusedAsync(A, db =>
        {
            var document = NewDocument(A);
            document.RemovedAt = Now;
            document.RemovedByMembershipId = Guid.NewGuid();
            db.BrandSourceDocuments.Add(document);
        });

        await using var scope = _fixture.ScopeFor(A);
        var ok = RecipeAggregateFixture.Db(scope);
        var removed = NewDocument(A);
        removed.Status = BrandSourceDocumentStatus.Removed;
        removed.ArchivedAt = Now;
        removed.RemovedAt = Now;
        removed.RemovedByMembershipId = Guid.NewGuid();
        ok.BrandSourceDocuments.Add(removed);
        await ok.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_extraction_has_text_exactly_when_it_succeeded()
    {
        var a = await SeedAsync(A);

        // Failed, yet pointing at text.
        await AssertRefusedAsync(A, db =>
        {
            var extraction = NewExtraction(A, a.Version.Id);
            extraction.Status = BrandSourceExtractionStatus.Failed;
            db.BrandSourceExtractions.Add(extraction);
        });

        // Succeeded, with nothing stored.
        await AssertRefusedAsync(A, db =>
        {
            var extraction = NewExtraction(A, a.Version.Id);
            extraction.ExtractedTextObjectKey = null;
            extraction.ContentChecksum = null;
            db.BrandSourceExtractions.Add(extraction);
        });

        // A correction nobody is named on.
        await AssertRefusedAsync(A, db =>
        {
            var extraction = NewExtraction(A, a.Version.Id);
            extraction.Origin = BrandSourceExtractionOrigin.Corrected;
            db.BrandSourceExtractions.Add(extraction);
        });

        // A URL where a private key belongs.
        await AssertRefusedAsync(A, db =>
        {
            var extraction = NewExtraction(A, a.Version.Id);
            extraction.ExtractedTextObjectKey = "https://example.com/text";
            db.BrandSourceExtractions.Add(extraction);
        });
    }

    [Fact]
    public async Task Failed_attempts_and_a_later_correction_stack_by_ordinal()
    {
        var a = await SeedAsync(A);
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);

            var unsupported = NewExtraction(A, a.Version.Id, ordinal: 1);
            unsupported.Status = BrandSourceExtractionStatus.Unsupported;
            unsupported.ExtractedTextObjectKey = null;
            unsupported.ContentChecksum = null;

            var failed = NewExtraction(A, a.Version.Id, ordinal: 2);
            failed.Status = BrandSourceExtractionStatus.Failed;
            failed.ExtractedTextObjectKey = null;
            failed.ContentChecksum = null;

            var corrected = NewExtraction(A, a.Version.Id, ordinal: 3);
            corrected.Origin = BrandSourceExtractionOrigin.Corrected;
            corrected.CreatedByMembershipId = Guid.NewGuid();

            // Two rows with no text do not collide on the object-key index.
            db.BrandSourceExtractions.AddRange(unsupported, failed, corrected);
            await db.SaveChangesAsync(ct);
        }

        await AssertRefusedAsync(A, db => db.BrandSourceExtractions.Add(NewExtraction(A, a.Version.Id, ordinal: 3)));
    }

    [Fact]
    public async Task A_tag_is_unique_by_normalized_name_within_a_workspace_and_once_per_document()
    {
        var a = await SeedAsync(A);
        var tag = NewTag(A);
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.BrandSourceTags.Add(tag);
            db.BrandSourceDocumentTags.Add(new BrandSourceDocumentTag { WorkspaceId = A, BrandSourceDocumentId = a.Document.Id, BrandSourceTagId = tag.Id });
            await db.SaveChangesAsync(ct);
        }

        await AssertRefusedAsync(A, db => db.BrandSourceTags.Add(NewTag(A, "LAUNCH")));
        await AssertRefusedAsync(A, db => db.BrandSourceDocumentTags.Add(
            new BrandSourceDocumentTag { WorkspaceId = A, BrandSourceDocumentId = a.Document.Id, BrandSourceTagId = tag.Id }));
    }

    [Fact]
    public async Task A_tag_still_on_a_document_cannot_be_deleted()
    {
        var a = await SeedAsync(A);
        var tag = NewTag(A);
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.BrandSourceTags.Add(tag);
            db.BrandSourceDocumentTags.Add(new BrandSourceDocumentTag { WorkspaceId = A, BrandSourceDocumentId = a.Document.Id, BrandSourceTagId = tag.Id });
            await db.SaveChangesAsync(ct);
        }

        await using var remove = _fixture.ScopeFor(A);
        var removeDb = RecipeAggregateFixture.Db(remove);
        removeDb.BrandSourceTags.Remove(await removeDb.BrandSourceTags.SingleAsync(ct));

        await Assert.ThrowsAnyAsync<Exception>(() => removeDb.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Ownership_of_a_document_cannot_be_changed_by_update()
    {
        await SeedAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);
        var document = await db.BrandSourceDocuments.SingleAsync(TestContext.Current.CancellationToken);
        document.WorkspaceId = B;

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Check constraints hard-code these values; renumbering an enum needs a migration.</summary>
    [Fact]
    public void The_enum_values_the_check_constraints_depend_on_are_pinned()
    {
        Assert.Equal(2, (int)BrandSourceDocumentStatus.Archived);
        Assert.Equal(3, (int)BrandSourceDocumentStatus.Removed);
        Assert.Equal(1, (int)BrandSourceExtractionStatus.Succeeded);
        Assert.Equal(2, (int)BrandSourceExtractionOrigin.Corrected);
    }

    /// <summary>SQL holds metadata and private pointers: no body, no extracted text, no address.</summary>
    [Fact]
    public void No_source_entity_carries_a_document_body_or_a_url()
    {
        string[] forbidden = ["body", "content", "text", "url", "uri", "link", "bytes"];
        string[] allowed =
        [
            nameof(BrandSourceDocumentVersion.SizeBytes), nameof(BrandSourceDocumentVersion.ContentChecksum),
            nameof(BrandSourceExtraction.ExtractedTextObjectKey),
        ];

        var offenders = new[] { typeof(BrandSourceDocument), typeof(BrandSourceDocumentVersion), typeof(BrandSourceExtraction) }
            .SelectMany(type => type.GetProperties().Select(p => (Type: type.Name, p.Name)))
            .Where(p => !allowed.Contains(p.Name))
            .Where(p => forbidden.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Type}.{p.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "document bodies and addresses stay out of SQL: " + string.Join(", ", offenders));
    }
}
