using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The content proposal aggregate's schema, isolation and immutable history, over the same two-workspace
/// SQLite fixture the recipe and brand aggregates use. Proves the EF configuration — composite keys, check
/// constraints, unique indexes, the query filter and the immutability interceptor — not that SQL Server
/// accepts the DDL.
/// </summary>
public sealed class ContentProposalAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid WsA = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid WsB = RecipeAggregateFixture.WorkspaceB;
    private static readonly Guid Author = Guid.NewGuid();

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(Guid RecipeId, Guid VersionId);

    private async Task<Seeded> SeedRecipeAsync(Guid workspaceId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);

        var version = NewVersion(workspaceId, recipe.Id, 1);
        db.RecipeVersions.Add(version);

        await db.SaveChangesAsync(Ct);
        return new Seeded(recipe.Id, version.Id);
    }

    private static RecipeVersion NewVersion(Guid workspaceId, Guid recipeId, int number) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        RecipeId = recipeId,
        VersionNumber = number,
        Source = RecipeVersionSource.CreatorEdit,
        Readiness = RecipeVersionReadiness.Draft,
        CreatedByMembershipId = Author,
        CreatedAt = Now,
        SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
    };

    private static ContentProposal NewProposal(
        Guid workspaceId, Guid recipeId, ContentPackageKind kind = ContentPackageKind.Editorial) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        RecipeId = recipeId,
        Kind = kind,
        Status = ContentProposalStatus.Proposed,
        CreatedByMembershipId = Author,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static ContentRevision NewRevision(
        ContentProposal proposal, Guid versionId, int number = 1, Guid? parentId = null,
        ContentRevisionSource source = ContentRevisionSource.CreatorEdit, string content = """{"headnote":"A cake."}""") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = proposal.WorkspaceId,
        ContentProposalId = proposal.Id,
        RecipeId = proposal.RecipeId,
        RevisionNumber = number,
        ParentRevisionId = parentId,
        Source = source,
        RecipeVersionId = versionId,
        SchemaVersion = 1,
        Content = content,
        CreatedByMembershipId = Author,
        CreatedAt = Now,
    };

    /// <summary>A proposal with one revision, committed the way the write seam will: proposal first, then revision.</summary>
    private async Task<(ContentProposal Proposal, ContentRevision Revision, Seeded Source)> SeedProposalAsync(Guid workspaceId)
    {
        var source = await SeedRecipeAsync(workspaceId);
        var proposal = NewProposal(workspaceId, source.RecipeId);
        var revision = NewRevision(proposal, source.VersionId);

        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentProposals.Add(proposal);
        db.ContentRevisions.Add(revision);
        await db.SaveChangesAsync(Ct);

        return (proposal, revision, source);
    }

    private async Task AcceptAsync(Guid workspaceId, Guid proposalId, Guid revisionId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var proposal = await db.ContentProposals.SingleAsync(p => p.Id == proposalId, Ct);
        proposal.Status = ContentProposalStatus.Accepted;
        proposal.AcceptedRevisionId = revisionId;
        db.ContentProposalTransitions.Add(new ContentProposalTransition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ContentProposalId = proposalId,
            FromStatus = ContentProposalStatus.Proposed,
            ToStatus = ContentProposalStatus.Accepted,
            ContentRevisionId = revisionId,
            ActorMembershipId = Author,
            OccurredAt = Now,
            MachineVersion = ContentProposalTransitions.Version,
        });
        await db.SaveChangesAsync(Ct);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Each_workspace_sees_only_its_own_proposals_revisions_and_transitions()
    {
        var a = await SeedProposalAsync(WsA);
        var b = await SeedProposalAsync(WsB);
        await AcceptAsync(WsA, a.Proposal.Id, a.Revision.Id);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);

        Assert.Equal(a.Proposal.Id, (await db.ContentProposals.SingleAsync(Ct)).Id);
        Assert.Equal(a.Revision.Id, (await db.ContentRevisions.SingleAsync(Ct)).Id);
        Assert.Single(await db.ContentProposalTransitions.ToListAsync(Ct));
        Assert.Null(await db.ContentProposals.FirstOrDefaultAsync(p => p.Id == b.Proposal.Id, Ct));
        Assert.Null(await db.ContentRevisions.FirstOrDefaultAsync(r => r.Id == b.Revision.Id, Ct));
    }

    [Fact]
    public async Task Two_workspaces_can_each_hold_a_slot_for_their_own_recipe_and_kind()
    {
        await SeedProposalAsync(WsA);
        await SeedProposalAsync(WsB);

        await using var scope = _fixture.ScopeFor(WsB);
        Assert.Single(await RecipeAggregateFixture.Db(scope).ContentProposals.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_revision_cannot_pin_another_workspaces_recipe_version()
    {
        var a = await SeedProposalAsync(WsA);
        var b = await SeedRecipeAsync(WsB);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentRevisions.Add(NewRevision(a.Proposal, b.VersionId, number: 2, parentId: a.Revision.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_revision_cannot_pin_a_version_of_a_different_recipe()
    {
        var a = await SeedProposalAsync(WsA);
        var other = await SeedRecipeAsync(WsA);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentRevisions.Add(NewRevision(a.Proposal, other.VersionId, number: 2, parentId: a.Revision.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_revision_cannot_restate_a_recipe_other_than_its_proposals()
    {
        var a = await SeedProposalAsync(WsA);
        var other = await SeedRecipeAsync(WsA);

        var stray = NewRevision(a.Proposal, other.VersionId, number: 2, parentId: a.Revision.Id);
        stray.RecipeId = other.RecipeId;

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentRevisions.Add(stray);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_brand_pin_cannot_name_another_workspaces_profile_revision()
    {
        var a = await SeedProposalAsync(WsA);

        Guid foreignBrandRevision;
        await using (var scopeB = _fixture.ScopeFor(WsB))
        {
            var dbB = RecipeAggregateFixture.Db(scopeB);
            var profile = new BrandProfile
            {
                Id = Guid.NewGuid(), WorkspaceId = WsB, BrandName = "B", CreatedAt = Now, UpdatedAt = Now,
                CreatedByMembershipId = Author, UpdatedByMembershipId = Author,
            };
            var revision = new BrandProfileRevision
            {
                Id = Guid.NewGuid(), WorkspaceId = WsB, BrandProfileId = profile.Id, Revision = 1,
                SchemaVersion = 1, Document = "{}", ChangedByMembershipId = Author, CreatedAt = Now,
            };
            dbB.BrandProfiles.Add(profile);
            dbB.BrandProfileRevisions.Add(revision);
            await dbB.SaveChangesAsync(Ct);
            foreignBrandRevision = revision.Id;
        }

        var pinned = NewRevision(a.Proposal, a.Source.VersionId, number: 2, parentId: a.Revision.Id);
        pinned.BrandProfileRevisionId = foreignBrandRevision;

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentRevisions.Add(pinned);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_accepted_pointer_cannot_name_another_proposals_revision()
    {
        var first = await SeedProposalAsync(WsA);
        var second = await SeedProposalAsync(WsA);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        var proposal = await db.ContentProposals.SingleAsync(p => p.Id == second.Proposal.Id, Ct);
        proposal.Status = ContentProposalStatus.Accepted;
        proposal.AcceptedRevisionId = first.Revision.Id;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    // ---- Immutable accepted history ----

    [Fact]
    public async Task An_accepted_revision_cannot_be_modified()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        var revision = await db.ContentRevisions.SingleAsync(Ct);
        revision.Content = """{"headnote":"Rewritten."}""";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task An_accepted_revision_cannot_be_repinned()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        var revision = await db.ContentRevisions.SingleAsync(Ct);
        revision.RecipeVersionId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task An_accepted_revision_cannot_be_deleted()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentRevisions.Remove(await db.ContentRevisions.SingleAsync(Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_transition_cannot_be_modified_or_deleted()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            (await db.ContentProposalTransitions.SingleAsync(Ct)).Reason = "edited";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.ContentProposalTransitions.Remove(await db.ContentProposalTransitions.SingleAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task Accepted_content_is_retained_through_regeneration_rejection_staleness_and_a_newer_acceptance()
    {
        var seeded = await SeedProposalAsync(WsA);
        const string acceptedContent = """{"headnote":"A cake."}""";
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        // The creator regenerates: a second revision under the same proposal, Accepted -> Proposed.
        var second = NewRevision(seeded.Proposal, seeded.Source.VersionId, 2, seeded.Revision.Id, content: """{"headnote":"Another."}""");
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.ContentRevisions.Add(second);
            (await db.ContentProposals.SingleAsync(Ct)).Status = ContentProposalStatus.Proposed;
            await db.SaveChangesAsync(Ct);
        }

        // ...and rejects it. The accepted pointer has not moved.
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            (await db.ContentProposals.SingleAsync(Ct)).Status = ContentProposalStatus.Rejected;
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var proposal = await db.ContentProposals.SingleAsync(Ct);
            Assert.Equal(seeded.Revision.Id, proposal.AcceptedRevisionId);
            Assert.Equal(acceptedContent, (await db.ContentRevisions.SingleAsync(r => r.Id == seeded.Revision.Id, Ct)).Content);
        }

        // A newer acceptance moves the pointer; the earlier revision stays.
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var proposal = await db.ContentProposals.SingleAsync(Ct);
            proposal.Status = ContentProposalStatus.Accepted;
            proposal.AcceptedRevisionId = second.Id;
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            Assert.Equal(second.Id, (await db.ContentProposals.SingleAsync(Ct)).AcceptedRevisionId);
            Assert.Equal(2, await db.ContentRevisions.CountAsync(Ct));
            Assert.Equal(acceptedContent, (await db.ContentRevisions.SingleAsync(r => r.Id == seeded.Revision.Id, Ct)).Content);
        }
    }

    [Fact]
    public async Task Marking_an_accepted_proposal_NeedsReview_leaves_the_accepted_revision_and_its_pins_untouched()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var proposal = await db.ContentProposals.SingleAsync(Ct);
            proposal.Status = ContentProposalStatus.NeedsReview;
            proposal.StaleSince = Now.AddHours(1);
            proposal.StaleReasons = ContentStaleReasons.RecipeChanged;
            db.ContentProposalTransitions.Add(new ContentProposalTransition
            {
                Id = Guid.NewGuid(), WorkspaceId = WsA, ContentProposalId = proposal.Id,
                FromStatus = ContentProposalStatus.Accepted, ToStatus = ContentProposalStatus.NeedsReview,
                StaleReasons = ContentStaleReasons.RecipeChanged, OccurredAt = Now.AddHours(1),
                MachineVersion = ContentProposalTransitions.Version,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var proposal = await db.ContentProposals.SingleAsync(Ct);
            var revision = await db.ContentRevisions.SingleAsync(Ct);

            Assert.Equal(ContentProposalStatus.NeedsReview, proposal.Status);
            Assert.Equal(seeded.Revision.Id, proposal.AcceptedRevisionId);
            Assert.Equal(seeded.Revision.Content, revision.Content);
            Assert.Equal(seeded.Source.VersionId, revision.RecipeVersionId);
        }
    }

    [Fact]
    public async Task Reaffirming_appends_a_re_pinned_revision_and_leaves_the_old_pins_in_history()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        Guid newVersionId;
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var newVersion = NewVersion(WsA, seeded.Source.RecipeId, 2);
            db.RecipeVersions.Add(newVersion);
            newVersionId = newVersion.Id;
            await db.SaveChangesAsync(Ct);
        }

        var reaffirmed = NewRevision(seeded.Proposal, newVersionId, 2, seeded.Revision.Id, ContentRevisionSource.Reaffirmed);
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.ContentRevisions.Add(reaffirmed);
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var revisions = await db.ContentRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(Ct);
            Assert.Equal(seeded.Source.VersionId, revisions[0].RecipeVersionId);
            Assert.Equal(newVersionId, revisions[1].RecipeVersionId);
            Assert.Equal(ContentStaleReasons.RecipeChanged, revisions[0].Pins.StaleAgainst(revisions[1].Pins));
        }
    }

    // ---- Constraints ----

    [Fact]
    public async Task One_slot_per_recipe_and_kind_but_a_second_kind_is_allowed()
    {
        var seeded = await SeedProposalAsync(WsA);

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.ContentProposals.Add(NewProposal(WsA, seeded.Source.RecipeId, ContentPackageKind.Seo));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.ContentProposals.Add(NewProposal(WsA, seeded.Source.RecipeId, ContentPackageKind.Editorial));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task A_revision_number_cannot_be_reused()
    {
        var seeded = await SeedProposalAsync(WsA);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentRevisions.Add(NewRevision(seeded.Proposal, seeded.Source.VersionId, number: 1));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Revision_one_has_no_parent_and_later_revisions_must_have_one()
    {
        var seeded = await SeedProposalAsync(WsA);

        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.ContentRevisions.Add(NewRevision(seeded.Proposal, seeded.Source.VersionId, number: 2, parentId: null));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task An_AI_proposal_id_belongs_to_generated_revisions_only_and_generated_ones_name_their_template()
    {
        var seeded = await SeedProposalAsync(WsA);

        // Generated, but naming no template.
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var generated = NewRevision(seeded.Proposal, seeded.Source.VersionId, 2, seeded.Revision.Id, ContentRevisionSource.AiGenerated);
            db.ContentRevisions.Add(generated);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        // An edit claiming an AI proposal.
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var edit = NewRevision(seeded.Proposal, seeded.Source.VersionId, 2, seeded.Revision.Id);
            edit.AiProposalId = Guid.NewGuid();
            db.ContentRevisions.Add(edit);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task A_reaffirmation_must_have_a_parent()
    {
        var seeded = await SeedRecipeAsync(WsA);
        var proposal = NewProposal(WsA, seeded.RecipeId);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentProposals.Add(proposal);
        db.ContentRevisions.Add(NewRevision(proposal, seeded.VersionId, source: ContentRevisionSource.Reaffirmed));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Accepted_and_NeedsReview_proposals_must_name_an_accepted_revision()
    {
        var seeded = await SeedRecipeAsync(WsA);
        var proposal = NewProposal(WsA, seeded.RecipeId);
        proposal.Status = ContentProposalStatus.Accepted;

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentProposals.Add(proposal);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Staleness_is_recorded_exactly_while_NeedsReview()
    {
        var seeded = await SeedProposalAsync(WsA);
        await AcceptAsync(WsA, seeded.Proposal.Id, seeded.Revision.Id);

        // NeedsReview with no staleness recorded.
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            (await db.ContentProposals.SingleAsync(Ct)).Status = ContentProposalStatus.NeedsReview;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        // Staleness left behind on a proposal that is not NeedsReview.
        await using (var scope = _fixture.ScopeFor(WsA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var proposal = await db.ContentProposals.SingleAsync(Ct);
            proposal.StaleSince = Now;
            proposal.StaleReasons = ContentStaleReasons.VoiceChanged;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task Only_the_system_marks_content_stale()
    {
        var seeded = await SeedProposalAsync(WsA);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        db.ContentProposalTransitions.Add(new ContentProposalTransition
        {
            Id = Guid.NewGuid(), WorkspaceId = WsA, ContentProposalId = seeded.Proposal.Id,
            FromStatus = ContentProposalStatus.Accepted, ToStatus = ContentProposalStatus.NeedsReview,
            StaleReasons = ContentStaleReasons.RecipeChanged, ActorMembershipId = Author, OccurredAt = Now,
            MachineVersion = ContentProposalTransitions.Version,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_recipe_with_content_proposals_cannot_be_deleted()
    {
        var seeded = await SeedProposalAsync(WsA);

        await using var scope = _fixture.ScopeFor(WsA);
        var db = RecipeAggregateFixture.Db(scope);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM Recipes WHERE Id = {0}", [seeded.Source.RecipeId], Ct));
    }
}
