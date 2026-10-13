using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.6.1 against a real SQL Server: a pinned source moving marks an accepted long-form article NeedsReview,
/// through the outbox, after commit — never rewritten, never left falsely current.
/// </summary>
/// <remarks>
/// <para>
/// The recipe half runs the whole path: a real recipe write, the event row it commits with, the dispatcher,
/// the recipes module's handler and the content module's consumer. The style guide half starts at the
/// consumer, handed an active version the way the brand module's handler hands it one; that the activation
/// commits its event, and that the handler reads the active version, are
/// <c>BrandStyleGuideActivationEndpointTests</c>', where the brand module is composed.
/// </para>
/// <para>
/// The fixture is shared, so every assertion is scoped to the rows the test made.
/// </para>
/// </remarks>
public sealed class LongFormStalenessPropagationTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static readonly RecipeVersionFacts EditFacts =
        new(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Edited.");

    private const string AcceptedWords = "The words the creator accepted, exactly as they accepted them.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- A recipe update cannot leave an accepted article falsely current ----

    [Fact]
    public async Task A_recipe_update_marks_the_accepted_article_NeedsReview_and_changes_nothing_else_about_it()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);
        var before = await SnapshotAsync(WsA, article);

        await EditAsync(WsA, recipe.RecipeId);

        // The event committed with the version, and is waiting. Until it is delivered the draft still reads
        // Accepted: that is the window, and the durable row is what closes it.
        Assert.Equal(OutboxMessageStatus.Pending, Assert.Single(await OutboxFor(recipe.RecipeId)).Status);
        Assert.Equal(LongFormDraftStatus.Accepted, (await DraftAsync(WsA, article.DraftId)).Status);

        await DispatchAsync();

        var draft = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.NeedsReview, draft.Status);
        Assert.Equal(ContentStaleReasons.RecipeChanged, draft.StaleReasons);
        Assert.NotNull(draft.StaleSince);

        // Which article it is, and when it was accepted, are exactly what they were.
        Assert.Equal(article.RevisionId, draft.AcceptedRevisionId);
        Assert.Equal(before.AcceptedAt, draft.AcceptedAt);

        // Nothing rewritten, regenerated or deleted: the same revisions, the same words, the same pins.
        var after = await SnapshotAsync(WsA, article);
        Assert.Equal(before.Revisions, after.Revisions);
        Assert.Equal(before.Pins, after.Pins);
        Assert.Contains(AcceptedWords, after.Revisions.Single().Content, StringComparison.Ordinal);

        // And the creator is told which source moved, by the read the screen uses.
        var read = await DocumentAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.NeedsReview, read!.Status);
        Assert.Equal([ContentStaleReasons.RecipeChanged], read.StaleReasons);
        Assert.Equal(draft.StaleSince, read.StaleSince);
        Assert.Contains(AcceptedWords, read.Markdown, StringComparison.Ordinal);

        // One system audit entry: no actor, a status and a reason, and not a word of the piece.
        var entry = Assert.Single(await AuditAsync(WsA, article.DraftId));
        Assert.Null(entry.ActorUserId);
        Assert.Equal("Marked an accepted long-form draft as needing review: RecipeChanged.", entry.Summary);
    }

    [Fact]
    public async Task A_recipe_write_that_fails_announces_nothing_and_the_article_stays_accepted()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);

        // Two writers; the second loses on the recipe's row version. No version was written for it, so there
        // is no newer version for the article to be behind — and no event.
        await using (var first = fixture.ScopeFor(WsA))
        await using (var second = fixture.ScopeFor(WsA))
        {
            var firstLayer = first.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
            var secondLayer = second.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
            var readByFirst = (await firstLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;
            var readBySecond = (await secondLayer.GetForUpdateAsync(recipe.RecipeId, Ct))!;

            readByFirst.Recipe.Recipe.Title = "First edit";
            Assert.False((await firstLayer.UpdateAsync(readByFirst, EditFacts, null, null, Ct)).Conflicted);

            readBySecond.Recipe.Recipe.Title = "Second edit";
            Assert.True((await secondLayer.UpdateAsync(readBySecond, EditFacts, null, null, Ct)).Conflicted);
        }

        // One event for the one version that was written — the winner's.
        Assert.Single(await OutboxFor(recipe.RecipeId));

        await DispatchAsync();

        // Marked once, for the write that happened. The write that did not happen left nothing behind.
        Assert.Equal(LongFormDraftStatus.NeedsReview, (await DraftAsync(WsA, article.DraftId)).Status);
        Assert.Single(await AuditAsync(WsA, article.DraftId));
    }

    [Fact]
    public async Task A_delivery_that_fails_writes_nothing_and_its_redelivery_marks_the_article()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);
        await EditAsync(WsA, recipe.RecipeId);
        var message = Assert.Single(await OutboxFor(recipe.RecipeId));

        // The consumer's save is refused, as a database blip would refuse it.
        await using (var failing = fixture.ScopeFor(WsA, new ThrowOnLongFormDraftUpdate()))
        {
            var handler = failing.ServiceProvider.GetRequiredKeyedService<IOutboxMessageHandler>(message.Type);

            await Assert.ThrowsAsync<AggregateException>(() => handler.HandleAsync(Envelope(message), Ct));
        }

        // Nothing half-done: not marked, and no audit entry claiming it was.
        var untouched = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.Accepted, untouched.Status);
        Assert.Equal(ContentStaleReasons.None, untouched.StaleReasons);
        Assert.Empty(await AuditAsync(WsA, article.DraftId));

        // The throw is what makes the outbox keep the message. Delivered again, it does the whole job.
        await DeliverAsync(message);

        Assert.Equal(LongFormDraftStatus.NeedsReview, (await DraftAsync(WsA, article.DraftId)).Status);
        Assert.Single(await AuditAsync(WsA, article.DraftId));
    }

    [Fact]
    public async Task A_draft_written_to_while_it_is_being_marked_fails_the_delivery_and_the_redelivery_decides_afresh()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);
        await EditAsync(WsA, recipe.RecipeId);
        var message = Assert.Single(await OutboxFor(recipe.RecipeId));

        // Somebody else writes the draft row between the consumer reading it and saving it: the consumer's
        // UPDATE carries the row version it read, finds no row, and the save throws.
        await using (var racing = fixture.ScopeFor(WsA, new WriteDraftFirst(fixture, WsA, article.DraftId)))
        {
            var handler = racing.ServiceProvider.GetRequiredKeyedService<IOutboxMessageHandler>(message.Type);

            var failure = await Assert.ThrowsAsync<AggregateException>(() => handler.HandleAsync(Envelope(message), Ct));
            Assert.Contains(failure.InnerExceptions, inner => inner is DbUpdateConcurrencyException);
        }

        // The loser wrote nothing: still Accepted, and no audit entry for a mark that did not happen.
        Assert.Equal(LongFormDraftStatus.Accepted, (await DraftAsync(WsA, article.DraftId)).Status);
        Assert.Empty(await AuditAsync(WsA, article.DraftId));

        // Not swallowed, so the outbox still holds the message. Delivered again it reads the row as it is now.
        await DeliverAsync(message);

        var marked = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.NeedsReview, marked.Status);
        Assert.Equal(ContentStaleReasons.RecipeChanged, marked.StaleReasons);
        Assert.Single(await AuditAsync(WsA, article.DraftId));
    }

    [Fact]
    public async Task A_draft_reopened_before_the_delivery_is_left_alone_by_it()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);
        await EditAsync(WsA, recipe.RecipeId);

        // Between the recipe's commit and the delivery, the creator took the piece back to writing.
        await using (var scope = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            (await db.LongFormDrafts.SingleAsync(draft => draft.Id == article.DraftId, Ct)).Status = LongFormDraftStatus.Writing;
            await db.SaveChangesAsync(Ct);
        }

        await DispatchAsync();

        var after = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.Writing, after.Status);
        Assert.Equal(ContentStaleReasons.None, after.StaleReasons);
        Assert.Empty(await AuditAsync(WsA, article.DraftId));
    }

    // ---- Replay and order ----

    [Fact]
    public async Task A_replayed_delivery_changes_nothing()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);
        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        var marked = await DraftAsync(WsA, article.DraftId);
        var message = Assert.Single(await OutboxFor(recipe.RecipeId));

        await DeliverAsync(message);
        await DeliverAsync(message);

        var again = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(marked.StaleSince, again.StaleSince);
        Assert.Equal(marked.UpdatedAt, again.UpdatedAt);
        Assert.Equal(Convert.ToHexString(marked.RowVersion), Convert.ToHexString(again.RowVersion));
        Assert.Single(await AuditAsync(WsA, article.DraftId));
    }

    [Fact]
    public async Task An_old_event_does_not_mark_an_article_accepted_against_the_latest_version()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var second = await EditAsync(WsA, recipe.RecipeId);

        // Accepted after the edit, against the version the edit wrote: it is current.
        var article = await SeedAcceptedAsync(WsA, [recipe with { VersionId = second.Id }]);

        // The edit's own event, delivered late and then again: it names a version the article already has.
        await DispatchAsync();
        await DeliverAsync(Assert.Single(await OutboxFor(recipe.RecipeId)));

        var draft = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.Accepted, draft.Status);
        Assert.Equal(ContentStaleReasons.None, draft.StaleReasons);
        Assert.Empty(await AuditAsync(WsA, article.DraftId));
    }

    [Fact]
    public async Task Events_delivered_out_of_order_decide_the_same_thing()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe]);
        await EditAsync(WsA, recipe.RecipeId);
        await EditAsync(WsA, recipe.RecipeId);

        var messages = (await OutboxFor(recipe.RecipeId))
            .OrderByDescending(message => RecipeVersionChangedEvent.TryParse(message.PayloadJson)!.VersionNumber)
            .ToList();
        Assert.Equal(2, messages.Count);

        // Newest first, then the older one.
        foreach (var message in messages)
        {
            await DeliverAsync(message);
        }

        Assert.Equal(LongFormDraftStatus.NeedsReview, (await DraftAsync(WsA, article.DraftId)).Status);
        Assert.Single(await AuditAsync(WsA, article.DraftId));
    }

    // ---- Which articles ----

    [Theory]
    [InlineData(LongFormDraftStatus.Draft)]
    [InlineData(LongFormDraftStatus.Outlined)]
    [InlineData(LongFormDraftStatus.Writing)]
    [InlineData(LongFormDraftStatus.Assembled)]
    public async Task A_draft_that_was_never_accepted_does_not_go_stale(LongFormDraftStatus status)
    {
        var recipe = await CreateRecipeAsync(WsA);
        var draft = await SeedAsync(WsA, [recipe], status, accepted: false);

        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        var after = await DraftAsync(WsA, draft.DraftId);
        Assert.Equal(status, after.Status);
        Assert.Equal(ContentStaleReasons.None, after.StaleReasons);
        Assert.Null(after.StaleSince);
        Assert.Empty(await AuditAsync(WsA, draft.DraftId));
    }

    [Fact]
    public async Task An_article_reopened_for_writing_keeps_its_accepted_pointer_and_is_not_marked()
    {
        var recipe = await CreateRecipeAsync(WsA);

        // Accepted once and since reopened: the pointer is retained, and it is not an article of record now.
        var draft = await SeedAsync(WsA, [recipe], LongFormDraftStatus.Writing, accepted: true);

        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        Assert.Equal(LongFormDraftStatus.Writing, (await DraftAsync(WsA, draft.DraftId)).Status);
        Assert.Empty(await AuditAsync(WsA, draft.DraftId));
    }

    [Fact]
    public async Task The_document_is_the_unit_an_article_written_from_several_recipes_is_marked_once_by_any_of_them()
    {
        var first = await CreateRecipeAsync(WsA);
        var second = await CreateRecipeAsync(WsA);
        var unrelated = await CreateRecipeAsync(WsA);
        var roundup = await SeedAcceptedAsync(WsA, [first, second]);
        var other = await SeedAcceptedAsync(WsA, [unrelated]);

        await EditAsync(WsA, second.RecipeId);
        await DispatchAsync();

        Assert.Equal(LongFormDraftStatus.NeedsReview, (await DraftAsync(WsA, roundup.DraftId)).Status);

        // The first recipe moving too adds nothing: the reason is already recorded, and the piece is one piece.
        await EditAsync(WsA, first.RecipeId);
        await DispatchAsync();

        Assert.Single(await AuditAsync(WsA, roundup.DraftId));

        // An article about a recipe that did not move is still current.
        Assert.Equal(LongFormDraftStatus.Accepted, (await DraftAsync(WsA, other.DraftId)).Status);
    }

    // ---- The brand style guide ----

    [Fact]
    public async Task A_new_active_style_guide_marks_articles_written_under_another_and_leaves_those_written_under_it()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var (oldVoice, newVoice) = await SeedStyleGuideVersionsAsync(WsA);

        var underOld = await SeedAcceptedAsync(WsA, [recipe], oldVoice);
        var underNew = await SeedAcceptedAsync(WsA, [recipe], newVoice);
        var underNone = await SeedAcceptedAsync(WsA, [recipe]);
        var unaccepted = await SeedAsync(WsA, [recipe], LongFormDraftStatus.Writing, accepted: false, oldVoice);
        var before = await SnapshotAsync(WsA, underOld);

        await VoiceChangedAsync(WsA, newVoice);

        var marked = await DraftAsync(WsA, underOld.DraftId);
        Assert.Equal(LongFormDraftStatus.NeedsReview, marked.Status);
        Assert.Equal(ContentStaleReasons.VoiceChanged, marked.StaleReasons);
        Assert.Equal(before.Revisions, (await SnapshotAsync(WsA, underOld)).Revisions);

        // Written with no guide in force is not current against one activated since: null is a value.
        Assert.Equal(ContentStaleReasons.VoiceChanged, (await DraftAsync(WsA, underNone.DraftId)).StaleReasons);

        Assert.Equal(LongFormDraftStatus.Accepted, (await DraftAsync(WsA, underNew.DraftId)).Status);
        Assert.Equal(LongFormDraftStatus.Writing, (await DraftAsync(WsA, unaccepted.DraftId)).Status);

        // Delivered again, and then an older activation delivered late with the same active version: nothing.
        await VoiceChangedAsync(WsA, newVoice);
        await VoiceChangedAsync(WsA, newVoice);

        Assert.Single(await AuditAsync(WsA, underOld.DraftId));
        Assert.Empty(await AuditAsync(WsA, underNew.DraftId));
        Assert.Equal([ContentStaleReasons.VoiceChanged], (await DocumentAsync(WsA, underOld.DraftId))!.StaleReasons);
    }

    [Fact]
    public async Task A_second_pin_moving_adds_its_reason_and_keeps_the_day_the_article_first_went_stale()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var (oldVoice, newVoice) = await SeedStyleGuideVersionsAsync(WsA);
        var article = await SeedAcceptedAsync(WsA, [recipe], oldVoice);

        await EditAsync(WsA, recipe.RecipeId);
        await DispatchAsync();
        var first = await DraftAsync(WsA, article.DraftId);

        await VoiceChangedAsync(WsA, newVoice);

        var both = await DraftAsync(WsA, article.DraftId);
        Assert.Equal(LongFormDraftStatus.NeedsReview, both.Status);
        Assert.Equal(ContentStaleReasons.RecipeChanged | ContentStaleReasons.VoiceChanged, both.StaleReasons);
        Assert.Equal(first.StaleSince, both.StaleSince);

        // Both are named to the creator, each once.
        Assert.Equal(
            [ContentStaleReasons.RecipeChanged, ContentStaleReasons.VoiceChanged],
            (await DocumentAsync(WsA, article.DraftId))!.StaleReasons);
        Assert.Equal(2, (await AuditAsync(WsA, article.DraftId)).Count);

        // Each replayed: still two.
        await VoiceChangedAsync(WsA, newVoice);
        await DeliverAsync(Assert.Single(await OutboxFor(recipe.RecipeId)));
        Assert.Equal(2, (await AuditAsync(WsA, article.DraftId)).Count);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_change_in_one_workspace_never_marks_the_other_workspaces_articles()
    {
        var recipeA = await CreateRecipeAsync(WsA);
        var recipeB = await CreateRecipeAsync(WsB);
        var (oldVoiceA, _) = await SeedStyleGuideVersionsAsync(WsA);
        var (_, newVoiceB) = await SeedStyleGuideVersionsAsync(WsB);
        var articleA = await SeedAcceptedAsync(WsA, [recipeA], oldVoiceA);
        var articleB = await SeedAcceptedAsync(WsB, [recipeB]);

        // B's recipe moves and B's active guide changes. Both are B's business.
        await EditAsync(WsB, recipeB.RecipeId);
        await DispatchAsync();
        await VoiceChangedAsync(WsB, newVoiceB);

        Assert.Equal(LongFormDraftStatus.NeedsReview, (await DraftAsync(WsB, articleB.DraftId)).Status);

        var untouched = await DraftAsync(WsA, articleA.DraftId);
        Assert.Equal(LongFormDraftStatus.Accepted, untouched.Status);
        Assert.Equal(ContentStaleReasons.None, untouched.StaleReasons);
        Assert.Empty(await AuditAsync(WsA, articleA.DraftId));

        // And B cannot see A's article to be told anything about it.
        Assert.Null(await DocumentAsync(WsB, articleA.DraftId));
    }

    [Fact]
    public async Task A_payload_claiming_another_workspace_for_a_recipe_marks_nothing_in_either()
    {
        var recipeA = await CreateRecipeAsync(WsA);
        var recipeB = await CreateRecipeAsync(WsB);
        var articleA = await SeedAcceptedAsync(WsA, [recipeA]);
        var articleB = await SeedAcceptedAsync(WsB, [recipeB]);
        await EditAsync(WsA, recipeA.RecipeId);

        // Tampered: workspace B's id over workspace A's recipe. The handler resolves B, and every read below it
        // runs under B's filter, so A's recipe, its versions and its article are all invisible.
        var real = Assert.Single(await OutboxFor(recipeA.RecipeId));
        var forged = RecipeVersionChangedEvent.TryParse(real.PayloadJson)! with { WorkspaceId = WsB };

        await DeliverAsync(new OutboxMessage
        {
            Id = real.Id,
            Type = real.Type,
            PayloadJson = forged.Serialize(),
            CorrelationId = real.CorrelationId,
            Attempts = real.Attempts,
        });

        Assert.Equal(LongFormDraftStatus.Accepted, (await DraftAsync(WsA, articleA.DraftId)).Status);
        Assert.Equal(LongFormDraftStatus.Accepted, (await DraftAsync(WsB, articleB.DraftId)).Status);
    }

    // ---- Helpers ----

    private sealed record SeededArticle(Guid DraftId, Guid RevisionId);

    private sealed record Snapshot(
        DateTimeOffset? AcceptedAt,
        IReadOnlyList<(Guid Id, int RevisionNumber, string Content, Guid? Voice)> Revisions,
        IReadOnlyList<(Guid Id, Guid RecipeId, Guid RecipeVersionId, int SortOrder)> Pins)
    {
        public bool Equals(Snapshot? other) =>
            other is not null && AcceptedAt == other.AcceptedAt && Revisions.SequenceEqual(other.Revisions) && Pins.SequenceEqual(other.Pins);

        public override int GetHashCode() => HashCode.Combine(AcceptedAt, Revisions.Count, Pins.Count);
    }

    private Task<SeededArticle> SeedAcceptedAsync(Guid workspaceId, CreatedRecipe[] recipes, Guid? voice = null) =>
        SeedAsync(workspaceId, recipes, LongFormDraftStatus.Accepted, accepted: true, voice);

    /// <summary>
    /// A draft with one revision pinned to each recipe, at the status asked for. Written as rows: the seams
    /// that plan, write and accept a piece have their own tests, and none of them is what is under test here.
    /// </summary>
    private async Task<SeededArticle> SeedAsync(
        Guid workspaceId, CreatedRecipe[] recipes, LongFormDraftStatus status, bool accepted, Guid? voice = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);

        var context = CreativeContextSeeds.NewContext(Now);
        db.CreativeContexts.Add(context);
        await db.SaveChangesAsync(Ct);

        var draft = new LongFormDraft
        {
            Id = Guid.NewGuid(),
            CreativeContextId = context.Id,
            Kind = LongFormKind.BlogArticle,
            Status = LongFormDraftStatus.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        var revision = new LongFormRevision
        {
            Id = Guid.NewGuid(),
            LongFormDraftId = draft.Id,
            RevisionNumber = 1,
            Source = ContentRevisionSource.CreatorEdit,
            BrandStyleGuideVersionId = voice,
            SchemaVersion = LongFormDocument.CurrentSchemaVersion,
            Content = new LongFormDocument(
            [
                new(Guid.NewGuid(), LongFormSectionRole.Hook, "Start here", "Open with the smell of it.", AcceptedWords, false, null,
                    new(ContentRevisionSource.CreatorEdit, null, null, null, null, null, 1)),
            ]).Serialize(),
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

        db.AddRange(draft, revision);
        db.AddRange(recipes.Select((recipe, index) => new LongFormRevisionRecipePin
        {
            Id = Guid.NewGuid(),
            LongFormRevisionId = revision.Id,
            SortOrder = index,
            RecipeId = recipe.RecipeId,
            RecipeVersionId = recipe.VersionId,
            CreativeContextReferenceId = Guid.NewGuid(),
        }));
        await db.SaveChangesAsync(Ct);

        // The pointer after the revision exists, as an acceptance writes it; then the status under test.
        if (accepted)
        {
            draft.AcceptedRevisionId = revision.Id;
            draft.AcceptedAt = Now;
        }

        draft.Status = status;
        await db.SaveChangesAsync(Ct);

        return new SeededArticle(draft.Id, revision.Id);
    }

    /// <summary>Two versions of one guide, as rows: something for a revision's pin to name.</summary>
    private async Task<(Guid Old, Guid New)> SeedStyleGuideVersionsAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);
        var author = Guid.NewGuid();

        var guide = new BrandStyleGuide
        {
            Id = Guid.NewGuid(),
            DisplayName = $"House voice {Guid.NewGuid():N}",
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = author,
            UpdatedByMembershipId = author,
        };

        var first = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(), BrandStyleGuideId = guide.Id, VersionNumber = 1, CreatedByMembershipId = author, CreatedAt = Now,
        };

        var second = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(), BrandStyleGuideId = guide.Id, VersionNumber = 2, ParentVersionId = first.Id,
            CreatedByMembershipId = author, CreatedAt = Now,
        };

        db.Add(guide);
        await db.SaveChangesAsync(Ct);
        db.Add(first);
        await db.SaveChangesAsync(Ct);
        db.Add(second);
        await db.SaveChangesAsync(Ct);

        return (first.Id, second.Id);
    }

    /// <summary>
    /// What the brand module's handler does once it has resolved the workspace and read the active version:
    /// hands that version to the consumer.
    /// </summary>
    private async Task VoiceChangedAsync(Guid workspaceId, Guid? activeVersionId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        foreach (var consumer in scope.ServiceProvider.GetServices<IBrandVoiceChangeConsumer>())
        {
            await consumer.OnActiveStyleGuideChangedAsync(activeVersionId, Ct);
        }
    }

    private async Task<CreatedRecipe> CreateRecipeAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var tagId = workspaceId == WsA ? SqlServerRecipeFixture.TagIdA : SqlServerRecipeFixture.TagIdB;

        return await scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>().CreateAsync(
            SqlServerRecipeFixture.NewRecipe(
                "Olive oil cake", tagId, SqlServerRecipeFixture.MediaAssetIdFor(workspaceId)),
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

    private async Task<LongFormDraft> DraftAsync(Guid workspaceId, Guid draftId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await SqlServerRecipeFixture.Db(scope).LongFormDrafts.AsNoTracking().SingleAsync(draft => draft.Id == draftId, Ct);
    }

    private async Task<LongFormAssembledDocumentServiceModel?> DocumentAsync(Guid workspaceId, Guid draftId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var read = await scope.ServiceProvider.GetRequiredService<ILongFormDocumentFacade>().GetDocumentAsync(draftId, Ct);

        return read.Succeeded ? read.Value : null;
    }

    private async Task<Snapshot> SnapshotAsync(Guid workspaceId, SeededArticle article)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);

        var draft = await db.LongFormDrafts.AsNoTracking().SingleAsync(candidate => candidate.Id == article.DraftId, Ct);
        var revisions = await db.LongFormRevisions.AsNoTracking()
            .Where(revision => revision.LongFormDraftId == article.DraftId)
            .OrderBy(revision => revision.RevisionNumber)
            .ToListAsync(Ct);
        var pins = await db.LongFormRevisionRecipePins.AsNoTracking()
            .Where(pin => pin.LongFormRevisionId == article.RevisionId)
            .OrderBy(pin => pin.SortOrder)
            .ToListAsync(Ct);

        return new Snapshot(
            draft.AcceptedAt,
            [.. revisions.Select(revision => (revision.Id, revision.RevisionNumber, revision.Content, revision.BrandStyleGuideVersionId))],
            [.. pins.Select(pin => (pin.Id, pin.RecipeId, pin.RecipeVersionId, pin.SortOrder))]);
    }

    private async Task<List<AuditLog>> AuditAsync(Guid workspaceId, Guid draftId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var resourceId = draftId.ToString("D");

        return await SqlServerRecipeFixture.Db(scope).AuditLogs.AsNoTracking()
            .Where(entry => entry.ResourceId == resourceId && entry.Action == ContentAuditActions.LongFormDraftMarkedStale)
            .ToListAsync(Ct);
    }

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

        await handler.HandleAsync(Envelope(message), Ct);
    }

    private static OutboxMessageEnvelope Envelope(OutboxMessage message) =>
        new(message.Id, message.Type, message.PayloadJson, message.CorrelationId, message.Attempts + 1);

    /// <summary>
    /// Writes the draft's row from another connection just before the consumer's own save, once: the race a
    /// creator's edit, a second delivery or another worker would be.
    /// </summary>
    private sealed class WriteDraftFirst(SqlServerRecipeFixture fixture, Guid workspaceId, Guid draftId) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done && eventData.Context!.ChangeTracker.Entries<LongFormDraft>().Any(entry => entry.State is EntityState.Modified))
            {
                _done = true;

                await using var other = fixture.ScopeFor(workspaceId);

                // Any write moves the row version. Nothing about the piece changes.
                await SqlServerRecipeFixture.Db(other).Database.ExecuteSqlAsync(
                    $"UPDATE LongFormDrafts SET UpdatedAt = UpdatedAt WHERE Id = {draftId}", cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Refuses any save that would change a long-form draft: the consumer's write, failing.</summary>
    private sealed class ThrowOnLongFormDraftUpdate : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LongFormDraft>().Any(entry => entry.State is EntityState.Modified))
            {
                throw new InvalidOperationException("Simulated failure writing the long-form draft.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
