using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand style-guide schema and isolation, over the same two-workspace SQLite fixture the recipe aggregate
/// uses. Proves the EF configuration — section cardinality, immutability, the approved-only default, composite
/// keys, the query filter — not that SQL Server accepts the DDL, which the migration's own verification covers.
/// </summary>
public sealed class BrandStyleGuideAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid A = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid B = RecipeAggregateFixture.WorkspaceB;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static BrandStyleGuide NewGuide(Guid workspaceId, string name = "Everyday voice") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        DisplayName = name,
        CreatedAt = Now,
        UpdatedAt = Now,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
    };

    private static BrandStyleGuideVersion NewVersion(Guid workspaceId, Guid guideId, int number = 1, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandStyleGuideId = guideId,
        VersionNumber = number,
        ParentVersionId = parentId,
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now,
    };

    private static BrandStyleGuideSection NewSection(
        Guid workspaceId, Guid versionId, BrandStyleGuideSectionKey key = BrandStyleGuideSectionKey.Voice, string channelKey = "", string body = "Warm, plain, a little dry.") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandStyleGuideVersionId = versionId,
        SectionKey = key,
        ChannelKey = channelKey,
        Body = body,
    };

    private static BrandStyleGuideRule NewRule(Guid workspaceId, Guid versionId, int order = 0, string text = "Say \"you\", not \"one\".") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandStyleGuideVersionId = versionId,
        Kind = BrandStyleGuideRuleKind.Do,
        Text = text,
        SortOrder = order,
    };

    private static BrandStyleGuideApproval NewApproval(Guid workspaceId, Guid versionId) => new()
    {
        WorkspaceId = workspaceId,
        BrandStyleGuideVersionId = versionId,
        ApprovedByMembershipId = Guid.NewGuid(),
        ApprovedAt = Now,
    };

    private static BrandStyleGuideDefault NewDefault(Guid workspaceId, Guid versionId) => new()
    {
        WorkspaceId = workspaceId,
        BrandStyleGuideVersionId = versionId,
        ActivatedByMembershipId = Guid.NewGuid(),
        ActivatedAt = Now,
    };

    /// <summary>A guide with version 1 holding one section and one rule, saved from its own workspace's scope.</summary>
    private async Task<(BrandStyleGuide Guide, BrandStyleGuideVersion Version)> SeedAsync(Guid workspaceId, bool approved = false)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var guide = NewGuide(workspaceId);
        var version = NewVersion(workspaceId, guide.Id);
        version.Sections.Add(NewSection(workspaceId, version.Id));
        version.Rules.Add(NewRule(workspaceId, version.Id));
        db.BrandStyleGuides.Add(guide);
        db.BrandStyleGuideVersions.Add(version);
        if (approved)
        {
            db.BrandStyleGuideApprovals.Add(NewApproval(workspaceId, version.Id));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (guide, version);
    }

    private async Task<BrandSourceDocumentVersion> SeedSourceAsync(Guid workspaceId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
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
            SizeBytes = 1024,
            ContentChecksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            OriginalFileName = "house-style.pdf",
            ObjectKey = $"brand-sources/{document.Id:N}/1",
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };
        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(version);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return version;
    }

    private async Task SaveAsync(Guid workspaceId, Action<CreatorPantryDbContext> arrange)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        arrange(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertRefusedAsync(Guid workspaceId, Action<CreatorPantryDbContext> arrange)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        arrange(db);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_workspace_holds_its_own_guides_and_cannot_see_the_others()
    {
        // The same name, version number, section, rule order, approval and default in both.
        var a = await SeedAsync(A, approved: true);
        var b = await SeedAsync(B, approved: true);
        await SaveAsync(A, db => db.BrandStyleGuideDefaults.Add(NewDefault(A, a.Version.Id)));
        await SaveAsync(B, db => db.BrandStyleGuideDefaults.Add(NewDefault(B, b.Version.Id)));
        var ct = TestContext.Current.CancellationToken;

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);

        Assert.Equal(a.Guide.Id, (await db.BrandStyleGuides.SingleAsync(ct)).Id);
        Assert.Equal(a.Version.Id, (await db.BrandStyleGuideVersions.SingleAsync(ct)).Id);
        Assert.Equal(a.Version.Id, (await db.BrandStyleGuideSections.SingleAsync(ct)).BrandStyleGuideVersionId);
        Assert.Equal(a.Version.Id, (await db.BrandStyleGuideRules.SingleAsync(ct)).BrandStyleGuideVersionId);
        Assert.Equal(a.Version.Id, (await db.BrandStyleGuideApprovals.SingleAsync(ct)).BrandStyleGuideVersionId);
        Assert.Equal(a.Version.Id, (await db.BrandStyleGuideDefaults.SingleAsync(ct)).BrandStyleGuideVersionId);
        Assert.Null(await db.BrandStyleGuides.FirstOrDefaultAsync(g => g.Id == b.Guide.Id, ct));
        Assert.Null(await db.BrandStyleGuideVersions.FirstOrDefaultAsync(v => v.Id == b.Version.Id, ct));
    }

    [Fact]
    public async Task Children_cannot_point_at_another_workspaces_guide_or_version()
    {
        var b = await SeedAsync(B, approved: true);

        await AssertRefusedAsync(A, db => db.BrandStyleGuideVersions.Add(NewVersion(A, b.Guide.Id, number: 2)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, b.Version.Id, BrandStyleGuideSectionKey.Tone)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideRules.Add(NewRule(A, b.Version.Id, order: 1)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideApprovals.Add(NewApproval(A, b.Version.Id)));
    }

    [Fact]
    public async Task A_workspace_cannot_default_to_another_workspaces_approved_version()
    {
        var b = await SeedAsync(B, approved: true);

        await AssertRefusedAsync(A, db => db.BrandStyleGuideDefaults.Add(NewDefault(A, b.Version.Id)));
    }

    [Fact]
    public async Task A_guide_version_cannot_cite_another_workspaces_source_document()
    {
        var a = await SeedAsync(A);
        var sourceB = await SeedSourceAsync(B);
        var draft = NewVersion(A, a.Guide.Id, number: 2, parentId: a.Version.Id);
        draft.SourceLinks.Add(new BrandStyleGuideSourceLink { WorkspaceId = A, BrandStyleGuideVersionId = draft.Id, BrandSourceDocumentVersionId = sourceB.Id });

        await AssertRefusedAsync(A, db => db.BrandStyleGuideVersions.Add(draft));
    }

    [Fact]
    public async Task A_row_stamped_with_another_workspace_from_this_scope_is_refused()
    {
        var a = await SeedAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandStyleGuideVersions.Add(NewVersion(B, a.Guide.Id, number: 2));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unresolved_scope_cannot_read_guides()
    {
        await SeedAsync(A);

        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        await Assert.ThrowsAnyAsync<Exception>(() => db.BrandStyleGuides.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_guide_entity_is_workspace_owned_and_filtered()
    {
        using var scope = _fixture.UnresolvedScope();
        var model = RecipeAggregateFixture.Db(scope).Model;

        foreach (var type in new[]
                 {
                     typeof(BrandStyleGuide), typeof(BrandStyleGuideVersion), typeof(BrandStyleGuideSection), typeof(BrandStyleGuideRule),
                     typeof(BrandStyleGuideSourceLink), typeof(BrandStyleGuideApproval), typeof(BrandStyleGuideDefault),
                 })
        {
            Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(type), $"{type.Name} is not IWorkspaceOwned");
            Assert.NotEmpty(model.FindEntityType(type)!.GetDeclaredQueryFilters());
        }
    }

    [Fact]
    public async Task A_version_holds_one_section_per_key_and_one_channel_variant_per_channel()
    {
        var a = await SeedAsync(A);
        var id = a.Version.Id;

        // A different key, and the same variant key for two channels: both fine.
        await SaveAsync(A, db => db.BrandStyleGuideSections.AddRange(
            NewSection(A, id, BrandStyleGuideSectionKey.Tone),
            NewSection(A, id, BrandStyleGuideSectionKey.ChannelVariant, "instagram"),
            NewSection(A, id, BrandStyleGuideSectionKey.ChannelVariant, "newsletter")));

        // A second voice section, and a second variant for one channel.
        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, id)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, id, BrandStyleGuideSectionKey.ChannelVariant, "instagram")));
    }

    [Fact]
    public async Task Only_a_channel_variant_names_a_channel_and_no_section_is_blank()
    {
        var a = await SeedAsync(A);
        var id = a.Version.Id;

        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, id, BrandStyleGuideSectionKey.ChannelVariant)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, id, BrandStyleGuideSectionKey.Tone, "instagram")));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, id, BrandStyleGuideSectionKey.Tone, body: "   ")));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideSections.Add(NewSection(A, id, key: 0)));
    }

    [Fact]
    public async Task Rules_are_ordered_uniquely_and_never_blank()
    {
        var a = await SeedAsync(A);
        var id = a.Version.Id;

        await SaveAsync(A, db => db.BrandStyleGuideRules.Add(NewRule(A, id, order: 1)));

        await AssertRefusedAsync(A, db => db.BrandStyleGuideRules.Add(NewRule(A, id, order: 1)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideRules.Add(NewRule(A, id, order: 2, text: "  ")));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideRules.Add(NewRule(A, id, order: -1)));
    }

    [Fact]
    public async Task A_version_and_everything_under_it_cannot_be_edited_or_deleted()
    {
        var a = await SeedAsync(A, approved: true);
        var ct = TestContext.Current.CancellationToken;

        async Task AssertImmutableAsync(Func<CreatorPantryDbContext, Task> change)
        {
            await using var scope = _fixture.ScopeFor(A);
            var db = RecipeAggregateFixture.Db(scope);
            await change(db);
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(ct));
        }

        await AssertImmutableAsync(async db => (await db.BrandStyleGuideVersions.SingleAsync(ct)).ChangeReason = "rewritten");
        await AssertImmutableAsync(async db => (await db.BrandStyleGuideSections.SingleAsync(ct)).Body = "rewritten");
        await AssertImmutableAsync(async db => (await db.BrandStyleGuideRules.SingleAsync(ct)).Text = "rewritten");
        await AssertImmutableAsync(async db => (await db.BrandStyleGuideApprovals.SingleAsync(ct)).Reason = "rewritten");
        await AssertImmutableAsync(async db => db.BrandStyleGuideSections.Remove(await db.BrandStyleGuideSections.SingleAsync(ct)));
        await AssertImmutableAsync(async db => db.BrandStyleGuideApprovals.Remove(await db.BrandStyleGuideApprovals.SingleAsync(ct)));
        await AssertImmutableAsync(async db => db.BrandStyleGuideVersions.Remove(await db.BrandStyleGuideVersions.SingleAsync(ct)));
    }

    [Fact]
    public async Task Deleting_a_guide_does_not_take_its_versions_with_it()
    {
        var a = await SeedAsync(A);
        var ct = TestContext.Current.CancellationToken;

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandStyleGuides.Remove(await db.BrandStyleGuides.SingleAsync(ct));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(ct));

        await using var verify = _fixture.ScopeFor(A);
        Assert.Equal(a.Version.Id, (await RecipeAggregateFixture.Db(verify).BrandStyleGuideVersions.SingleAsync(ct)).Id);
    }

    [Fact]
    public async Task Version_numbers_are_unique_and_a_parent_is_an_earlier_version_of_the_same_guide()
    {
        var a = await SeedAsync(A);
        var other = await SeedAsync(A);

        await SaveAsync(A, db => db.BrandStyleGuideVersions.Add(NewVersion(A, a.Guide.Id, number: 2, parentId: a.Version.Id)));

        // The number is taken; version 1 has no parent; a parent from another guide is not this guide's.
        await AssertRefusedAsync(A, db => db.BrandStyleGuideVersions.Add(NewVersion(A, a.Guide.Id, number: 2, parentId: a.Version.Id)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideVersions.Add(NewVersion(A, NewGuideIdFor(db), number: 1, parentId: a.Version.Id)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideVersions.Add(NewVersion(A, a.Guide.Id, number: 3, parentId: other.Version.Id)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideVersions.Add(NewVersion(A, a.Guide.Id, number: 0)));

        static Guid NewGuideIdFor(CreatorPantryDbContext db)
        {
            var guide = NewGuide(A, "Holiday");
            db.BrandStyleGuides.Add(guide);
            return guide.Id;
        }
    }

    [Fact]
    public async Task A_version_is_approved_at_most_once()
    {
        var a = await SeedAsync(A, approved: true);

        await AssertRefusedAsync(A, db => db.BrandStyleGuideApprovals.Add(NewApproval(A, a.Version.Id)));
    }

    [Fact]
    public async Task Only_an_approved_version_can_be_the_default_and_a_workspace_has_one()
    {
        var draft = await SeedAsync(A);
        var approved = await SeedAsync(A, approved: true);
        var alsoApproved = await SeedAsync(A, approved: true);
        var ct = TestContext.Current.CancellationToken;

        await AssertRefusedAsync(A, db => db.BrandStyleGuideDefaults.Add(NewDefault(A, draft.Version.Id)));

        await SaveAsync(A, db => db.BrandStyleGuideDefaults.Add(NewDefault(A, approved.Version.Id)));
        await AssertRefusedAsync(A, db => db.BrandStyleGuideDefaults.Add(NewDefault(A, alsoApproved.Version.Id)));

        // Activation repoints the one row; pointing it at a draft is still refused.
        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            (await db.BrandStyleGuideDefaults.SingleAsync(ct)).BrandStyleGuideVersionId = alsoApproved.Version.Id;
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = _fixture.ScopeFor(A))
        {
            var db = RecipeAggregateFixture.Db(scope);
            (await db.BrandStyleGuideDefaults.SingleAsync(ct)).BrandStyleGuideVersionId = draft.Version.Id;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        }
    }

    [Fact]
    public async Task A_source_link_pins_one_document_version_once()
    {
        var a = await SeedAsync(A);
        var source = await SeedSourceAsync(A);
        var draft = NewVersion(A, a.Guide.Id, number: 2, parentId: a.Version.Id);
        draft.SourceLinks.Add(new BrandStyleGuideSourceLink { WorkspaceId = A, BrandStyleGuideVersionId = draft.Id, BrandSourceDocumentVersionId = source.Id });
        var ct = TestContext.Current.CancellationToken;

        await SaveAsync(A, db => db.BrandStyleGuideVersions.Add(draft));

        await using (var scope = _fixture.ScopeFor(A))
        {
            var link = await RecipeAggregateFixture.Db(scope).BrandStyleGuideSourceLinks.SingleAsync(ct);
            Assert.Equal(source.Id, link.BrandSourceDocumentVersionId);
        }

        await AssertRefusedAsync(A, db => db.BrandStyleGuideSourceLinks.Add(
            new BrandStyleGuideSourceLink { WorkspaceId = A, BrandStyleGuideVersionId = draft.Id, BrandSourceDocumentVersionId = source.Id }));
    }

    [Fact]
    public async Task A_blank_name_or_an_archive_with_no_timestamp_is_refused()
    {
        await AssertRefusedAsync(A, db => db.BrandStyleGuides.Add(NewGuide(A, "   ")));
        await AssertRefusedAsync(A, db =>
        {
            var guide = NewGuide(A);
            guide.Status = BrandStyleGuideStatus.Archived;
            db.BrandStyleGuides.Add(guide);
        });
    }

    [Fact]
    public async Task Ownership_of_a_guide_cannot_be_changed_by_update()
    {
        await SeedAsync(A);

        await using var scope = _fixture.ScopeFor(A);
        var db = RecipeAggregateFixture.Db(scope);
        var guide = await db.BrandStyleGuides.SingleAsync(TestContext.Current.CancellationToken);
        guide.WorkspaceId = B;

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Section keys drive comparison and partial acceptance, and check constraints hard-code two of these
    /// values; renumbering any of them needs a migration.
    /// </summary>
    [Fact]
    public void The_enum_values_stored_rows_and_check_constraints_depend_on_are_pinned()
    {
        Assert.Equal(12, (int)BrandStyleGuideSectionKey.ChannelVariant);
        Assert.Equal(2, (int)BrandStyleGuideStatus.Archived);
        Assert.Equal(
            Enumerable.Range(1, 19),
            Enum.GetValues<BrandStyleGuideSectionKey>().Select(key => (int)key).Order());
    }

    /// <summary>A guide is the creator's words in keyed sections, never a stored provider prompt.</summary>
    [Fact]
    public void No_guide_entity_carries_a_prompt_or_a_document_blob()
    {
        string[] forbidden = ["prompt", "document", "json", "template", "system"];

        var offenders = new[]
            {
                typeof(BrandStyleGuide), typeof(BrandStyleGuideVersion), typeof(BrandStyleGuideSection), typeof(BrandStyleGuideRule),
                typeof(BrandStyleGuideApproval), typeof(BrandStyleGuideDefault),
            }
            .SelectMany(type => type.GetProperties().Select(p => (Type: type.Name, p.Name)))
            .Where(p => forbidden.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Type}.{p.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "guide content is structured source data: " + string.Join(", ", offenders));
    }
}
