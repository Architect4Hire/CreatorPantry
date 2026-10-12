using CreatorPantry.Domain.Modules.Content.Business;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.1.2 against a real SQL Server: the long-form draft, its immutable revisions and their recipe pins, as the
/// migration built them.
/// </summary>
/// <remarks>
/// There is no write seam yet — that arrives with LF.2.2 — so these tests write rows directly and assert what
/// the model and the engine refuse. SQL Server rather than SQLite because the migration is what is under test:
/// three tables that all reach one workspace, a cycle of restricted keys between a draft and its revisions, and
/// a row version that has to move. The fixture is shared, so every assertion is scoped to rows the test made.
/// </remarks>
public sealed class LongFormDraftTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The shape ----

    [Fact]
    public async Task A_document_round_trips_with_its_plan_its_words_and_every_recipe_it_was_written_against()
    {
        var contextId = await SeedContextAsync(WsA);
        var first = await SeedRecipeAsync(WsA);
        var second = await SeedRecipeAsync(WsA);
        var firstReference = Guid.NewGuid();
        var secondReference = Guid.NewGuid();

        var document = new LongFormDocument(
        [
            new(Guid.NewGuid(), LongFormSectionRole.Greeting, "Hello again", "Open warmly.", "Hello, friends.", true, null,
                new(ContentRevisionSource.CreatorEdit, null, null, null, null, null, 1)),
            new(Guid.NewGuid(), LongFormSectionRole.Item, "Soda bread", "The first recipe.", null, false, firstReference, null),
            new(Guid.NewGuid(), LongFormSectionRole.Item, "Brown butter loaf", "The second recipe.", null, false, secondReference, null),
        ]);

        var draft = NewDraft(contextId, LongFormKind.NewsletterIssue);
        var revision = NewRevision(draft, 1, parent: null, document);

        await InAsync(WsA, async db =>
        {
            db.AddRange(draft, revision);
            db.AddRange(
                Pin(revision, 0, first, firstReference),
                Pin(revision, 1, second, secondReference));
            await db.SaveChangesAsync(Ct);
        });

        await InAsync(WsA, async db =>
        {
            var loadedDraft = await db.LongFormDrafts.AsNoTracking().SingleAsync(item => item.Id == draft.Id, Ct);
            var loaded = await db.LongFormRevisions.AsNoTracking().SingleAsync(item => item.LongFormDraftId == draft.Id, Ct);
            var pins = await db.LongFormRevisionRecipePins.AsNoTracking()
                .Where(pin => pin.LongFormRevisionId == loaded.Id)
                .OrderBy(pin => pin.SortOrder)
                .ToListAsync(Ct);

            // Stamped by the ownership interceptor from the resolved context, never set by hand.
            Assert.Equal(WsA, loadedDraft.WorkspaceId);
            Assert.Equal(WsA, loaded.WorkspaceId);
            Assert.All(pins, pin => Assert.Equal(WsA, pin.WorkspaceId));

            Assert.Equal(LongFormDraftStatus.Draft, loadedDraft.Status);
            Assert.Null(loadedDraft.AcceptedRevisionId);
            Assert.Equal([first.VersionId, second.VersionId], pins.Select(pin => pin.RecipeVersionId));

            var read = LongFormDocument.Deserialize(loaded.Content);
            Assert.Equal(document.Sections, read.Sections);
        });
    }

    [Fact]
    public void A_section_role_is_stored_as_its_number()
    {
        var content = new LongFormDocument(
        [
            new(Guid.NewGuid(), LongFormSectionRole.WhyItWorks, "Why this works", "Explain the method.", null, false, null, null),
        ]).Serialize();

        using var json = JsonDocument.Parse(content);
        var role = json.RootElement.GetProperty("sections")[0].GetProperty("role");

        Assert.Equal(JsonValueKind.Number, role.ValueKind);
        Assert.Equal((int)LongFormSectionRole.WhyItWorks, role.GetInt32());
    }

    [Fact]
    public async Task A_context_may_hold_several_drafts_of_the_same_kind()
    {
        var contextId = await SeedContextAsync(WsA);

        await InAsync(WsA, async db =>
        {
            db.AddRange(
                NewDraft(contextId, LongFormKind.NewsletterIssue),
                NewDraft(contextId, LongFormKind.NewsletterIssue),
                NewDraft(contextId, LongFormKind.BlogArticle));
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(3, await InAsync(WsA, db => db.LongFormDrafts.CountAsync(draft => draft.CreativeContextId == contextId, Ct)));
    }

    // ---- Immutability ----

    [Fact]
    public async Task A_revision_and_its_pins_can_be_neither_rewritten_nor_deleted()
    {
        var (_, revision, pin) = await SeedDraftWithPinAsync(WsA);

        await AssertRefusedAsync(async db =>
            (await db.LongFormRevisions.SingleAsync(item => item.Id == revision.Id, Ct)).Content = "{\"sections\":[]}");
        await AssertRefusedAsync(async db =>
            (await db.LongFormRevisions.SingleAsync(item => item.Id == revision.Id, Ct)).CreativeContextVersion = "AAAAAAAAB9E=");
        await AssertRefusedAsync(async db =>
            (await db.LongFormRevisions.SingleAsync(item => item.Id == revision.Id, Ct)).PromptTemplateVersion = "9.9.9");
        await AssertRefusedAsync(async db =>
            db.LongFormRevisions.Remove(await db.LongFormRevisions.SingleAsync(item => item.Id == revision.Id, Ct)));
        await AssertRefusedAsync(async db =>
            (await db.LongFormRevisionRecipePins.SingleAsync(item => item.Id == pin.Id, Ct)).SortOrder = 5);
        await AssertRefusedAsync(async db =>
            db.LongFormRevisionRecipePins.Remove(await db.LongFormRevisionRecipePins.SingleAsync(item => item.Id == pin.Id, Ct)));

        var stored = await InAsync(WsA, db => db.LongFormRevisions.AsNoTracking().SingleAsync(item => item.Id == revision.Id, Ct));
        Assert.Equal(revision.Content, stored.Content);

        async Task AssertRefusedAsync(Func<CreatorPantryDbContext, Task> change) =>
            await InAsync(WsA, async db =>
            {
                await change(db);
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            });
    }

    [Fact]
    public async Task An_edit_is_a_new_revision_and_the_one_before_it_reads_exactly_as_it_did()
    {
        var (draft, first, _) = await SeedDraftWithPinAsync(WsA);
        var edited = NewRevision(draft, 2, first, Document("Rewritten by the creator."));

        await InAsync(WsA, async db =>
        {
            db.Add(edited);
            await db.SaveChangesAsync(Ct);
        });

        var history = await InAsync(WsA, db => db.LongFormRevisions.AsNoTracking()
            .Where(revision => revision.LongFormDraftId == draft.Id)
            .OrderBy(revision => revision.RevisionNumber)
            .ToListAsync(Ct));

        Assert.Equal([1, 2], history.Select(revision => revision.RevisionNumber));
        Assert.Equal(first.Content, history[0].Content);
        Assert.Equal(first.Id, history[1].ParentRevisionId);
    }

    // ---- Revision numbering ----

    [Theory]
    [InlineData("repeated-number")]
    [InlineData("zero")]
    [InlineData("first-with-parent")]
    [InlineData("later-without-parent")]
    [InlineData("own-parent")]
    [InlineData("parent-from-another-draft")]
    [InlineData("reaffirmed-first")]
    public async Task The_engine_refuses_a_revision_whose_number_or_parent_is_wrong(string fault)
    {
        var (draft, first, _) = await SeedDraftWithPinAsync(WsA);
        var (_, otherFirst, _) = await SeedDraftWithPinAsync(WsA);

        // The two faults about revision 1 open a fresh draft: on this one the unique index would answer first.
        var revision = fault switch
        {
            "repeated-number" => NewRevision(draft, 1, parent: null, Document()),
            "zero" => NewRevision(draft, 0, parent: null, Document()),
            "later-without-parent" => NewRevision(draft, 2, parent: null, Document()),
            "own-parent" => NewRevision(draft, 2, first, Document()),
            "parent-from-another-draft" => NewRevision(draft, 2, otherFirst, Document()),
            _ => NewRevision(await SeedEmptyDraftAsync(WsA), 1, parent: null, Document()),
        };

        switch (fault)
        {
            case "first-with-parent":
                revision.ParentRevisionId = first.Id;
                break;
            case "own-parent":
                revision.ParentRevisionId = revision.Id;
                break;
            case "reaffirmed-first":
                revision.Source = ContentRevisionSource.Reaffirmed;
                break;
        }

        await AssertEngineRefusesAsync(WsA, revision);
    }

    // ---- Pin requirements ----

    [Theory]
    [InlineData("generated-without-proposal")]
    [InlineData("edit-with-proposal")]
    [InlineData("unknown-brand-revision")]
    [InlineData("unknown-style-guide")]
    [InlineData("blank-content")]
    [InlineData("schema-zero")]
    public async Task The_engine_refuses_a_revision_whose_provenance_does_not_hold_together(string fault)
    {
        var draft = await SeedEmptyDraftAsync(WsA);
        var revision = NewRevision(draft, 1, parent: null, Document());

        switch (fault)
        {
            case "generated-without-proposal":
                revision.Source = ContentRevisionSource.AiGenerated;
                revision.PromptTemplateId = "content.long-form-section";
                revision.PromptTemplateVersion = "1.0.0";
                revision.PromptTemplateBodyChecksum = "sha256:seed";
                break;
            case "edit-with-proposal":
                revision.AiProposalId = Guid.NewGuid();
                break;
            case "unknown-brand-revision":
                revision.BrandProfileRevisionId = Guid.NewGuid();
                break;
            case "unknown-style-guide":
                revision.BrandStyleGuideVersionId = Guid.NewGuid();
                break;
            case "blank-content":
                revision.Content = "   ";
                break;
            case "schema-zero":
                revision.SchemaVersion = 0;
                break;
        }

        await AssertEngineRefusesAsync(WsA, revision);
    }

    [Fact]
    public async Task A_recipe_pin_names_one_version_of_its_own_recipe_once()
    {
        var (_, revision, pin) = await SeedDraftWithPinAsync(WsA);
        var other = await SeedRecipeAsync(WsA);

        // Another recipe's version under this recipe's id.
        await AssertEngineRefusesAsync(WsA, new LongFormRevisionRecipePin
        {
            Id = Guid.NewGuid(),
            LongFormRevisionId = revision.Id,
            SortOrder = 1,
            RecipeId = pin.RecipeId,
            RecipeVersionId = other.VersionId,
            CreativeContextReferenceId = Guid.NewGuid(),
        });

        // The same recipe twice, and the same position twice.
        await AssertEngineRefusesAsync(
            WsA, Pin(revision, 1, (pin.RecipeId, pin.RecipeVersionId), Guid.NewGuid()));
        await AssertEngineRefusesAsync(WsA, Pin(revision, 0, other, Guid.NewGuid()));

        // A revision that does not exist.
        await AssertEngineRefusesAsync(WsA, new LongFormRevisionRecipePin
        {
            Id = Guid.NewGuid(),
            LongFormRevisionId = Guid.NewGuid(),
            SortOrder = 0,
            RecipeId = other.RecipeId,
            RecipeVersionId = other.VersionId,
            CreativeContextReferenceId = Guid.NewGuid(),
        });
    }

    [Theory]
    [InlineData("accepted-without-revision")]
    [InlineData("accepted-without-instant")]
    [InlineData("another-drafts-revision")]
    [InlineData("stale-while-accepted")]
    [InlineData("needs-review-without-reason")]
    [InlineData("status-out-of-range")]
    [InlineData("kind-out-of-range")]
    public async Task The_engine_refuses_a_draft_whose_state_does_not_hold_together(string fault)
    {
        var (draft, revision, _) = await SeedDraftWithPinAsync(WsA);
        var (_, foreign, _) = await SeedDraftWithPinAsync(WsA);

        await InAsync(WsA, async db =>
        {
            var tracked = await db.LongFormDrafts.SingleAsync(item => item.Id == draft.Id, Ct);
            tracked.UpdatedAt = Now.AddMinutes(1);

            switch (fault)
            {
                case "accepted-without-revision":
                    tracked.Status = LongFormDraftStatus.Accepted;
                    break;
                case "accepted-without-instant":
                    tracked.Status = LongFormDraftStatus.Accepted;
                    tracked.AcceptedRevisionId = revision.Id;
                    break;
                case "another-drafts-revision":
                    tracked.Status = LongFormDraftStatus.Accepted;
                    tracked.AcceptedRevisionId = foreign.Id;
                    tracked.AcceptedAt = Now;
                    break;
                case "stale-while-accepted":
                    tracked.Status = LongFormDraftStatus.Accepted;
                    tracked.AcceptedRevisionId = revision.Id;
                    tracked.AcceptedAt = Now;
                    tracked.StaleSince = Now;
                    tracked.StaleReasons = ContentStaleReasons.RecipeChanged;
                    break;
                case "needs-review-without-reason":
                    tracked.Status = LongFormDraftStatus.NeedsReview;
                    tracked.AcceptedRevisionId = revision.Id;
                    tracked.AcceptedAt = Now;
                    tracked.StaleSince = Now;
                    break;
                case "status-out-of-range":
                    tracked.Status = (LongFormDraftStatus)6;
                    break;
                case "kind-out-of-range":
                    tracked.Kind = (LongFormKind)2;
                    break;
            }

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });
    }

    [Fact]
    public async Task An_accepted_draft_goes_stale_and_keeps_the_revision_that_was_accepted()
    {
        var (draft, revision, _) = await SeedDraftWithPinAsync(WsA);

        await InAsync(WsA, async db =>
        {
            var tracked = await db.LongFormDrafts.SingleAsync(item => item.Id == draft.Id, Ct);
            tracked.Status = LongFormDraftStatus.Accepted;
            tracked.AcceptedRevisionId = revision.Id;
            tracked.AcceptedAt = Now;
            await db.SaveChangesAsync(Ct);

            tracked.Status = LongFormDraftStatus.NeedsReview;
            tracked.StaleSince = Now.AddDays(1);
            tracked.StaleReasons = ContentStaleReasons.RecipeChanged | ContentStaleReasons.TemplateChanged;
            await db.SaveChangesAsync(Ct);
        });

        var stored = await InAsync(WsA, db => db.LongFormDrafts.AsNoTracking().SingleAsync(item => item.Id == draft.Id, Ct));
        Assert.Equal(LongFormDraftStatus.NeedsReview, stored.Status);
        Assert.Equal(revision.Id, stored.AcceptedRevisionId);
        Assert.Equal(ContentStaleReasons.RecipeChanged | ContentStaleReasons.TemplateChanged, stored.StaleReasons);
    }

    // ---- A dangling context reference ----

    [Fact]
    public async Task A_reference_the_context_later_loses_does_not_break_a_revision_that_pinned_it()
    {
        var contextId = await SeedContextAsync(WsA);
        var recipe = await SeedRecipeAsync(WsA);
        var referenceId = Guid.NewGuid();

        await InAsync(WsA, async db =>
        {
            db.CreativeContextReferences.Add(new CreativeContextReference
            {
                Id = referenceId,
                CreativeContextId = contextId,
                Kind = CreativeContextReferenceKind.Recipe,
                SortOrder = 0,
                RecipeId = recipe.RecipeId,
                RecipeVersionId = recipe.VersionId,
                AddedAt = Now,
            });
            await db.SaveChangesAsync(Ct);
        });

        var draft = NewDraft(contextId, LongFormKind.BlogArticle);
        var revision = NewRevision(draft, 1, parent: null, Document());

        await InAsync(WsA, async db =>
        {
            db.AddRange(draft, revision, Pin(revision, 0, recipe, referenceId));
            await db.SaveChangesAsync(Ct);
        });

        // The context drops the recipe. Nothing about the revision holds the reference row in place.
        await InAsync(WsA, async db =>
        {
            db.CreativeContextReferences.Remove(await db.CreativeContextReferences.SingleAsync(item => item.Id == referenceId, Ct));
            await db.SaveChangesAsync(Ct);
        });

        var pin = await InAsync(WsA, db => db.LongFormRevisionRecipePins.AsNoTracking()
            .SingleAsync(item => item.LongFormRevisionId == revision.Id, Ct));

        Assert.Equal(referenceId, pin.CreativeContextReferenceId);
        Assert.Equal(recipe.RecipeId, pin.RecipeId);
        Assert.Equal(recipe.VersionId, pin.RecipeVersionId);
        Assert.Equal(
            revision.Content,
            (await InAsync(WsA, db => db.LongFormRevisions.AsNoTracking().SingleAsync(item => item.Id == revision.Id, Ct))).Content);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task A_stale_write_to_a_draft_is_refused_and_the_row_version_moves()
    {
        var draft = await SeedEmptyDraftAsync(WsA);

        await using var firstScope = fixture.ScopeFor(WsA);
        await using var secondScope = fixture.ScopeFor(WsA);
        var firstDb = SqlServerRecipeFixture.Db(firstScope);
        var secondDb = SqlServerRecipeFixture.Db(secondScope);

        var first = await firstDb.LongFormDrafts.SingleAsync(item => item.Id == draft.Id, Ct);
        var second = await secondDb.LongFormDrafts.SingleAsync(item => item.Id == draft.Id, Ct);
        var readWith = first.RowVersion.ToArray();

        first.Status = LongFormDraftStatus.Outlined;
        await firstDb.SaveChangesAsync(Ct);

        Assert.NotEqual(readWith, first.RowVersion);

        // Read before the first write landed, so its UPDATE quotes a version the row no longer has.
        second.Status = LongFormDraftStatus.Writing;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));

        Assert.Equal(
            LongFormDraftStatus.Outlined,
            (await InAsync(WsA, db => db.LongFormDrafts.AsNoTracking().SingleAsync(item => item.Id == draft.Id, Ct))).Status);
    }

    /// <summary>
    /// LF.2.2's write seam on the engine whose row version really moves — which the HTTP fixture's SQLite store
    /// cannot show: every write hands back a new token, and a writer still holding an old one is refused.
    /// </summary>
    [Fact]
    public async Task Each_write_through_the_seam_moves_the_token_and_a_writer_holding_an_old_one_is_refused()
    {
        var (seeded, _, _) = await SeedDraftWithPinAsync(WsA);

        PatchLongFormPlanViewModel Edit(string token, string heading) => new()
        {
            ExpectedConcurrencyToken = token,
            Sections = [new LongFormPlanSectionInputViewModel { Role = LongFormSectionRole.Hook, Heading = heading, Intent = "Open." }],
        };

        var read = await SeamAsync(facade => facade.GetAsync(seeded.Id, Ct));
        var firstWrite = await SeamAsync(facade => facade.PatchPlanAsync("user-1", seeded.Id, Edit(read.Value!.ConcurrencyToken, "First writer"), Ct));

        Assert.True(firstWrite.Succeeded, firstWrite.Error?.Code);
        Assert.NotEqual(read.Value!.ConcurrencyToken, firstWrite.Value!.ConcurrencyToken);
        Assert.Equal(2, firstWrite.Value.Latest!.RevisionNumber);

        // Composed against the read before the first write landed.
        var secondWrite = await SeamAsync(facade => facade.PatchPlanAsync("user-2", seeded.Id, Edit(read.Value.ConcurrencyToken, "Second writer"), Ct));
        var staleApproval = await SeamAsync(facade => facade.ApprovePlanAsync(
            "user-2",
            seeded.Id,
            new ApproveLongFormPlanViewModel { ExpectedConcurrencyToken = read.Value.ConcurrencyToken, RevisionNumber = 2 },
            Ct));

        Assert.Equal(ContentErrorCodes.LongFormDraftStale, secondWrite.Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, staleApproval.Error!.Code);

        var approved = await SeamAsync(facade => facade.ApprovePlanAsync(
            "user-1",
            seeded.Id,
            new ApproveLongFormPlanViewModel { ExpectedConcurrencyToken = firstWrite.Value.ConcurrencyToken, RevisionNumber = 2 },
            Ct));

        Assert.Equal(LongFormDraftStatus.Outlined, approved.Value!.Status);
        Assert.NotEqual(firstWrite.Value.ConcurrencyToken, approved.Value.ConcurrencyToken);

        // The retry after a lost response still holds the token the approval moved. It is the same approval.
        var retried = await SeamAsync(facade => facade.ApprovePlanAsync(
            "user-1",
            seeded.Id,
            new ApproveLongFormPlanViewModel { ExpectedConcurrencyToken = firstWrite.Value.ConcurrencyToken, RevisionNumber = 2 },
            Ct));

        Assert.True(retried.Succeeded, retried.Error?.Code);
        Assert.Equal(approved.Value.ConcurrencyToken, retried.Value!.ConcurrencyToken);

        await InAsync(WsA, async db =>
        {
            var stored = await db.LongFormRevisions.AsNoTracking()
                .Where(revision => revision.LongFormDraftId == seeded.Id)
                .OrderBy(revision => revision.RevisionNumber)
                .ToListAsync(Ct);

            // Two revisions, the first writer's last, each pinned to the recipe the first was written against.
            Assert.Equal(2, stored.Count);
            Assert.Contains("First writer", stored[1].Content, StringComparison.Ordinal);
            Assert.Equal(1, await db.LongFormRevisionRecipePins.CountAsync(pin => pin.LongFormRevisionId == stored[1].Id, Ct));

            var resourceId = seeded.Id.ToString("D");
            Assert.Equal(1, await db.AuditLogs.CountAsync(
                entry => entry.ResourceId == resourceId && entry.Action == ContentAuditActions.LongFormPlanApproved, Ct));
        });
    }

    private async Task<OperationResult<LongFormDraftServiceModel>> SeamAsync(
        Func<ILongFormDraftFacade, Task<OperationResult<LongFormDraftServiceModel>>> ask)
    {
        await using var scope = fixture.ScopeFor(WsA);

        return await ask(scope.ServiceProvider.GetRequiredService<ILongFormDraftFacade>());
    }

    /// <summary>
    /// The same, for a section (LF.3.3): an edit and a settled flag each move the token, a writer holding the
    /// old one is refused whichever of the two it tries, and the pins travel with every new revision.
    /// </summary>
    [Fact]
    public async Task A_section_edit_and_a_settlement_each_move_the_token_and_a_stale_writer_is_refused()
    {
        var (seeded, _, _) = await SeedDraftWithPinAsync(WsA);

        await InAsync(WsA, async db =>
        {
            var row = await db.LongFormDrafts.SingleAsync(draft => draft.Id == seeded.Id, Ct);
            row.Status = LongFormDraftStatus.Outlined;
            await db.SaveChangesAsync(Ct);
        });

        var read = await SectionsAsync(facade => facade.GetSectionsAsync(seeded.Id, Ct));
        var section = read.Value!.Sections[0].Id;

        var edited = await SectionsAsync(facade => facade.EditSectionAsync(
            "user-1",
            seeded.Id,
            section,
            new EditLongFormSectionViewModel { ExpectedConcurrencyToken = read.Value.ConcurrencyToken, Body = "First writer's text." },
            Ct));

        Assert.True(edited.Succeeded, edited.Error?.Code);
        Assert.NotEqual(read.Value.ConcurrencyToken, edited.Value!.ConcurrencyToken);
        Assert.Equal(2, edited.Value.RevisionNumber);
        Assert.Equal(LongFormDraftStatus.Writing, edited.Value.Status);

        // Composed against the read before the first write landed.
        var staleEdit = await SectionsAsync(facade => facade.EditSectionAsync(
            "user-2",
            seeded.Id,
            section,
            new EditLongFormSectionViewModel { ExpectedConcurrencyToken = read.Value.ConcurrencyToken, Body = "Second writer's text." },
            Ct));
        var staleSettle = await SectionsAsync(facade => facade.SettleSectionAsync(
            "user-2",
            seeded.Id,
            section,
            new SettleLongFormSectionViewModel { ExpectedConcurrencyToken = read.Value.ConcurrencyToken, Settled = true },
            Ct));

        Assert.Equal(ContentErrorCodes.LongFormDraftStale, staleEdit.Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, staleSettle.Error!.Code);

        var settled = await SectionsAsync(facade => facade.SettleSectionAsync(
            "user-1",
            seeded.Id,
            section,
            new SettleLongFormSectionViewModel { ExpectedConcurrencyToken = edited.Value.ConcurrencyToken, Settled = true },
            Ct));

        Assert.True(settled.Succeeded, settled.Error?.Code);
        Assert.NotEqual(edited.Value.ConcurrencyToken, settled.Value!.ConcurrencyToken);
        Assert.True(settled.Value.Sections[0].Settled);
        Assert.Equal("First writer's text.", settled.Value.Sections[0].Body);

        await InAsync(WsA, async db =>
        {
            var stored = await db.LongFormRevisions.AsNoTracking()
                .Where(revision => revision.LongFormDraftId == seeded.Id)
                .OrderBy(revision => revision.RevisionNumber)
                .ToListAsync(Ct);

            // Three revisions and no fourth from the refused writer, each pinned to the recipe the first was.
            Assert.Equal(3, stored.Count);
            Assert.DoesNotContain(stored, revision => revision.Content.Contains("Second writer", StringComparison.Ordinal));

            foreach (var revision in stored)
            {
                Assert.Equal(1, await db.LongFormRevisionRecipePins.CountAsync(pin => pin.LongFormRevisionId == revision.Id, Ct));
            }
        });
    }

    /// <summary>
    /// Acceptance against a real row version (LF.3.4): an acceptance composed before an edit landed is refused,
    /// one composed after it is taken, and it writes no revision of its own.
    /// </summary>
    [Fact]
    public async Task An_acceptance_composed_before_an_edit_landed_is_refused_and_a_fresh_one_writes_no_revision()
    {
        var (seeded, _, _) = await SeedDraftWithPinAsync(WsA);

        await InAsync(WsA, async db =>
        {
            var row = await db.LongFormDrafts.SingleAsync(draft => draft.Id == seeded.Id, Ct);
            row.Status = LongFormDraftStatus.Writing;
            await db.SaveChangesAsync(Ct);
        });

        var read = await DocumentAsync(facade => facade.GetDocumentAsync(seeded.Id, Ct));
        var section = read.Value!.Sections[0].Id;

        var edited = await SectionsAsync(facade => facade.EditSectionAsync(
            "user-1",
            seeded.Id,
            section,
            new EditLongFormSectionViewModel { ExpectedConcurrencyToken = read.Value.ConcurrencyToken, Body = "Changed after it was read." },
            Ct));
        Assert.True(edited.Succeeded, edited.Error?.Code);

        // The reader accepts what they read. It is no longer what is there.
        var stale = await DocumentAsync(facade => facade.AcceptAsync(
            "user-2",
            seeded.Id,
            new AcceptLongFormDocumentViewModel { ExpectedConcurrencyToken = read.Value.ConcurrencyToken, RevisionNumber = read.Value.RevisionNumber!.Value },
            Ct));
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, stale.Error!.Code);

        var accepted = await DocumentAsync(facade => facade.AcceptAsync(
            "user-1",
            seeded.Id,
            new AcceptLongFormDocumentViewModel { ExpectedConcurrencyToken = edited.Value!.ConcurrencyToken, RevisionNumber = edited.Value.RevisionNumber!.Value },
            Ct));

        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(LongFormDraftStatus.Accepted, accepted.Value!.Status);
        Assert.NotEqual(edited.Value.ConcurrencyToken, accepted.Value.ConcurrencyToken);
        Assert.Contains("Changed after it was read.", accepted.Value.Markdown, StringComparison.Ordinal);

        // The retry holds the token the acceptance moved. It is the same acceptance.
        var retried = await DocumentAsync(facade => facade.AcceptAsync(
            "user-1",
            seeded.Id,
            new AcceptLongFormDocumentViewModel { ExpectedConcurrencyToken = edited.Value.ConcurrencyToken, RevisionNumber = edited.Value.RevisionNumber.Value },
            Ct));
        Assert.True(retried.Succeeded, retried.Error?.Code);
        Assert.Equal(accepted.Value.ConcurrencyToken, retried.Value!.ConcurrencyToken);

        await InAsync(WsA, async db =>
        {
            // Two revisions: the seeded one and the edit. Accepting wrote none, and pointed at the second.
            var stored = await db.LongFormRevisions.AsNoTracking()
                .Where(revision => revision.LongFormDraftId == seeded.Id)
                .OrderBy(revision => revision.RevisionNumber)
                .ToListAsync(Ct);
            Assert.Equal(2, stored.Count);

            var row = await db.LongFormDrafts.AsNoTracking().SingleAsync(draft => draft.Id == seeded.Id, Ct);
            Assert.Equal(stored[1].Id, row.AcceptedRevisionId);
            Assert.NotNull(row.AcceptedAt);

            var resourceId = seeded.Id.ToString("D");
            var entry = Assert.Single(await db.AuditLogs.AsNoTracking()
                .Where(candidate => candidate.ResourceId == resourceId && candidate.Action == ContentAuditActions.LongFormDocumentAccepted)
                .ToListAsync(Ct));

            // A revision number and a count, and not a word of the piece.
            Assert.Matches(@"^Accepted revision 2 of a long-form draft(, confirming [0-9]+ thing\(s\) wrong with it)?\.$", entry.Summary);
        });
    }

    private async Task<OperationResult<LongFormAssembledDocumentServiceModel>> DocumentAsync(
        Func<ILongFormDocumentFacade, Task<OperationResult<LongFormAssembledDocumentServiceModel>>> ask)
    {
        await using var scope = fixture.ScopeFor(WsA);

        return await ask(scope.ServiceProvider.GetRequiredService<ILongFormDocumentFacade>());
    }

    /// <summary>
    /// The claim scanner on a real database (LF.3.2): the pins are read through the recipes module's own facade,
    /// a creator's edit is saved exactly as typed, and what the scanner found is stored in the revision.
    /// </summary>
    /// <remarks>
    /// The recipe version this fixture seeds has no stored snapshot, which is the case of a pinned version that
    /// can no longer be read: it contributes no facts, so the figure is reported rather than waved through. A
    /// figure a readable version states being left alone is <c>LongFormClaimScannerTests</c>'.
    /// </remarks>
    [Fact]
    public async Task A_creators_edit_is_scanned_saved_as_typed_and_its_findings_are_stored_in_the_revision()
    {
        var (seeded, revision, _) = await SeedDraftWithPinAsync(WsA);

        await InAsync(WsA, async db =>
        {
            var row = await db.LongFormDrafts.SingleAsync(draft => draft.Id == seeded.Id, Ct);
            row.Status = LongFormDraftStatus.Writing;
            await db.SaveChangesAsync(Ct);
        });

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var pins = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().LongFormRevisionRecipePins
                .AsNoTracking()
                .Where(pin => pin.LongFormRevisionId == revision.Id)
                .ToListAsync(Ct);

            // The real reader, through the real recipes facade: a pinned version with nothing to read is left
            // out, and reading it is not an error.
            Assert.Single(pins);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<ILongFormClaimSourceReader>().ReadAsync(pins, Ct));
        }

        var faithful = await SectionsAsync(facade => facade.GetSectionsAsync(seeded.Id, Ct));
        var section = faithful.Value!.Sections[0].Id;

        // A figure the recipe does not state: saved exactly as typed, with the caution recorded beside it.
        const string invented = "Use 98765 grams of flour.";
        var edited = await SectionsAsync(facade => facade.EditSectionAsync(
            "user-1", seeded.Id, section,
            new EditLongFormSectionViewModel { ExpectedConcurrencyToken = faithful.Value!.ConcurrencyToken, Body = invented },
            Ct));
        Assert.True(edited.Succeeded, edited.Error?.Code);
        Assert.Equal(invented, edited.Value!.Sections[0].Body);

        var flagged = await DocumentAsync(facade => facade.GetDocumentAsync(seeded.Id, Ct));
        var finding = Assert.Single(flagged.Value!.Sections[0].Findings, found => found.Code == "editorial.unsupported_number");
        Assert.Contains("98765", finding.Message);

        // Recorded in the revision itself, so it is what the revision says and not a reading made later.
        await InAsync(WsA, async db =>
        {
            var latest = await db.LongFormRevisions.AsNoTracking()
                .Where(candidate => candidate.LongFormDraftId == seeded.Id)
                .OrderByDescending(candidate => candidate.RevisionNumber)
                .FirstAsync(Ct);

            Assert.Contains("editorial.unsupported_number", latest.Content, StringComparison.Ordinal);
        });
    }

    private async Task<OperationResult<LongFormSectionsServiceModel>> SectionsAsync(
        Func<ILongFormSectionFacade, Task<OperationResult<LongFormSectionsServiceModel>>> ask)
    {
        await using var scope = fixture.ScopeFor(WsA);

        return await ask(scope.ServiceProvider.GetRequiredService<ILongFormSectionFacade>());
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Each_workspace_sees_only_its_own_drafts_revisions_and_pins()
    {
        var (draftA, revisionA, pinA) = await SeedDraftWithPinAsync(WsA, "A's article, in A's words.");
        var (draftB, revisionB, pinB) = await SeedDraftWithPinAsync(WsB, "B's article, in B's words.");

        await InAsync(WsB, async db =>
        {
            Assert.Null(await db.LongFormDrafts.FirstOrDefaultAsync(item => item.Id == draftA.Id, Ct));
            Assert.Null(await db.LongFormRevisions.FirstOrDefaultAsync(item => item.Id == revisionA.Id, Ct));
            Assert.Null(await db.LongFormRevisionRecipePins.FirstOrDefaultAsync(item => item.Id == pinA.Id, Ct));
            Assert.DoesNotContain(
                await db.LongFormRevisions.Select(item => item.Content).ToListAsync(Ct),
                content => content.Contains("A's article", StringComparison.Ordinal));
            Assert.DoesNotContain(
                pinA.RecipeVersionId,
                await db.LongFormRevisionRecipePins.Select(item => item.RecipeVersionId).ToListAsync(Ct));
        });

        await InAsync(WsA, async db =>
        {
            Assert.Null(await db.LongFormDrafts.FirstOrDefaultAsync(item => item.Id == draftB.Id, Ct));
            Assert.Null(await db.LongFormRevisions.FirstOrDefaultAsync(item => item.Id == revisionB.Id, Ct));
            Assert.Null(await db.LongFormRevisionRecipePins.FirstOrDefaultAsync(item => item.Id == pinB.Id, Ct));
        });
    }

    [Fact]
    public async Task One_workspace_cannot_write_into_another_workspaces_context_draft_or_recipe()
    {
        var contextA = await SeedContextAsync(WsA);
        var recipeA = await SeedRecipeAsync(WsA);
        var (draftA, revisionA, _) = await SeedDraftWithPinAsync(WsA);
        var (_, revisionB, _) = await SeedDraftWithPinAsync(WsB);

        // The keys are workspace-paired, so each of these names nothing in B however valid the id is in A.
        await AssertEngineRefusesAsync(WsB, NewDraft(contextA, LongFormKind.BlogArticle));
        await AssertEngineRefusesAsync(WsB, NewRevision(draftA, 2, revisionA, Document()));
        await AssertEngineRefusesAsync(WsB, Pin(revisionB, 1, recipeA, Guid.NewGuid()));
        await AssertEngineRefusesAsync(WsB, Pin(revisionA, 1, await SeedRecipeAsync(WsB), Guid.NewGuid()));

        Assert.Equal(1, await InAsync(WsA, db => db.LongFormRevisions.CountAsync(item => item.LongFormDraftId == draftA.Id, Ct)));
    }

    [Fact]
    public async Task A_revision_cannot_pin_another_workspaces_proposal_brand_or_guide_and_a_draft_cannot_accept_its_revision()
    {
        var (draftA, firstA, _) = await SeedDraftWithPinAsync(WsA);
        var (draftB, firstB, _) = await SeedDraftWithPinAsync(WsB);

        // Real rows in B, so what A is refused is "not yours" rather than "no such id".
        var (proposalB, brandRevisionB, guideVersionB) = await InAsync(WsB, async db =>
        {
            var operationId = await CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct);
            var proposal = new AiProposal
            {
                Id = Guid.NewGuid(),
                AiOperationId = operationId,
                OutputSchemaVersion = "content.long-form-section.v1",
                PromptTemplateId = "content.long-form-section",
                PromptTemplateVersion = "1.0.0",
                PromptTemplateBodyChecksum = "sha256:" + new string('c', 64),
                ProviderName = "test-provider",
                ModelName = "test-model",
                CreatedAt = Now,
            };
            var member = Guid.NewGuid();
            var profile = new BrandProfile
            {
                Id = Guid.NewGuid(), BrandName = "B", CreatedAt = Now, UpdatedAt = Now,
                CreatedByMembershipId = member, UpdatedByMembershipId = member,
            };
            var brandRevision = new BrandProfileRevision
            {
                Id = Guid.NewGuid(), BrandProfileId = profile.Id, Revision = 1,
                SchemaVersion = 1, Document = "{}", ChangedByMembershipId = member, CreatedAt = Now,
            };
            var guide = new BrandStyleGuide
            {
                Id = Guid.NewGuid(), DisplayName = "B's voice", CreatedAt = Now, UpdatedAt = Now,
                CreatedByMembershipId = member, UpdatedByMembershipId = member,
            };
            var guideVersion = new BrandStyleGuideVersion
            {
                Id = Guid.NewGuid(), BrandStyleGuideId = guide.Id, VersionNumber = 1,
                CreatedByMembershipId = member, CreatedAt = Now,
            };

            db.AddRange(proposal, profile, guide);
            await db.SaveChangesAsync(Ct);
            db.AddRange(brandRevision, guideVersion);
            await db.SaveChangesAsync(Ct);

            return (proposal.Id, brandRevision.Id, guideVersion.Id);
        });

        LongFormRevision Generated(LongFormDraft draft, LongFormRevision parent)
        {
            var revision = NewRevision(draft, 2, parent, Document("Written by a model."));
            revision.Source = ContentRevisionSource.AiGenerated;
            revision.AiProposalId = proposalB;
            revision.PromptTemplateId = "content.long-form-section";
            revision.PromptTemplateVersion = "1.0.0";
            revision.PromptTemplateBodyChecksum = "sha256:" + new string('c', 64);

            return revision;
        }

        var withBrand = NewRevision(draftA, 2, firstA, Document());
        withBrand.BrandProfileRevisionId = brandRevisionB;
        var withGuide = NewRevision(draftA, 2, firstA, Document());
        withGuide.BrandStyleGuideVersionId = guideVersionB;

        await AssertEngineRefusesAsync(WsA, Generated(draftA, firstA));
        await AssertEngineRefusesAsync(WsA, withBrand);
        await AssertEngineRefusesAsync(WsA, withGuide);

        await InAsync(WsA, async db =>
        {
            var tracked = await db.LongFormDrafts.SingleAsync(item => item.Id == draftA.Id, Ct);
            tracked.Status = LongFormDraftStatus.Accepted;
            tracked.AcceptedRevisionId = firstB.Id;
            tracked.AcceptedAt = Now;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });

        // The same three pins are B's to use, so the refusals above were about the workspace and nothing else.
        var own = Generated(draftB, firstB);
        own.BrandProfileRevisionId = brandRevisionB;
        own.BrandStyleGuideVersionId = guideVersionB;

        await InAsync(WsB, async db =>
        {
            db.Add(own);
            await db.SaveChangesAsync(Ct);
        });
    }

    // ---- What only the engine answers ----

    [Fact]
    public async Task Deleting_a_workspace_still_succeeds_with_an_accepted_long_form_draft_in_it()
    {
        // A draft and its revisions point at each other with restricted keys, a pin holds a restricted key to a
        // recipe version, and all four tables cascade from the same workspace. If SQL Server checked those
        // NO ACTION constraints mid-cascade rather than at the end of the statement, a workspace holding one
        // accepted article could never be erased.
        var workspaceId = Guid.NewGuid();

        await InAsync(WsA, async db =>
        {
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Doomed",
                Slug = $"doomed-{workspaceId:N}",
                CreatedAt = Now,
            });
            await db.SaveChangesAsync(Ct);
        });

        var (draft, revision, _) = await SeedDraftWithPinAsync(workspaceId);

        await InAsync(workspaceId, async db =>
        {
            var tracked = await db.LongFormDrafts.SingleAsync(item => item.Id == draft.Id, Ct);
            tracked.Status = LongFormDraftStatus.Accepted;
            tracked.AcceptedRevisionId = revision.Id;
            tracked.AcceptedAt = Now;
            await db.SaveChangesAsync(Ct);
        });

        await using var erase = fixture.ScopeFor(WsA);
        var unfiltered = SqlServerRecipeFixture.Db(erase);
        unfiltered.Workspaces.Remove(await unfiltered.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await unfiltered.SaveChangesAsync(Ct);

        // Counted with raw SQL rather than IgnoreQueryFilters: the erasure is what is under test.
        foreach (var table in new[] { "LongFormDrafts", "LongFormRevisions", "LongFormRevisionRecipePins" })
        {
            var remaining = await unfiltered.Database
                .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}}", workspaceId)
                .SingleAsync(Ct);

            Assert.Equal(0, remaining);
        }
    }

    // ---- Helpers ----

    private async Task InAsync(Guid workspaceId, Func<CreatorPantryDbContext, Task> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        await work(SqlServerRecipeFixture.Db(scope));
    }

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await work(SqlServerRecipeFixture.Db(scope));
    }

    private Task AssertEngineRefusesAsync(Guid workspaceId, object row) =>
        InAsync(workspaceId, async db =>
        {
            db.Add(row);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });

    private Task<Guid> SeedContextAsync(Guid workspaceId) =>
        InAsync(workspaceId, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

    private Task<(Guid RecipeId, Guid VersionId)> SeedRecipeAsync(Guid workspaceId) =>
        InAsync(workspaceId, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));

    private async Task<LongFormDraft> SeedEmptyDraftAsync(Guid workspaceId)
    {
        var draft = NewDraft(await SeedContextAsync(workspaceId), LongFormKind.BlogArticle);

        await InAsync(workspaceId, async db =>
        {
            db.Add(draft);
            await db.SaveChangesAsync(Ct);
        });

        return draft;
    }

    private async Task<(LongFormDraft Draft, LongFormRevision Revision, LongFormRevisionRecipePin Pin)> SeedDraftWithPinAsync(
        Guid workspaceId, string body = "The first draft of the opening.")
    {
        var recipe = await SeedRecipeAsync(workspaceId);
        var draft = NewDraft(await SeedContextAsync(workspaceId), LongFormKind.BlogArticle);
        var revision = NewRevision(draft, 1, parent: null, Document(body));
        var pin = Pin(revision, 0, recipe, Guid.NewGuid());

        await InAsync(workspaceId, async db =>
        {
            db.AddRange(draft, revision, pin);
            await db.SaveChangesAsync(Ct);
        });

        return (draft, revision, pin);
    }

    private static LongFormDraft NewDraft(Guid contextId, LongFormKind kind) => new()
    {
        Id = Guid.NewGuid(),
        CreativeContextId = contextId,
        Kind = kind,
        Status = LongFormDraftStatus.Draft,
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static LongFormRevision NewRevision(
        LongFormDraft draft, int number, LongFormRevision? parent, LongFormDocument document) => new()
        {
            Id = Guid.NewGuid(),
            LongFormDraftId = draft.Id,
            RevisionNumber = number,
            ParentRevisionId = parent?.Id,
            Source = ContentRevisionSource.CreatorEdit,
            SchemaVersion = LongFormDocument.CurrentSchemaVersion,
            Content = document.Serialize(),
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

    private static LongFormRevisionRecipePin Pin(
        LongFormRevision revision, int sortOrder, (Guid RecipeId, Guid VersionId) recipe, Guid referenceId) => new()
        {
            Id = Guid.NewGuid(),
            LongFormRevisionId = revision.Id,
            SortOrder = sortOrder,
            RecipeId = recipe.RecipeId,
            RecipeVersionId = recipe.VersionId,
            CreativeContextReferenceId = referenceId,
        };

    private static LongFormDocument Document(string? body = null) => new(
    [
        new(Guid.NewGuid(), LongFormSectionRole.Hook, "Start here", "Open with the smell of it.", body, false, null,
            body is null ? null : new(ContentRevisionSource.CreatorEdit, null, null, null, null, null, 1)),
    ]);
}
