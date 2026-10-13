using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// 13.4 against a real SQL Server: the content card, its immutable move history and its recipe package pins, as
/// the migration built them.
/// </summary>
/// <remarks>
/// There is no write seam yet — that arrives with 13.5 onward — so these tests write rows directly and assert
/// what the model and the engine refuse. SQL Server rather than SQLite because the migration is what is under
/// test: five tables that reach one workspace by a single cascade path each, restricted keys into four other
/// modules' tables, and a row version that has to move. The fixture is shared, so every assertion is scoped to
/// rows the test made.
/// </remarks>
public sealed class ContentBoardTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The shape ----

    [Fact]
    public async Task A_card_round_trips_with_its_plan_and_every_record_it_points_at()
    {
        var contextId = await SeedContextAsync(WsA);
        var recipe = await SeedRecipeAsync(WsA);
        var assetId = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var promptId = await InAsync(WsA, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));

        var card = NewCard("Soda bread, Friday", ContentCardColumn.InProcess, rank: 2048);
        card.Description = "Hero shot and the three posts.";
        card.ChannelKey = "instagram";
        card.PlannedDate = new DateOnly(2026, 10, 16);
        card.PlannedDayOfWeek = DayOfWeek.Friday;
        card.CuisineCode = "irish";
        card.DishTypeCode = "bread";
        card.MethodCode = "baking";
        card.CreativeContextId = contextId;
        card.RecipeId = recipe.RecipeId;
        card.RecipeVersionId = recipe.VersionId;
        card.MediaAssetId = assetId;
        card.MediaAssetVersionNumber = 1;
        card.PromptRecordId = promptId;

        await InAsync(WsA, async db =>
        {
            db.AddRange(card, Created(card));
            await db.SaveChangesAsync(Ct);
        });

        var loaded = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));

        // Stamped by the ownership interceptor from the resolved context, never set by hand.
        Assert.Equal(WsA, loaded.WorkspaceId);
        Assert.Equal("Soda bread, Friday", loaded.Title);
        Assert.Equal(ContentCardColumn.InProcess, loaded.Column);
        Assert.Equal(2048, loaded.SortRank);
        Assert.Equal(new DateOnly(2026, 10, 16), loaded.PlannedDate);
        Assert.Equal(DayOfWeek.Friday, loaded.PlannedDayOfWeek);
        Assert.Equal(("irish", "bread", "baking"), (loaded.CuisineCode, loaded.DishTypeCode, loaded.MethodCode));
        Assert.Equal(recipe.VersionId, loaded.RecipeVersionId);
        Assert.Equal((assetId, 1), (loaded.MediaAssetId, loaded.MediaAssetVersionNumber));
        Assert.Equal(promptId, loaded.PromptRecordId);
        Assert.NotEmpty(loaded.RowVersion);
    }

    [Fact]
    public async Task A_card_needs_nothing_but_a_title_and_a_column()
    {
        var card = NewCard("An idea, nothing behind it yet", ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap);

        await InAsync(WsA, async db =>
        {
            db.Add(card);
            await db.SaveChangesAsync(Ct);
        });

        var loaded = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));
        Assert.Null(loaded.RecipeId);
        Assert.Null(loaded.CreativeContextId);
        Assert.Null(loaded.PlannedDate);
        Assert.Null(loaded.ChannelKey);
    }

    /// <summary>
    /// The restriction on this prompt, held by the model rather than by a reviewer remembering it: a card says
    /// where the creator's work stands, and what a provider did with it is a Publication record's to say.
    /// </summary>
    [Fact]
    public void A_card_carries_no_publishing_or_provider_state()
    {
        string[] forbidden = ["provider", "publish", "publication", "external", "delivery", "posted", "scheduled", "buffer"];

        var names = typeof(ContentCard).GetProperties().Select(property => property.Name)
            .Concat(typeof(CardMoveHistory).GetProperties().Select(property => property.Name));

        Assert.DoesNotContain(names, name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void The_columns_are_numbered_in_the_order_a_card_travels_and_zero_is_not_one()
    {
        Assert.Equal(
            [ContentCardColumn.ToDo, ContentCardColumn.InProcess, ContentCardColumn.ReadyForReview, ContentCardColumn.Approved],
            Enum.GetValues<ContentCardColumn>().OrderBy(column => (int)column));
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<ContentCardColumn>().Select(column => (int)column).Order());
    }

    [Theory]
    [InlineData("blank title")]
    [InlineData("no column")]
    [InlineData("column past the last")]
    [InlineData("day without a date")]
    [InlineData("date without a day")]
    [InlineData("day out of range")]
    [InlineData("recipe version without its recipe")]
    [InlineData("asset version without its asset")]
    [InlineData("asset version zero")]
    [InlineData("origin past the last")]
    [InlineData("pipeline card for no piece of work")]
    public async Task The_engine_refuses_a_card_whose_state_does_not_hold_together(string fault)
    {
        var card = NewCard("Soda bread", ContentCardColumn.ToDo, rank: 1024);

        switch (fault)
        {
            case "blank title": card.Title = "   "; break;
            case "no column": card.Column = default; break;
            case "column past the last": card.Column = (ContentCardColumn)5; break;
            case "day without a date": card.PlannedDayOfWeek = DayOfWeek.Friday; break;
            case "date without a day": card.PlannedDate = new DateOnly(2026, 10, 16); break;
            case "day out of range":
                card.PlannedDate = new DateOnly(2026, 10, 16);
                card.PlannedDayOfWeek = (DayOfWeek)7;
                break;
            case "origin past the last": card.Origin = (ContentCardOrigin)3; break;
            case "pipeline card for no piece of work": card.Origin = ContentCardOrigin.ContentPipeline; break;
            case "recipe version without its recipe": card.RecipeVersionId = Guid.NewGuid(); break;
            case "asset version without its asset": card.MediaAssetVersionNumber = 1; break;
            case "asset version zero":
                card.MediaAssetId = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
                card.MediaAssetVersionNumber = 0;
                break;
        }

        await AssertEngineRefusesAsync(WsA, card);
    }

    [Fact]
    public async Task A_pinned_version_must_be_a_version_of_the_record_the_card_names()
    {
        var first = await SeedRecipeAsync(WsA);
        var second = await SeedRecipeAsync(WsA);
        var assetId = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));

        var wrongRecipeVersion = NewCard("Mismatched", ContentCardColumn.ToDo, rank: 1024);
        wrongRecipeVersion.RecipeId = first.RecipeId;
        wrongRecipeVersion.RecipeVersionId = second.VersionId;
        await AssertEngineRefusesAsync(WsA, wrongRecipeVersion);

        var missingAssetVersion = NewCard("No such version", ContentCardColumn.ToDo, rank: 1024);
        missingAssetVersion.MediaAssetId = assetId;
        missingAssetVersion.MediaAssetVersionNumber = 9;
        await AssertEngineRefusesAsync(WsA, missingAssetVersion);
    }

    // ---- Ordering ----

    [Fact]
    public async Task A_board_reads_in_column_then_rank_then_id_so_equal_ranks_still_have_one_order()
    {
        var marker = $"order-{Guid.NewGuid():N}";
        var cards = new[]
        {
            NewCard(marker, ContentCardColumn.Approved, rank: 1024),
            NewCard(marker, ContentCardColumn.ToDo, rank: 2048),
            NewCard(marker, ContentCardColumn.ToDo, rank: 1024),

            // Two cards dropped into the same slot at once: the same column and the same rank.
            NewCard(marker, ContentCardColumn.InProcess, rank: 1536),
            NewCard(marker, ContentCardColumn.InProcess, rank: 1536),
        };

        await InAsync(WsA, async db =>
        {
            db.AddRange(cards);
            await db.SaveChangesAsync(Ct);
        });

        Task<List<Guid>> ReadAsync() => InAsync(WsA, db => db.ContentCards.AsNoTracking()
            .Where(card => card.Title == marker)
            .OrderBy(card => card.Column).ThenBy(card => card.SortRank).ThenBy(card => card.Id)
            .Select(card => card.Id)
            .ToListAsync(Ct));

        var first = await ReadAsync();
        var second = await ReadAsync();

        Assert.Equal(first, second);
        Assert.Equal(cards[2].Id, first[0]);
        Assert.Equal(cards[1].Id, first[1]);
        Assert.Equal(new[] { cards[3].Id, cards[4].Id }.Order(), first.Skip(2).Take(2).Order());
        Assert.Equal(cards[0].Id, first[4]);
    }

    [Fact]
    public async Task A_move_between_two_cards_takes_the_rank_between_them_and_writes_one_row()
    {
        var marker = $"midpoint-{Guid.NewGuid():N}";
        var above = NewCard(marker, ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap);
        var below = NewCard(marker, ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap * 2);
        var moved = NewCard(marker, ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap * 3);

        await InAsync(WsA, async db =>
        {
            db.AddRange(above, below, moved);
            await db.SaveChangesAsync(Ct);
        });

        var untouched = await InAsync(WsA, db => db.ContentCards.AsNoTracking()
            .Where(card => card.Id == above.Id || card.Id == below.Id)
            .ToDictionaryAsync(card => card.Id, card => card.RowVersion, Ct));

        await InAsync(WsA, async db =>
        {
            var tracked = await db.ContentCards.SingleAsync(card => card.Id == moved.Id, Ct);
            tracked.SortRank = (above.SortRank + below.SortRank) / 2;
            Assert.Equal(1, await db.SaveChangesAsync(Ct));
        });

        var after = await InAsync(WsA, db => db.ContentCards.AsNoTracking()
            .Where(card => card.Title == marker)
            .OrderBy(card => card.Column).ThenBy(card => card.SortRank).ThenBy(card => card.Id)
            .ToListAsync(Ct));

        Assert.Equal([above.Id, moved.Id, below.Id], after.Select(card => card.Id));

        // The neighbours were not written, so nobody editing one of them is told it changed.
        Assert.Equal(untouched[above.Id], after[0].RowVersion);
        Assert.Equal(untouched[below.Id], after[2].RowVersion);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task A_stale_write_to_a_card_is_refused_and_the_row_version_moves()
    {
        var card = await SeedCardAsync(WsA);

        await using var firstScope = fixture.ScopeFor(WsA);
        await using var secondScope = fixture.ScopeFor(WsA);
        var firstDb = SqlServerRecipeFixture.Db(firstScope);
        var secondDb = SqlServerRecipeFixture.Db(secondScope);

        var first = await firstDb.ContentCards.SingleAsync(item => item.Id == card.Id, Ct);
        var second = await secondDb.ContentCards.SingleAsync(item => item.Id == card.Id, Ct);
        var readWith = first.RowVersion.ToArray();

        first.Column = ContentCardColumn.InProcess;
        await firstDb.SaveChangesAsync(Ct);

        Assert.NotEqual(readWith, first.RowVersion);

        // Read before the first write landed, so its UPDATE quotes a version the row no longer has.
        second.Title = "Renamed from an old read";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));

        var stored = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));
        Assert.Equal(ContentCardColumn.InProcess, stored.Column);
        Assert.Equal(card.Title, stored.Title);
    }

    /// <summary>
    /// 13.6a's write on the engine whose row version really moves, which the HTTP fixture's SQLite store cannot
    /// show: an edit hands back a new token, and of two edits composed against the same read the second writes
    /// nothing — not the card, and not its audit entry.
    /// </summary>
    [Fact]
    public async Task Of_two_edits_composed_against_one_read_the_second_is_stale_and_writes_nothing()
    {
        var card = await SeedCardAsync(WsA);

        await using var firstScope = fixture.ScopeFor(WsA);
        await using var secondScope = fixture.ScopeFor(WsA);
        var first = firstScope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();
        var second = secondScope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        var mine = (await first.FindForUpdateAsync(card.Id, Ct))!;
        var theirs = (await second.FindForUpdateAsync(card.Id, Ct))!;
        var readWith = ContentCardConcurrencyToken.From(mine.RowVersion);
        Assert.Equal(readWith, ContentCardConcurrencyToken.From(theirs.RowVersion));

        mine.Title = "Edited first";
        Assert.Equal(ContentCardWrite.Saved, await first.SaveAsync(mine, Edit(card, "first"), Ct));

        // The token a client would be handed back is not the one it sent.
        Assert.NotEqual(readWith, ContentCardConcurrencyToken.From(mine.RowVersion));
        Assert.False(ContentCardConcurrencyToken.Matches(readWith, mine.RowVersion));

        // Read before the first write landed, so its UPDATE quotes a version the row no longer has.
        theirs.Title = "Edited second";
        Assert.Equal(ContentCardWrite.Stale, await second.SaveAsync(theirs, Edit(card, "second"), Ct));

        var stored = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));
        Assert.Equal("Edited first", stored.Title);

        // The audit entry is in the same save as the card, so the edit that did not happen left none.
        var summaries = await InAsync(WsA, db => db.AuditLogs.AsNoTracking()
            .Where(log => log.ResourceId == card.Id.ToString("D"))
            .Select(log => log.Summary)
            .ToListAsync(Ct));
        Assert.Equal(["first"], summaries);

        // And nothing was left staged in the scope that lost: its next save writes nothing at all.
        Assert.Equal(0, await SqlServerRecipeFixture.Db(secondScope).SaveChangesAsync(Ct));

        static AuditEntry Edit(ContentCard edited, string summary) => new(
            "test-account",
            ContentAuditActions.ContentCardUpdated,
            ContentAuditActions.ContentCardResourceType,
            edited.Id.ToString("D"),
            Guid.NewGuid(),
            summary);
    }

    // ---- Moving (13.6b), on the engine where two writers can really collide ----

    [Fact]
    public async Task Of_two_moves_of_one_card_composed_against_one_read_only_the_first_is_recorded()
    {
        var card = await SeedCardAsync(WsA);

        await using var firstScope = fixture.ScopeFor(WsA);
        await using var secondScope = fixture.ScopeFor(WsA);
        var first = firstScope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();
        var second = secondScope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        var mine = (await first.FindForUpdateAsync(card.Id, Ct))!;
        var theirs = (await second.FindForUpdateAsync(card.Id, Ct))!;

        // Both read the same history, so both take the same next place in it.
        Assert.Equal(1, await first.LastMoveSequenceAsync(card.Id, Ct));
        Assert.Equal(1, await second.LastMoveSequenceAsync(card.Id, Ct));

        mine.Column = ContentCardColumn.InProcess;
        Assert.Equal(
            ContentCardWrite.Saved,
            await first.SaveMoveAsync(mine, Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess, "First."), audit: null, Ct));

        theirs.Column = ContentCardColumn.Approved;
        Assert.NotEqual(
            ContentCardWrite.Saved,
            await second.SaveMoveAsync(theirs, Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.Approved, "Second."), audit: null, Ct));

        // The card is where the first mover put it, and its history says so once.
        var stored = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));
        Assert.Equal(ContentCardColumn.InProcess, stored.Column);

        var history = await InAsync(WsA, db => db.CardMoveHistory.AsNoTracking()
            .Where(move => move.ContentCardId == card.Id)
            .OrderBy(move => move.Sequence)
            .ToListAsync(Ct));
        Assert.Equal([1, 2], history.Select(move => move.Sequence));
        Assert.Equal("First.", history[1].Note);

        // Nothing of the move that lost was left staged.
        Assert.Equal(0, await SqlServerRecipeFixture.Db(secondScope).SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_re_spacing_that_collides_with_an_edit_of_a_neighbour_writes_nothing_at_all()
    {
        var marker = $"respace-{Guid.NewGuid():N}";
        var upper = NewCard(marker, ContentCardColumn.ReadyForReview, rank: 1024);
        var lower = NewCard(marker, ContentCardColumn.ReadyForReview, rank: 1025);
        var mover = await SeedCardAsync(WsA);

        await InAsync(WsA, async db =>
        {
            db.AddRange(upper, lower);
            await db.SaveChangesAsync(Ct);
        });

        await using var moving = fixture.ScopeFor(WsA);
        var layer = moving.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        // The mover plans its place between two cards with no room, so they have to be re-spaced. The fixture
        // is shared, so the plan is made over this test's own two cards only.
        var card = (await layer.FindForUpdateAsync(mover.Id, Ct))!;
        var slots = (await layer.ListColumnSlotsAsync(ContentCardColumn.ReadyForReview, Ct))
            .Where(slot => slot.CardId == upper.Id || slot.CardId == lower.Id)
            .ToList();
        var placement = ContentCardPlacer.Place(slots, card.Id, lower.Id);
        Assert.Equal(ContentCardPlacementKind.Rebalance, placement.Kind);

        var neighbours = (await layer.ListColumnForUpdateAsync(ContentCardColumn.ReadyForReview, card.Id, Ct))
            .Where(other => other.Id == upper.Id || other.Id == lower.Id)
            .ToList();
        Assert.Equal(2, neighbours.Count);

        // Somebody edits one of those neighbours before the move is saved.
        await InAsync(WsA, async db =>
        {
            (await db.ContentCards.SingleAsync(item => item.Id == lower.Id, Ct)).Title = $"{marker} edited";
            await db.SaveChangesAsync(Ct);
        });

        foreach (var neighbour in neighbours)
        {
            neighbour.SortRank = placement.Respaced[neighbour.Id];
        }

        card.Column = ContentCardColumn.ReadyForReview;
        card.SortRank = placement.SortRank;

        var write = await layer.SaveMoveAsync(
            card, Moved(mover, 2, ContentCardColumn.ToDo, ContentCardColumn.ReadyForReview), audit: null, Ct);

        Assert.Equal(ContentCardWrite.Stale, write);

        // Not the mover, not the neighbour that was untouched, not the history: the save is one thing.
        var after = await InAsync(WsA, db => db.ContentCards.AsNoTracking()
            .Where(item => item.Id == mover.Id || item.Id == upper.Id || item.Id == lower.Id)
            .ToDictionaryAsync(item => item.Id, Ct));

        Assert.Equal(ContentCardColumn.ToDo, after[mover.Id].Column);
        Assert.Equal(mover.SortRank, after[mover.Id].SortRank);
        Assert.Equal(1024, after[upper.Id].SortRank);
        Assert.Equal(1025, after[lower.Id].SortRank);
        Assert.Equal($"{marker} edited", after[lower.Id].Title);
        Assert.Equal(1, await InAsync(WsA, db => db.CardMoveHistory.CountAsync(move => move.ContentCardId == mover.Id, Ct)));
    }

    [Fact]
    public async Task A_move_whose_history_cannot_be_written_does_not_move_the_card()
    {
        var card = await SeedCardAsync(WsA);

        await using var scope = fixture.ScopeFor(WsA, new FailOnMoveHistoryInsert());
        var layer = scope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        var tracked = (await layer.FindForUpdateAsync(card.Id, Ct))!;
        tracked.Column = ContentCardColumn.Approved;
        tracked.SortRank = 99;

        var write = await layer.SaveMoveAsync(
            tracked, Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.Approved), audit: null, Ct);

        Assert.Equal(ContentCardWrite.Refused, write);

        var stored = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));
        Assert.Equal((ContentCardColumn.ToDo, card.SortRank), (stored.Column, stored.SortRank));
        Assert.Equal(1, await InAsync(WsA, db => db.CardMoveHistory.CountAsync(move => move.ContentCardId == card.Id, Ct)));
    }

    /// <summary>
    /// Cards that share a rank are ordered by id, and SQL Server does not order a <c>uniqueidentifier</c> the
    /// way .NET orders a <c>Guid</c>. The placement is given the column in the store's order and never sorts
    /// ids itself, so "before that card" lands where every reader of the board will then see it.
    /// </summary>
    [Fact]
    public async Task A_place_among_cards_that_share_a_rank_lands_where_the_store_orders_them()
    {
        var workspaceId = Guid.NewGuid();
        await InAsync(WsA, async db =>
        {
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Ties", Slug = $"ties-{workspaceId:N}", CreatedAt = Now });
            await db.SaveChangesAsync(Ct);
        });

        var tied = Enumerable.Range(0, 6).Select(_ => NewCard("Tied", ContentCardColumn.InProcess, rank: 1024)).ToList();
        var mover = NewCard("Mover", ContentCardColumn.ToDo, rank: 1024);

        await InAsync(workspaceId, async db =>
        {
            db.AddRange(tied);
            db.Add(mover);
            await db.SaveChangesAsync(Ct);
        });

        await using var scope = fixture.ScopeFor(workspaceId);
        var layer = scope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        var before = await layer.ListColumnSlotsAsync(ContentCardColumn.InProcess, Ct);
        Assert.Equal(6, before.Count);

        // Before the fourth card as the store reads them — all six share one rank, so the column is re-spaced.
        var target = before[3].CardId;
        var placement = ContentCardPlacer.Place(before, mover.Id, target);
        Assert.Equal(ContentCardPlacementKind.Rebalance, placement.Kind);

        var card = (await layer.FindForUpdateAsync(mover.Id, Ct))!;
        foreach (var other in await layer.ListColumnForUpdateAsync(ContentCardColumn.InProcess, card.Id, Ct))
        {
            other.SortRank = placement.Respaced[other.Id];
        }

        card.Column = ContentCardColumn.InProcess;
        card.SortRank = placement.SortRank;
        Assert.Equal(ContentCardWrite.Saved, await layer.SaveMoveAsync(card, entered: null, audit: null, Ct));

        var after = await layer.ListColumnSlotsAsync(ContentCardColumn.InProcess, Ct);

        // The six keep the order they had, with the mover fourth: immediately before the card it named.
        Assert.Equal(
            [.. before.Take(3).Select(slot => slot.CardId), mover.Id, .. before.Skip(3).Select(slot => slot.CardId)],
            after.Select(slot => slot.CardId));
        Assert.Equal(after.Count, after.Select(slot => slot.SortRank).Distinct().Count());
    }

    // ---- The pipeline's card (13.7a), on the engine where two commits can really collide ----

    [Fact]
    public async Task The_store_keeps_one_pipeline_card_per_piece_of_work_and_any_number_made_by_hand()
    {
        var contextId = await SeedContextAsync(WsA);
        var otherContextId = await SeedContextAsync(WsA);

        ContentCard For(Guid context, ContentCardOrigin origin)
        {
            var card = NewCard("Soda bread", ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap);
            card.CreativeContextId = context;
            card.Origin = origin;

            return card;
        }

        var pipeline = For(contextId, ContentCardOrigin.ContentPipeline);
        await InAsync(WsA, async db =>
        {
            // The pipeline's card, two a creator made for the same work, and another work's pipeline card.
            db.AddRange(
                pipeline,
                For(contextId, ContentCardOrigin.Board),
                For(contextId, ContentCardOrigin.Board),
                For(otherContextId, ContentCardOrigin.ContentPipeline));
            await db.SaveChangesAsync(Ct);
        });

        // A second pipeline card for the first work is the one thing refused.
        await AssertEngineRefusesAsync(WsA, For(contextId, ContentCardOrigin.ContentPipeline));

        // A card that never said how it came to be here is one put there directly.
        var plain = NewCard("No origin given", ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap);
        plain.Origin = default;
        await InAsync(WsA, async db =>
        {
            db.Add(plain);
            await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(
            ContentCardOrigin.Board,
            await InAsync(WsA, db => db.ContentCards.AsNoTracking().Where(card => card.Id == plain.Id).Select(card => card.Origin).SingleAsync(Ct)));

        // And the seam finds the pipeline's card, not either of the creator's.
        await using var scope = fixture.ScopeFor(WsA);
        var dataLayer = scope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();
        Assert.Equal(pipeline.Id, await dataLayer.FindPipelineCardIdAsync(contextId, Ct));

        // From the other workspace the same work has no card at all.
        await using var elsewhere = fixture.ScopeFor(WsB);
        Assert.Null(await elsewhere.ServiceProvider.GetRequiredService<IContentCardDataLayer>().FindPipelineCardIdAsync(contextId, Ct));
    }

    [Fact]
    public async Task Of_two_pipeline_cards_written_at_once_for_one_piece_of_work_the_store_lets_one_through()
    {
        var contextId = await SeedContextAsync(WsA);

        async Task<(bool Saved, Guid CardId, int Staged)> WriteAsync()
        {
            await using var scope = fixture.ScopeFor(WsA);
            var dataLayer = scope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();
            var card = NewCard("Soda bread", ContentCardColumn.ToDo, rank: 0);
            card.CreativeContextId = contextId;
            card.Origin = ContentCardOrigin.ContentPipeline;

            var saved = await dataLayer.CreateAsync(
                card, Created(card), Entry(card, ContentAuditActions.ContentCardCreated, "commit"), Ct);

            // Whatever happened, nothing of it may ride along on this scope's next save.
            return (saved, card.Id, await SqlServerRecipeFixture.Db(scope).SaveChangesAsync(Ct));
        }

        // Several at once, so that at least two are past their read before any has written.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(WriteAsync, Ct)));

        var winner = Assert.Single(attempts, attempt => attempt.Saved);
        Assert.All(attempts, attempt => Assert.Equal(0, attempt.Staged));

        // One card, with its one creation row and its one audit entry; the losers left none of the three.
        var cards = await InAsync(WsA, db => db.ContentCards.AsNoTracking()
            .Where(card => card.CreativeContextId == contextId)
            .Select(card => card.Id)
            .ToListAsync(Ct));
        Assert.Equal([winner.CardId], cards);

        var ids = attempts.Select(attempt => attempt.CardId).ToList();
        var resources = ids.Select(id => id.ToString("D")).ToList();
        Assert.Equal(1, await InAsync(WsA, db => db.CardMoveHistory.CountAsync(move => ids.Contains(move.ContentCardId), Ct)));
        Assert.Equal(1, await InAsync(WsA, db => db.AuditLogs.CountAsync(log => resources.Contains(log.ResourceId), Ct)));

        // Which is what every loser then reads as its answer.
        await using var reading = fixture.ScopeFor(WsA);
        Assert.Equal(
            winner.CardId,
            await reading.ServiceProvider.GetRequiredService<IContentCardDataLayer>().FindPipelineCardIdAsync(contextId, Ct));
    }

    [Fact]
    public async Task What_a_commit_reads_about_a_piece_of_work_is_its_recipe_its_saved_keepers_in_order_and_its_accepted_posts()
    {
        var recipe = await SeedRecipeAsync(WsA);
        var first = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var second = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var cue = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var staged = await InAsync(WsA, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        var contextId = await InAsync(WsA, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now);

            CreativeContextReference Named(CreativeContextReferenceKind kind, int order, CreativeContextReferencePurpose purpose)
            {
                var reference = CreativeContextSeeds.Reference(context, kind, order, Now);
                reference.Purpose = purpose;

                return reference;
            }

            // A cue picture and a kept picture that was never saved sit among the keepers, and are not them.
            var cueRow = Named(CreativeContextReferenceKind.DamAsset, 0, CreativeContextReferencePurpose.Source);
            cueRow.MediaAssetId = cue;
            var recipeRow = Named(CreativeContextReferenceKind.Recipe, 1, CreativeContextReferencePurpose.Source);
            (recipeRow.RecipeId, recipeRow.RecipeVersionId) = (recipe.RecipeId, recipe.VersionId);
            var stagedRow = Named(CreativeContextReferenceKind.GeneratedImage, 2, CreativeContextReferencePurpose.Keeper);
            stagedRow.GeneratedImageId = staged;
            var secondRow = Named(CreativeContextReferenceKind.DamAsset, 4, CreativeContextReferencePurpose.Keeper);
            (secondRow.MediaAssetId, secondRow.MediaAssetVersionNumber) = (second, 1);
            var firstRow = Named(CreativeContextReferenceKind.DamAsset, 3, CreativeContextReferencePurpose.Keeper);
            (firstRow.MediaAssetId, firstRow.MediaAssetVersionNumber) = (first, 1);

            db.Add(context);
            db.AddRange(cueRow, recipeRow, stagedRow, secondRow, firstRow);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

        await using var scope = fixture.ScopeFor(WsA);
        var dataLayer = scope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();
        var source = (await dataLayer.FindPipelineSourceAsync(contextId, Ct))!;

        Assert.Equal("Soda bread, autumn", source.WorkingTitle);
        Assert.Equal((recipe.RecipeId, recipe.VersionId), (source.RecipeId, source.RecipeVersionId));
        Assert.Equal([first, second], source.Keepers.Select(keeper => keeper.MediaAssetId));
        Assert.Null(source.SocialPackageId);

        // The other workspace is told nothing about it: not the title, and not that it exists.
        await using var elsewhere = fixture.ScopeFor(WsB);
        Assert.Null(await elsewhere.ServiceProvider.GetRequiredService<IContentCardDataLayer>().FindPipelineSourceAsync(contextId, Ct));
    }

    // ---- Deleting (13.6c), on the engine where two writers can really collide ----

    [Fact]
    public async Task A_deletion_decided_against_a_card_that_has_since_changed_deletes_nothing()
    {
        var card = await SeedCardAsync(WsA);
        await InAsync(WsA, async db =>
        {
            db.Add(Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess));
            await db.SaveChangesAsync(Ct);
        });

        await using var editing = fixture.ScopeFor(WsA);
        await using var deleting = fixture.ScopeFor(WsA);
        var editor = editing.ServiceProvider.GetRequiredService<IContentCardDataLayer>();
        var deleter = deleting.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        var edited = (await editor.FindForUpdateAsync(card.Id, Ct))!;
        var doomed = (await deleter.FindForUpdateAsync(card.Id, Ct))!;

        // A collaborator's edit lands after the deleter read the card and before it deletes.
        edited.Title = "Edited while somebody decided to delete it";
        Assert.Equal(ContentCardWrite.Saved, await editor.SaveAsync(edited, Entry(card, ContentAuditActions.ContentCardUpdated, "edit"), Ct));

        var write = await deleter.DeleteAsync(doomed, Entry(card, ContentAuditActions.ContentCardDeleted, "delete"), Ct);

        Assert.Equal(ContentCardWrite.Stale, write);

        // The card, the collaborator's words, and both rows of its history are all still there.
        var stored = await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct));
        Assert.Equal("Edited while somebody decided to delete it", stored.Title);
        Assert.Equal(2, await InAsync(WsA, db => db.CardMoveHistory.CountAsync(move => move.ContentCardId == card.Id, Ct)));

        // And no deletion was audited: the entry was in the save that did not happen.
        var actions = await InAsync(WsA, db => db.AuditLogs.AsNoTracking()
            .Where(log => log.ResourceId == card.Id.ToString("D"))
            .Select(log => log.Action)
            .ToListAsync(Ct));
        Assert.Equal([ContentAuditActions.ContentCardUpdated], actions);

        // Nothing of the deletion was left staged in the scope that lost.
        Assert.Equal(0, await SqlServerRecipeFixture.Db(deleting).SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_delete_seam_takes_a_cards_history_and_package_without_loading_them()
    {
        var (card, link) = await SeedCardWithPackageAsync(WsA);
        await InAsync(WsA, async db =>
        {
            db.Add(Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.Approved, "Done."));
            await db.SaveChangesAsync(Ct);
        });

        await using var scope = fixture.ScopeFor(WsA);
        var layer = scope.ServiceProvider.GetRequiredService<IContentCardDataLayer>();

        var tracked = (await layer.FindForUpdateAsync(card.Id, Ct))!;

        // The write-once rows are never tracked, so the interceptor that keeps them immutable has nothing to
        // refuse; they leave by the database's cascade with the card.
        Assert.Empty(SqlServerRecipeFixture.Db(scope).ChangeTracker.Entries<CardMoveHistory>());
        Assert.Equal(
            ContentCardWrite.Saved,
            await layer.DeleteAsync(tracked, Entry(card, ContentAuditActions.ContentCardDeleted, "delete"), Ct));

        await InAsync(WsA, async db =>
        {
            Assert.False(await db.ContentCards.AnyAsync(item => item.Id == card.Id, Ct));
            Assert.False(await db.CardMoveHistory.AnyAsync(move => move.ContentCardId == card.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinks.AnyAsync(item => item.Id == link.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinkAssets.AnyAsync(item => item.RecipeContentPackageLinkId == link.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinkChannels.AnyAsync(item => item.RecipeContentPackageLinkId == link.Id, Ct));

            // What the package pinned is untouched.
            Assert.True(await db.Recipes.AnyAsync(item => item.Id == link.RecipeId, Ct));
            Assert.True(await db.AuditLogs.AnyAsync(
                log => log.ResourceId == card.Id.ToString("D") && log.Action == ContentAuditActions.ContentCardDeleted, Ct));
        });
    }

    // ---- Movement history ----

    [Fact]
    public async Task A_move_can_be_neither_rewritten_nor_deleted()
    {
        var card = await SeedCardAsync(WsA);
        var move = Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess, "Shoot booked.");

        await InAsync(WsA, async db =>
        {
            db.Add(move);
            await db.SaveChangesAsync(Ct);
        });

        await AssertRefusedAsync(async db =>
            (await db.CardMoveHistory.SingleAsync(item => item.Id == move.Id, Ct)).Note = "Rewritten afterwards.");
        await AssertRefusedAsync(async db =>
            (await db.CardMoveHistory.SingleAsync(item => item.Id == move.Id, Ct)).ToColumn = ContentCardColumn.Approved);
        await AssertRefusedAsync(async db =>
            (await db.CardMoveHistory.SingleAsync(item => item.Id == move.Id, Ct)).OccurredAt = Now.AddDays(-30));
        await AssertRefusedAsync(async db =>
            db.CardMoveHistory.Remove(await db.CardMoveHistory.SingleAsync(item => item.Id == move.Id, Ct)));

        var stored = await InAsync(WsA, db => db.CardMoveHistory.AsNoTracking().SingleAsync(item => item.Id == move.Id, Ct));
        Assert.Equal("Shoot booked.", stored.Note);
        Assert.Equal(ContentCardColumn.InProcess, stored.ToColumn);

        async Task AssertRefusedAsync(Func<CreatorPantryDbContext, Task> change) =>
            await InAsync(WsA, async db =>
            {
                await change(db);
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            });
    }

    [Theory]
    [InlineData("a reorder recorded as a move")]
    [InlineData("a second creation row")]
    [InlineData("a first row that came from somewhere")]
    [InlineData("a later row from nowhere")]
    [InlineData("a column that does not exist")]
    [InlineData("a sequence already taken")]
    public async Task The_engine_refuses_history_that_did_not_happen(string fault)
    {
        var card = await SeedCardAsync(WsA);

        var row = fault switch
        {
            // Changing a card's place within its column is not a transition, and writes nothing here.
            "a reorder recorded as a move" => Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.ToDo),
            "a second creation row" => Moved(card, 1, from: null, ContentCardColumn.ToDo),
            "a first row that came from somewhere" => Moved(await SeedCardAsync(WsA, withHistory: false), 1, ContentCardColumn.ToDo, ContentCardColumn.InProcess),
            "a later row from nowhere" => Moved(card, 2, from: null, ContentCardColumn.InProcess),
            "a column that does not exist" => Moved(card, 2, ContentCardColumn.ToDo, (ContentCardColumn)9),
            _ => await TakenSequenceAsync(),
        };

        await AssertEngineRefusesAsync(WsA, row);

        // Two moves composed against the same state take the same number; the second is refused, not recorded.
        async Task<CardMoveHistory> TakenSequenceAsync()
        {
            await InAsync(WsA, async db =>
            {
                db.Add(Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess));
                await db.SaveChangesAsync(Ct);
            });

            return Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.ReadyForReview);
        }
    }

    [Fact]
    public async Task A_cards_history_reads_in_sequence_and_its_move_count_is_the_moves_after_creation()
    {
        var card = await SeedCardAsync(WsA);

        await InAsync(WsA, async db =>
        {
            // Written out of order, and at one instant, so nothing but the sequence can order them.
            db.AddRange(
                Moved(card, 4, ContentCardColumn.ReadyForReview, ContentCardColumn.InProcess, "Needs a reshoot."),
                Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess),
                Moved(card, 3, ContentCardColumn.InProcess, ContentCardColumn.ReadyForReview));
            await db.SaveChangesAsync(Ct);
        });

        var history = await InAsync(WsA, db => db.CardMoveHistory.AsNoTracking()
            .Where(move => move.ContentCardId == card.Id)
            .OrderBy(move => move.Sequence)
            .ToListAsync(Ct));

        Assert.Equal([1, 2, 3, 4], history.Select(move => move.Sequence));
        Assert.Null(history[0].FromColumn);
        Assert.Equal(
            [ContentCardColumn.ToDo, ContentCardColumn.InProcess, ContentCardColumn.ReadyForReview, ContentCardColumn.InProcess],
            history.Select(move => move.ToColumn));
        Assert.Equal(3, history.Max(move => move.Sequence) - 1);
    }

    // ---- The recipe package ----

    [Fact]
    public async Task A_package_pins_exact_versions_and_can_be_neither_rewritten_nor_deleted()
    {
        var card = await SeedCardAsync(WsA);
        var recipe = await SeedRecipeAsync(WsA);
        var assetId = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var link = NewLink(card, recipe);
        link.WindowStartsAt = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.Zero);
        link.WindowEndsAt = new DateTimeOffset(2026, 10, 18, 4, 59, 0, TimeSpan.Zero);
        link.WindowTimeZoneId = "America/Chicago";
        link.WindowLocalStartsAt = new DateTime(2026, 10, 16, 9, 0, 0);
        link.WindowLocalEndsAt = new DateTime(2026, 10, 17, 23, 59, 0);
        var asset = LinkAsset(link, assetId, version: 1, sortOrder: 0);
        var channel = LinkChannel(link, "instagram", sortOrder: 0);

        await InAsync(WsA, async db =>
        {
            db.AddRange(link, asset, channel, LinkChannel(link, "blog", sortOrder: 1));
            await db.SaveChangesAsync(Ct);
        });

        var loaded = await InAsync(WsA, db => db.RecipeContentPackageLinks.AsNoTracking()
            .Include(item => item.Assets)
            .Include(item => item.Channels)
            .SingleAsync(item => item.Id == link.Id, Ct));

        Assert.Equal(WsA, loaded.WorkspaceId);
        Assert.Equal((recipe.RecipeId, recipe.VersionId), (loaded.RecipeId, loaded.RecipeVersionId));
        Assert.Equal("America/Chicago", loaded.WindowTimeZoneId);
        Assert.Equal(new DateTime(2026, 10, 16, 9, 0, 0), loaded.WindowLocalStartsAt);
        Assert.Equal((assetId, 1), (loaded.Assets.Single().MediaAssetId, loaded.Assets.Single().MediaAssetVersionNumber));
        Assert.Equal(["instagram", "blog"], loaded.Channels.OrderBy(item => item.SortOrder).Select(item => item.ChannelKey));

        await AssertRefusedAsync(async db =>
            (await db.RecipeContentPackageLinks.SingleAsync(item => item.Id == link.Id, Ct)).WindowTimeZoneId = "Europe/London");
        await AssertRefusedAsync(async db =>
            db.RecipeContentPackageLinks.Remove(await db.RecipeContentPackageLinks.SingleAsync(item => item.Id == link.Id, Ct)));
        await AssertRefusedAsync(async db =>
            (await db.RecipeContentPackageLinkAssets.SingleAsync(item => item.Id == asset.Id, Ct)).SortOrder = 5);
        await AssertRefusedAsync(async db =>
            db.RecipeContentPackageLinkChannels.Remove(
                await db.RecipeContentPackageLinkChannels.SingleAsync(item => item.Id == channel.Id, Ct)));

        async Task AssertRefusedAsync(Func<CreatorPantryDbContext, Task> change) =>
            await InAsync(WsA, async db =>
            {
                await change(db);
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            });
    }

    [Theory]
    [InlineData("a second package on one card")]
    [InlineData("a version of another recipe")]
    [InlineData("an editorial revision with no proposal")]
    [InlineData("half a window")]
    [InlineData("a window that ends before it starts")]
    [InlineData("a picture named twice")]
    [InlineData("a picture version that does not exist")]
    [InlineData("a channel named twice")]
    [InlineData("a blank channel")]
    [InlineData("a post revision with no channel")]
    public async Task The_engine_refuses_a_package_that_does_not_hold_together(string fault)
    {
        var card = await SeedCardAsync(WsA);
        var recipe = await SeedRecipeAsync(WsA);
        var other = await SeedRecipeAsync(WsA);
        var assetId = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var link = NewLink(card, recipe);

        if (fault is "a version of another recipe" or "an editorial revision with no proposal" or "half a window"
            or "a window that ends before it starts")
        {
            switch (fault)
            {
                case "a version of another recipe": link.RecipeVersionId = other.VersionId; break;
                case "an editorial revision with no proposal": link.EditorialRevisionId = Guid.NewGuid(); break;
                case "half a window": link.WindowStartsAt = Now; break;
                default:
                    link.WindowStartsAt = Now;
                    link.WindowEndsAt = Now.AddDays(-1);
                    link.WindowTimeZoneId = "America/Chicago";
                    link.WindowLocalStartsAt = new DateTime(2026, 10, 16, 9, 0, 0);
                    link.WindowLocalEndsAt = new DateTime(2026, 10, 15, 9, 0, 0);
                    break;
            }

            await AssertEngineRefusesAsync(WsA, link);
            return;
        }

        await InAsync(WsA, async db =>
        {
            db.AddRange(link, LinkAsset(link, assetId, version: 1, sortOrder: 0), LinkChannel(link, "instagram", sortOrder: 0));
            await db.SaveChangesAsync(Ct);
        });

        object row = fault switch
        {
            "a second package on one card" => NewLink(card, other),
            "a picture named twice" => LinkAsset(link, assetId, version: 1, sortOrder: 1),
            "a picture version that does not exist" => LinkAsset(
                link, await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct)), version: 7, sortOrder: 1),
            "a channel named twice" => LinkChannel(link, "instagram", sortOrder: 1),
            "a blank channel" => LinkChannel(link, "  ", sortOrder: 1),
            _ => PostWithNoChannel(),
        };

        await AssertEngineRefusesAsync(WsA, row);

        RecipeContentPackageLinkChannel PostWithNoChannel()
        {
            var channel = LinkChannel(link, "blog", sortOrder: 1);
            channel.SocialRevisionId = Guid.NewGuid();

            return channel;
        }
    }

    // ---- Deleting ----

    [Fact]
    public async Task Deleting_a_card_takes_its_history_and_its_package_and_leaves_what_it_pointed_at()
    {
        var recipe = await SeedRecipeAsync(WsA);
        var assetId = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var card = await SeedCardAsync(WsA);
        var link = NewLink(card, recipe);

        await InAsync(WsA, async db =>
        {
            db.AddRange(
                Moved(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess),
                link,
                LinkAsset(link, assetId, version: 1, sortOrder: 0),
                LinkChannel(link, "instagram", sortOrder: 0));
            await db.SaveChangesAsync(Ct);
        });

        // The card alone is loaded: its write-once children go by the database's own cascade, which is the
        // only way they can, and the way the delete seam (13.6c) has to do it.
        await InAsync(WsA, async db =>
        {
            db.ContentCards.Remove(await db.ContentCards.SingleAsync(item => item.Id == card.Id, Ct));
            await db.SaveChangesAsync(Ct);
        });

        await InAsync(WsA, async db =>
        {
            Assert.False(await db.ContentCards.AnyAsync(item => item.Id == card.Id, Ct));
            Assert.False(await db.CardMoveHistory.AnyAsync(item => item.ContentCardId == card.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinks.AnyAsync(item => item.Id == link.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinkAssets.AnyAsync(item => item.RecipeContentPackageLinkId == link.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinkChannels.AnyAsync(item => item.RecipeContentPackageLinkId == link.Id, Ct));

            // Never silently deleted with the card that pointed at them.
            Assert.True(await db.Recipes.AnyAsync(item => item.Id == recipe.RecipeId, Ct));
            Assert.True(await db.MediaAssets.AnyAsync(item => item.Id == assetId, Ct));
        });
    }

    [Fact]
    public async Task A_record_a_card_points_at_cannot_be_removed_from_under_it()
    {
        var promptId = await InAsync(WsA, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));
        var card = NewCard("About a saved prompt", ContentCardColumn.ToDo, rank: 1024);
        card.PromptRecordId = promptId;

        await InAsync(WsA, async db =>
        {
            db.Add(card);
            await db.SaveChangesAsync(Ct);
        });

        // Straight at the engine: a prompt record is write-once, so the model would refuse this before the key
        // was ever asked, and the key is what is under test.
        await InAsync(WsA, async db =>
            await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() =>
                db.Database.ExecuteSqlAsync($"DELETE FROM PromptRecords WHERE Id = {promptId}", Ct)));

        Assert.Equal(
            promptId,
            (await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct))).PromptRecordId);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Each_workspace_sees_only_its_own_cards_moves_and_packages()
    {
        var (cardA, linkA) = await SeedCardWithPackageAsync(WsA, "A's card, in A's words.");
        var (cardB, linkB) = await SeedCardWithPackageAsync(WsB, "B's card, in B's words.");

        await InAsync(WsB, async db =>
        {
            Assert.Null(await db.ContentCards.FirstOrDefaultAsync(item => item.Id == cardA.Id, Ct));
            Assert.False(await db.CardMoveHistory.AnyAsync(item => item.ContentCardId == cardA.Id, Ct));
            Assert.Null(await db.RecipeContentPackageLinks.FirstOrDefaultAsync(item => item.Id == linkA.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinkAssets.AnyAsync(item => item.RecipeContentPackageLinkId == linkA.Id, Ct));
            Assert.False(await db.RecipeContentPackageLinkChannels.AnyAsync(item => item.RecipeContentPackageLinkId == linkA.Id, Ct));
            Assert.DoesNotContain(
                await db.ContentCards.Select(item => item.Title).ToListAsync(Ct),
                title => title.Contains("A's card", StringComparison.Ordinal));
        });

        await InAsync(WsA, async db =>
        {
            Assert.Null(await db.ContentCards.FirstOrDefaultAsync(item => item.Id == cardB.Id, Ct));
            Assert.False(await db.CardMoveHistory.AnyAsync(item => item.ContentCardId == cardB.Id, Ct));
            Assert.Null(await db.RecipeContentPackageLinks.FirstOrDefaultAsync(item => item.Id == linkB.Id, Ct));
        });
    }

    [Fact]
    public async Task One_workspace_cannot_point_a_card_a_move_or_a_package_at_anothers_records()
    {
        var contextA = await SeedContextAsync(WsA);
        var recipeA = await SeedRecipeAsync(WsA);
        var assetA = await InAsync(WsA, db => CreativeContextSeeds.MediaAssetAsync(db, WsA, Now, Ct));
        var promptA = await InAsync(WsA, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));
        var (cardA, linkA) = await SeedCardWithPackageAsync(WsA);
        var cardB = await SeedCardAsync(WsB);
        var recipeB = await SeedRecipeAsync(WsB);

        // The keys are workspace-paired, so each of these names nothing in B however valid the id is in A.
        await AssertEngineRefusesAsync(WsB, With(card => card.CreativeContextId = contextA));
        await AssertEngineRefusesAsync(WsB, With(card => card.RecipeId = recipeA.RecipeId));
        await AssertEngineRefusesAsync(WsB, With(card => card.PromptRecordId = promptA));
        await AssertEngineRefusesAsync(WsB, With(card => card.MediaAssetId = assetA));
        await AssertEngineRefusesAsync(WsB, Moved(cardA, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess));
        await AssertEngineRefusesAsync(WsB, NewLink(cardB, recipeA));
        await AssertEngineRefusesAsync(WsB, NewLink(cardA, recipeB));
        await AssertEngineRefusesAsync(WsB, LinkAsset(linkA, assetA, version: 1, sortOrder: 5));
        await AssertEngineRefusesAsync(WsB, LinkChannel(linkA, "pinterest", sortOrder: 5));

        // The version pins are paired too: B's own recipe or asset cannot be pinned at one of A's versions, and
        // B's own package cannot carry A's picture.
        var (_, linkB) = await SeedCardWithPackageAsync(WsB);
        await AssertEngineRefusesAsync(WsB, With(card =>
        {
            card.RecipeId = recipeB.RecipeId;
            card.RecipeVersionId = recipeA.VersionId;
        }));
        await AssertEngineRefusesAsync(WsB, With(card =>
        {
            card.MediaAssetId = assetA;
            card.MediaAssetVersionNumber = 1;
        }));
        await AssertEngineRefusesAsync(WsB, LinkAsset(linkB, assetA, version: 1, sortOrder: 5));

        // And a row cannot simply claim the other workspace: ownership is stamped from the resolved context,
        // and a row that arrives already naming a different one is refused before it reaches the engine.
        await InAsync(WsB, async db =>
        {
            var claimed = With(card => card.WorkspaceId = WsA);
            db.Add(claimed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Equal(1, await InAsync(WsA, db => db.CardMoveHistory.CountAsync(item => item.ContentCardId == cardA.Id, Ct)));

        static ContentCard With(Action<ContentCard> lineage)
        {
            var card = NewCard("Reaching across", ContentCardColumn.ToDo, rank: 1024);
            lineage(card);

            return card;
        }
    }

    [Fact]
    public async Task The_same_card_id_in_two_workspaces_is_refused_rather_than_shared()
    {
        var card = await SeedCardAsync(WsA);
        var twin = NewCard("The same id, somewhere else", ContentCardColumn.ToDo, rank: 1024);
        twin.Id = card.Id;

        await AssertEngineRefusesAsync(WsB, twin);

        Assert.Equal(
            card.Title,
            (await InAsync(WsA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == card.Id, Ct))).Title);
    }

    [Fact]
    public async Task Deleting_a_workspace_still_succeeds_with_a_packaged_card_in_it()
    {
        // Five tables reach the workspace through the card, and the card and its package hold restricted keys
        // to recipes, assets and contexts that cascade from the same workspace. If SQL Server checked those
        // NO ACTION constraints mid-cascade rather than at the end of the statement, a workspace with one
        // packaged card on its board could never be erased.
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

        var contextId = await SeedContextAsync(workspaceId);
        var (card, _) = await SeedCardWithPackageAsync(workspaceId);

        await InAsync(workspaceId, async db =>
        {
            (await db.ContentCards.SingleAsync(item => item.Id == card.Id, Ct)).CreativeContextId = contextId;
            await db.SaveChangesAsync(Ct);
        });

        await using var erase = fixture.ScopeFor(WsA);
        var unfiltered = SqlServerRecipeFixture.Db(erase);
        unfiltered.Workspaces.Remove(await unfiltered.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await unfiltered.SaveChangesAsync(Ct);

        // Counted with raw SQL rather than IgnoreQueryFilters: the erasure is what is under test.
        foreach (var table in new[]
                 {
                     "ContentCards", "CardMoveHistory", "RecipeContentPackageLinks", "RecipeContentPackageLinkAssets",
                     "RecipeContentPackageLinkChannels",
                 })
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

    /// <summary>A card in ToDo, with the creation row every card has unless a test is about its absence.</summary>
    private async Task<ContentCard> SeedCardAsync(Guid workspaceId, bool withHistory = true, string title = "Soda bread")
    {
        var card = NewCard(title, ContentCardColumn.ToDo, rank: ContentCardPolicy.RankGap);

        await InAsync(workspaceId, async db =>
        {
            db.Add(card);
            if (withHistory) db.Add(Created(card));
            await db.SaveChangesAsync(Ct);
        });

        return card;
    }

    private async Task<(ContentCard Card, RecipeContentPackageLink Link)> SeedCardWithPackageAsync(
        Guid workspaceId, string title = "Soda bread")
    {
        var card = await SeedCardAsync(workspaceId, title: title);
        var recipe = await SeedRecipeAsync(workspaceId);
        var assetId = await InAsync(workspaceId, db => CreativeContextSeeds.MediaAssetAsync(db, workspaceId, Now, Ct));
        var link = NewLink(card, recipe);

        await InAsync(workspaceId, async db =>
        {
            db.AddRange(link, LinkAsset(link, assetId, version: 1, sortOrder: 0), LinkChannel(link, "instagram", sortOrder: 0));
            await db.SaveChangesAsync(Ct);
        });

        return (card, link);
    }

    /// <summary>Fails any save that inserts a row of movement history, from inside the save.</summary>
    private sealed class FailOnMoveHistoryInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            eventData.Context!.ChangeTracker.Entries<CardMoveHistory>().Any(entry => entry.State == EntityState.Added)
                ? throw new DbUpdateException("Injected failure while writing the movement history.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static AuditEntry Entry(ContentCard card, string action, string summary) => new(
        "test-account", action, ContentAuditActions.ContentCardResourceType, card.Id.ToString("D"), Guid.NewGuid(), summary);

    private static ContentCard NewCard(string title, ContentCardColumn column, long rank) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Column = column,
        SortRank = rank,
        CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
        UpdatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static CardMoveHistory Created(ContentCard card) => Moved(card, 1, from: null, card.Column);

    private static CardMoveHistory Moved(
        ContentCard card, int sequence, ContentCardColumn? from, ContentCardColumn to, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentCardId = card.Id,
        Sequence = sequence,
        FromColumn = from,
        ToColumn = to,
        Note = note,
        ActorMembershipId = SqlServerRecipeFixture.AuthorOne,
        OccurredAt = Now,
    };

    private static RecipeContentPackageLink NewLink(ContentCard card, (Guid RecipeId, Guid VersionId) recipe) => new()
    {
        Id = Guid.NewGuid(),
        ContentCardId = card.Id,
        RecipeId = recipe.RecipeId,
        RecipeVersionId = recipe.VersionId,
        CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
        CreatedAt = Now,
    };

    private static RecipeContentPackageLinkAsset LinkAsset(
        RecipeContentPackageLink link, Guid assetId, int version, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        RecipeContentPackageLinkId = link.Id,
        MediaAssetId = assetId,
        MediaAssetVersionNumber = version,
        SortOrder = sortOrder,
    };

    private static RecipeContentPackageLinkChannel LinkChannel(RecipeContentPackageLink link, string channelKey, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        RecipeContentPackageLinkId = link.Id,
        ChannelKey = channelKey,
        SortOrder = sortOrder,
    };
}
