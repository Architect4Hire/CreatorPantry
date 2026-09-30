using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// 11.3 against a real SQL Server: a recipe gaining a version commits its event with it, the event is delivered
/// after commit, and an accepted derivative is marked NeedsReview — never rewritten, never left falsely current.
/// </summary>
/// <remarks>
/// The fixture is shared across these tests, so every assertion is scoped to the recipe the test created rather
/// than to table counts; the outbox is a queue other tests in this class also fill.
/// </remarks>
public sealed class ContentStalenessPropagationTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly Guid Author = Guid.NewGuid();

    private static readonly RecipeVersionFacts EditFacts =
        new(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Edited.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Commit: the event rides the recipe write ----

    [Fact]
    public async Task An_edit_commits_one_event_naming_the_version_it_wrote()
    {
        var recipe = await CreateRecipeAsync(WsA);

        var version = await EditAsync(WsA, recipe.RecipeId);

        var message = Assert.Single(await OutboxFor(recipe.RecipeId));
        var change = RecipeVersionChangedEvent.TryParse(message.PayloadJson)!;

        Assert.Equal(RecipeVersionChangedEvent.MessageType, message.Type);
        Assert.Equal(version.Id, message.CorrelationId);
        Assert.Equal(WsA, change.WorkspaceId);
        Assert.Equal(recipe.RecipeId, change.RecipeId);
        Assert.Equal(version.Id, change.RecipeVersionId);
        Assert.Equal(2, change.VersionNumber);
        Assert.Equal(RecipeVersionChangeCause.Edit, change.Cause);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
    }

    [Fact]
    public async Task A_restore_commits_an_event_with_the_restore_cause()
    {
        var recipe = await CreateRecipeAsync(WsA);
        await EditAsync(WsA, recipe.RecipeId);

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var dataLayer = scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
            var loaded = (await dataLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;
            var outcome = await dataLayer.RestoreAsync(
                loaded,
                new RecipeVersionFacts(
                    RecipeVersionSource.Restore, RecipeVersionReadiness.Draft, "Back to the first.", RestoredFromVersionId: recipe.VersionId),
                loaded.Tags,
                Ct);
            Assert.False(outcome.Conflicted);
        }

        var messages = await OutboxFor(recipe.RecipeId);
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => RecipeVersionChangedEvent.TryParse(m.PayloadJson)!.Cause == RecipeVersionChangeCause.Restore);
    }

    [Fact]
    public async Task An_approval_commits_an_event_with_the_approval_cause()
    {
        var recipe = await CreateRecipeAsync(WsA);

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var dataLayer = scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
            var loaded = (await dataLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;
            loaded.Recipe.Recipe.Status = RecipeStatus.Approved;

            var committed = await dataLayer.TryTransitionAsync(
                loaded,
                new RecipeStatusTransition
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WsA,
                    RecipeId = recipe.RecipeId,
                    FromStatus = RecipeStatus.ReadyForReview,
                    ToStatus = RecipeStatus.Approved,
                    ActorMembershipId = Author,
                    OccurredAt = SqlServerRecipeFixture.Now,
                    MachineVersion = RecipeStatusTransitions.Version,
                    ReadinessRuleSetVersion = RecipeReadinessCatalogue.Version,
                    ReadinessEvaluatedVersionId = recipe.VersionId,
                },
                new RecipeVersionFacts(RecipeVersionSource.ReadinessApproval, RecipeVersionReadiness.Ready, "Approved."),
                new(
                    "user-1", RecipeAuditActions.Approved, RecipeAuditActions.ResourceType, recipe.RecipeId.ToString("D"),
                    Guid.NewGuid(), "Approved.", BeforeReference: nameof(RecipeStatus.ReadyForReview), AfterReference: nameof(RecipeStatus.Approved)),
                Ct);
            Assert.True(committed.Committed);
        }

        var message = Assert.Single(await OutboxFor(recipe.RecipeId));
        Assert.Equal(RecipeVersionChangeCause.Approval, RecipeVersionChangedEvent.TryParse(message.PayloadJson)!.Cause);
    }

    [Fact]
    public async Task Creating_a_recipe_commits_no_event()
    {
        var recipe = await CreateRecipeAsync(WsA);

        Assert.Empty(await OutboxFor(recipe.RecipeId));
    }

    // ---- Failure: a refused write announces nothing ----

    [Fact]
    public async Task A_lost_concurrency_race_commits_no_event_for_the_loser()
    {
        var recipe = await CreateRecipeAsync(WsA);

        await using var first = fixture.ScopeFor(WsA);
        await using var second = fixture.ScopeFor(WsA);
        var firstLayer = first.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
        var secondLayer = second.ServiceProvider.GetRequiredService<IRecipeDataLayer>();

        var readByFirst = (await firstLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;
        var readBySecond = (await secondLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;

        readByFirst.Recipe.Recipe.Title = "First edit";
        Assert.False((await firstLayer.UpdateAsync(readByFirst, EditFacts, null, null, Ct)).Conflicted);

        readBySecond.Recipe.Recipe.Title = "Second edit";
        Assert.True((await secondLayer.UpdateAsync(readBySecond, EditFacts, null, null, Ct)).Conflicted);

        // One version, one event: the loser's staged message went with its cleared tracker.
        Assert.Single(await OutboxFor(recipe.RecipeId));
        await using var read = fixture.ScopeFor(WsA);
        Assert.Equal(2, await SqlServerRecipeFixture.Db(read).RecipeVersions.CountAsync(v => v.RecipeId == recipe.RecipeId, Ct));
    }

    [Fact]
    public async Task A_failure_writing_the_version_commits_no_event_and_no_version()
    {
        var recipe = await CreateRecipeAsync(WsA);

        await using (var scope = fixture.ScopeFor(WsA, ThrowOnVersionInsert.Instance))
        {
            var dataLayer = scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
            var loaded = (await dataLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;
            loaded.Recipe.Recipe.Title = "Doomed edit";

            await Assert.ThrowsAsync<InvalidOperationException>(() => dataLayer.UpdateAsync(loaded, EditFacts, null, null, Ct));
        }

        Assert.Empty(await OutboxFor(recipe.RecipeId));
        await using var read = fixture.ScopeFor(WsA);
        Assert.Equal(1, await SqlServerRecipeFixture.Db(read).RecipeVersions.CountAsync(v => v.RecipeId == recipe.RecipeId, Ct));
    }

    // ---- Delivery: accepted derivatives are marked, not rewritten ----

    [Fact]
    public async Task A_recipe_edit_marks_an_accepted_derivative_NeedsReview_and_leaves_its_words_alone()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var content = await SeedAcceptedAsync(WsA, recipe);

        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        var proposal = await db.ContentProposals.SingleAsync(p => p.Id == content.ProposalId, Ct);
        var revision = await db.ContentRevisions.SingleAsync(r => r.Id == content.RevisionId, Ct);
        var transition = await db.ContentProposalTransitions
            .SingleAsync(t => t.ContentProposalId == content.ProposalId && t.ToStatus == ContentProposalStatus.NeedsReview, Ct);

        Assert.Equal(ContentProposalStatus.NeedsReview, proposal.Status);
        Assert.Equal(ContentStaleReasons.RecipeChanged, proposal.StaleReasons);
        Assert.NotNull(proposal.StaleSince);
        Assert.Equal(content.RevisionId, proposal.AcceptedRevisionId);

        // The accepted words and their pin are exactly what was accepted.
        Assert.Equal(ContentBody, revision.Content);
        Assert.Equal(recipe.VersionId, revision.RecipeVersionId);

        // A system move: no person behind it, and it names the revision found stale.
        Assert.Null(transition.ActorMembershipId);
        Assert.Equal(content.RevisionId, transition.ContentRevisionId);
        Assert.Equal(ContentStaleReasons.RecipeChanged, transition.StaleReasons);
    }

    [Fact]
    public async Task Between_the_recipe_commit_and_delivery_the_derivative_reads_Accepted_but_is_never_reported_current()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var content = await SeedAcceptedAsync(WsA, recipe);

        Assert.True((await CurrencyAsync(WsA, content.ProposalId))!.IsCurrent);

        await EditAsync(WsA, recipe.RecipeId);

        // The window: the flag has not been set because nothing has been delivered yet.
        await using (var scope = fixture.ScopeFor(WsA))
        {
            var status = (await SqlServerRecipeFixture.Db(scope).ContentProposals.SingleAsync(p => p.Id == content.ProposalId, Ct)).Status;
            Assert.Equal(ContentProposalStatus.Accepted, status);
        }

        var during = (await CurrencyAsync(WsA, content.ProposalId))!;
        Assert.False(during.IsCurrent);
        Assert.NotEqual(during.PinnedRecipeVersionId, during.LatestRecipeVersionId);

        await DispatchAsync();

        Assert.False((await CurrencyAsync(WsA, content.ProposalId))!.IsCurrent);
    }

    [Fact]
    public async Task A_replayed_delivery_changes_nothing()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var content = await SeedAcceptedAsync(WsA, recipe);
        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        // Deliver the same message again, as a crash after the side effect but before "Completed" would.
        var message = (await OutboxFor(recipe.RecipeId)).Single();
        await DeliverAsync(message);
        await DeliverAsync(message);

        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        Assert.Equal(1, await db.ContentProposalTransitions.CountAsync(
            t => t.ContentProposalId == content.ProposalId && t.ToStatus == ContentProposalStatus.NeedsReview, Ct));
        Assert.Equal(ContentProposalStatus.NeedsReview, (await db.ContentProposals.SingleAsync(p => p.Id == content.ProposalId, Ct)).Status);
    }

    [Fact]
    public async Task A_late_event_does_not_flip_content_the_creator_has_since_reaffirmed()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var content = await SeedAcceptedAsync(WsA, recipe);
        var second = await EditAsync(WsA, recipe.RecipeId);

        // Before the event is delivered the creator reaffirms: a new revision pinned to the latest version.
        await ReaffirmAsync(WsA, content, recipe.RecipeId, second.Id);
        await DispatchAsync();

        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        Assert.Equal(ContentProposalStatus.Accepted, (await db.ContentProposals.SingleAsync(p => p.Id == content.ProposalId, Ct)).Status);
        Assert.Equal(0, await db.ContentProposalTransitions.CountAsync(
            t => t.ContentProposalId == content.ProposalId && t.ToStatus == ContentProposalStatus.NeedsReview, Ct));
        Assert.True((await CurrencyAsync(WsA, content.ProposalId))!.IsCurrent);
    }

    [Fact]
    public async Task An_unaccepted_proposal_is_left_as_it_is()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var proposal = await SeedProposedAsync(WsA, recipe);

        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        Assert.Equal(ContentProposalStatus.Proposed, (await db.ContentProposals.SingleAsync(p => p.Id == proposal, Ct)).Status);
        Assert.Equal(0, await db.ContentProposalTransitions.CountAsync(t => t.ContentProposalId == proposal, Ct));
    }

    // ---- Isolation and tampering ----

    [Fact]
    public async Task A_change_in_one_workspace_never_touches_the_other_workspaces_derivatives()
    {
        var recipeA = await CreateRecipeAsync(WsA);
        var recipeB = await CreateRecipeAsync(WsB);
        var contentA = await SeedAcceptedAsync(WsA, recipeA);
        var contentB = await SeedAcceptedAsync(WsB, recipeB);

        await EditAsync(WsA, recipeA.RecipeId);
        await DispatchAsync();

        Assert.Equal(ContentProposalStatus.NeedsReview, await StatusAsync(WsA, contentA.ProposalId));
        Assert.Equal(ContentProposalStatus.Accepted, await StatusAsync(WsB, contentB.ProposalId));
        Assert.True((await CurrencyAsync(WsB, contentB.ProposalId))!.IsCurrent);

        // And B cannot even see A's proposal to ask about its currency.
        Assert.Null(await CurrencyAsync(WsB, contentA.ProposalId));
    }

    [Fact]
    public async Task A_payload_claiming_another_workspace_for_a_recipe_marks_nothing()
    {
        var recipeA = await CreateRecipeAsync(WsA);
        var contentA = await SeedAcceptedAsync(WsA, recipeA);
        var recipeB = await CreateRecipeAsync(WsB);
        var contentB = await SeedAcceptedAsync(WsB, recipeB);
        await EditAsync(WsA, recipeA.RecipeId);

        // Tampered: workspace B's id over workspace A's recipe. The handler resolves B, and every read below it
        // runs under B's filter, so A's recipe and proposal are invisible.
        var real = (await OutboxFor(recipeA.RecipeId)).Single();
        var forged = RecipeVersionChangedEvent.TryParse(real.PayloadJson)! with { WorkspaceId = WsB };
        await DeliverAsync(new OutboxMessage
        {
            Id = real.Id,
            Type = real.Type,
            PayloadJson = forged.Serialize(),
            CorrelationId = real.CorrelationId,
            Attempts = real.Attempts,
        });

        Assert.Equal(ContentProposalStatus.Accepted, await StatusAsync(WsA, contentA.ProposalId));

        // And the workspace the payload claimed is untouched too: nothing of B's was marked on A's behalf.
        Assert.Equal(ContentProposalStatus.Accepted, await StatusAsync(WsB, contentB.ProposalId));
    }

    [Fact]
    public async Task Delivery_does_not_depend_on_the_editor_still_being_a_member()
    {
        // No WorkspaceMembership row exists for the editor at all — the recipe's authors are bare ids — so a
        // reaction that needed one could never have marked this.
        var recipe = await CreateRecipeAsync(WsA);
        var content = await SeedAcceptedAsync(WsA, recipe);

        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        Assert.Equal(ContentProposalStatus.NeedsReview, await StatusAsync(WsA, content.ProposalId));
    }

    // ---- Poison ----

    [Fact]
    public async Task A_malformed_payload_fails_the_delivery_so_it_is_retried_and_eventually_poisoned()
    {
        var bad = new OutboxMessageEnvelope(Guid.NewGuid(), RecipeVersionChangedEvent.MessageType, "{ not json", Guid.NewGuid(), 1);

        await using var scope = fixture.ScopeFor(WsA);
        var handler = scope.ServiceProvider.GetRequiredKeyedService<IOutboxMessageHandler>(RecipeVersionChangedEvent.MessageType);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(bad, Ct));
    }

    // ---- Helpers ----

    private const string ContentBody = """{"headnote":"A cake."}""";

    private sealed record SeededContent(Guid ProposalId, Guid RevisionId);

    private async Task<CreatedRecipe> CreateRecipeAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var tagId = workspaceId == WsA ? SqlServerRecipeFixture.TagIdA : SqlServerRecipeFixture.TagIdB;

        return await scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>().CreateAsync(
            SqlServerRecipeFixture.NewRecipe("Olive oil cake", tagId),
            new RecipeVersionFacts(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Created."),
            [],
            Ct);
    }

    private async Task<RecipeVersion> EditAsync(Guid workspaceId, Guid recipeId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var dataLayer = scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
        var loaded = (await dataLayer.GetForUpdateAsync(recipeId, Ct))!;
        loaded.Recipe.Recipe.Title = $"Edited {Guid.NewGuid():N}";

        var outcome = await dataLayer.UpdateAsync(loaded, EditFacts, null, null, Ct);
        Assert.False(outcome.Conflicted);

        return outcome.Version!;
    }

    private async Task<SeededContent> SeedAcceptedAsync(Guid workspaceId, CreatedRecipe recipe)
    {
        var proposalId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);

        db.ContentProposals.Add(NewProposal(workspaceId, proposalId, recipe.RecipeId));
        db.ContentRevisions.Add(NewRevision(workspaceId, proposalId, revisionId, recipe.RecipeId, recipe.VersionId, 1, null));
        await db.SaveChangesAsync(Ct);

        var proposal = await db.ContentProposals.SingleAsync(p => p.Id == proposalId, Ct);
        proposal.Status = ContentProposalStatus.Accepted;
        proposal.AcceptedRevisionId = revisionId;
        await db.SaveChangesAsync(Ct);

        return new SeededContent(proposalId, revisionId);
    }

    private async Task<Guid> SeedProposedAsync(Guid workspaceId, CreatedRecipe recipe)
    {
        var proposalId = Guid.NewGuid();

        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);
        db.ContentProposals.Add(NewProposal(workspaceId, proposalId, recipe.RecipeId));
        db.ContentRevisions.Add(NewRevision(workspaceId, proposalId, Guid.NewGuid(), recipe.RecipeId, recipe.VersionId, 1, null));
        await db.SaveChangesAsync(Ct);

        return proposalId;
    }

    private async Task ReaffirmAsync(Guid workspaceId, SeededContent content, Guid recipeId, Guid latestVersionId)
    {
        var reaffirmedId = Guid.NewGuid();

        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);
        var revision = NewRevision(workspaceId, content.ProposalId, reaffirmedId, recipeId, latestVersionId, 2, content.RevisionId);
        revision.Source = ContentRevisionSource.Reaffirmed;
        db.ContentRevisions.Add(revision);
        await db.SaveChangesAsync(Ct);

        (await db.ContentProposals.SingleAsync(p => p.Id == content.ProposalId, Ct)).AcceptedRevisionId = reaffirmedId;
        await db.SaveChangesAsync(Ct);
    }

    private static ContentProposal NewProposal(Guid workspaceId, Guid id, Guid recipeId) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        RecipeId = recipeId,
        Kind = ContentPackageKind.Editorial,
        Status = ContentProposalStatus.Proposed,
        CreatedByMembershipId = Author,
        CreatedAt = SqlServerRecipeFixture.Now,
        UpdatedAt = SqlServerRecipeFixture.Now,
    };

    private static ContentRevision NewRevision(
        Guid workspaceId, Guid proposalId, Guid id, Guid recipeId, Guid versionId, int number, Guid? parentId) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        ContentProposalId = proposalId,
        RecipeId = recipeId,
        RevisionNumber = number,
        ParentRevisionId = parentId,
        Source = ContentRevisionSource.CreatorEdit,
        RecipeVersionId = versionId,
        SchemaVersion = 1,
        Content = ContentBody,
        CreatedByMembershipId = Author,
        CreatedAt = SqlServerRecipeFixture.Now,
    };

    private async Task<List<OutboxMessage>> OutboxFor(Guid recipeId)
    {
        await using var scope = fixture.ScopeFor(WsA);
        var id = recipeId.ToString();

        return await SqlServerRecipeFixture.Db(scope).OutboxMessages
            .AsNoTracking()
            .Where(message => message.PayloadJson.Contains(id))
            .ToListAsync(Ct);
    }

    /// <summary>One dispatch pass, as the worker's polling loop would run it.</summary>
    private async Task DispatchAsync()
    {
        await using var scope = fixture.ScopeFor(WsA);
        await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
    }

    /// <summary>Hands one message straight to its handler, bypassing the queue — a redelivery.</summary>
    private async Task DeliverAsync(OutboxMessage message)
    {
        await using var scope = fixture.ScopeFor(WsA);
        var handler = scope.ServiceProvider.GetRequiredKeyedService<IOutboxMessageHandler>(message.Type);

        await handler.HandleAsync(
            new OutboxMessageEnvelope(message.Id, message.Type, message.PayloadJson, message.CorrelationId, message.Attempts + 1), Ct);
    }

    private async Task<ContentCurrencyServiceModel?> CurrencyAsync(Guid workspaceId, Guid proposalId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        return await scope.ServiceProvider.GetRequiredService<IContentStalenessFacade>().GetCurrencyAsync(proposalId, Ct);
    }

    private async Task<ContentProposalStatus> StatusAsync(Guid workspaceId, Guid proposalId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        return (await SqlServerRecipeFixture.Db(scope).ContentProposals.SingleAsync(p => p.Id == proposalId, Ct)).Status;
    }
}
