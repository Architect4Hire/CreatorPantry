using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// <c>POST .../content-cards/{cardId}/move</c> through the real Gateway (13.6b, KAN-005): a move to another
/// column, a new place in the same one, what each does to the card's history, re-spacing, the concurrency
/// token, the role bar, and the boundary between two workspaces.
/// </summary>
/// <remarks>
/// <strong>What SQLite cannot show here.</strong> Its <c>RowVersion</c> never moves, so two writers cannot
/// collide in this fixture. The token check is exercised with a token the card does not hold; a lost race on
/// one card, a re-spacing that collides with an edit of a neighbour, and a history row that fails to write are
/// <c>ContentBoardTests</c>' on SQL Server.
/// </remarks>
public sealed class ContentCardMoveEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly string TokenOfAnotherState = Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9]);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CardsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/content-cards";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static Guid IdOf(JsonElement detail) => detail.GetProperty("card").GetProperty("id").GetGuid();

    /// <summary>Creates a card through the real route, so it has its creation row, and returns its detail.</summary>
    private static async Task<JsonElement> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, string title, string column = "ToDo")
    {
        var response = await client.PostAsJsonAsync(CardsIn(workspace), new { title, column }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    /// <summary>Moves a card, quoting the token of the detail it was read as unless one is given.</summary>
    private static Task<HttpResponseMessage> MoveAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        JsonElement detail,
        string column,
        Guid? before = null,
        string? note = null,
        string? token = null) =>
        client.PostAsJsonAsync(
            $"{CardsIn(workspace)}/{IdOf(detail)}/move",
            new
            {
                expectedConcurrencyToken = token ?? detail.GetProperty("card").GetProperty("concurrencyToken").GetString(),
                column,
                beforeCardId = before,
                note,
            },
            Ct);

    /// <summary>The titles in one column, in the board's own order, read through the list route.</summary>
    private static async Task<List<string>> ColumnAsync(GatewayClient client, SeededWorkspace workspace, string column)
    {
        var page = await BodyOf(await client.GetAsync($"{CardsIn(workspace)}?column={column}&limit=100", Ct));

        return [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()!)];
    }

    // ---- A move to another column ----

    [Fact]
    public async Task A_move_to_another_column_records_one_history_entry_with_its_note_and_its_actor()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "Soda bread");

        var response = await MoveAsync(client, _fixture.WorkspaceA, card, "InProcess", note: "  Shoot booked.  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var detail = await BodyOf(response);
        Assert.Equal("InProcess", detail.GetProperty("card").GetProperty("column").GetString());
        Assert.Equal(1, detail.GetProperty("card").GetProperty("moveCount").GetInt32());

        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal([1, 2], history.Select(move => move.GetProperty("sequence").GetInt32()));
        Assert.Equal("ToDo", history[1].GetProperty("fromColumn").GetString());
        Assert.Equal("InProcess", history[1].GetProperty("toColumn").GetString());
        Assert.Equal("Shoot booked.", history[1].GetProperty("note").GetString());

        var id = IdOf(card);
        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == id, Ct));
        var move = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.CardMoveHistory.AsNoTracking()
            .SingleAsync(item => item.ContentCardId == id && item.Sequence == 2, Ct));

        // The actor is the mover's own membership, from the resolved context.
        Assert.Equal(stored.UpdatedByMembershipId, move.ActorMembershipId);
        Assert.NotNull(move.ActorMembershipId);
    }

    [Fact]
    public async Task A_card_can_be_moved_to_the_top_the_middle_or_the_bottom_of_another_column()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = await CreateAsync(client, _fixture.WorkspaceA, "First", "InProcess");
        var second = await CreateAsync(client, _fixture.WorkspaceA, "Second", "InProcess");
        var top = await CreateAsync(client, _fixture.WorkspaceA, "To the top");
        var middle = await CreateAsync(client, _fixture.WorkspaceA, "To the middle");
        var bottom = await CreateAsync(client, _fixture.WorkspaceA, "To the bottom");

        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, _fixture.WorkspaceA, top, "InProcess", before: IdOf(first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, _fixture.WorkspaceA, middle, "InProcess", before: IdOf(second))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, _fixture.WorkspaceA, bottom, "InProcess")).StatusCode);

        Assert.Equal(
            ["To the top", "First", "To the middle", "Second", "To the bottom"],
            await ColumnAsync(client, _fixture.WorkspaceA, "InProcess"));
        Assert.Empty(await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));

        // None of the cards that were already there was written: making room touched only the mover.
        Assert.Equal(1, await HistoryCountAsync(_fixture.WorkspaceA, IdOf(first)));
        var ranks = await RanksAsync(_fixture.WorkspaceA);
        Assert.Equal((ContentCardPolicy.RankGap, 2 * ContentCardPolicy.RankGap), (ranks["First"], ranks["Second"]));
    }

    [Fact]
    public async Task Every_move_between_columns_adds_one_entry_in_sequence_including_a_move_back()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "Back and forth");

        foreach (var column in new[] { "InProcess", "ReadyForReview", "InProcess", "Approved" })
        {
            var moved = await MoveAsync(client, _fixture.WorkspaceA, card, column);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            card = await BodyOf(moved);
        }

        Assert.Equal(4, card.GetProperty("card").GetProperty("moveCount").GetInt32());
        Assert.Equal(
            [(null, "ToDo"), ("ToDo", "InProcess"), ("InProcess", "ReadyForReview"), ("ReadyForReview", "InProcess"), ("InProcess", "Approved")],
            card.GetProperty("history").EnumerateArray().Select(move => (
                move.GetProperty("fromColumn").ValueKind == JsonValueKind.Null ? null : move.GetProperty("fromColumn").GetString(),
                move.GetProperty("toColumn").GetString()!)));
    }

    // ---- A new place in the same column ----

    [Fact]
    public async Task A_new_place_in_the_same_column_reorders_and_adds_no_history()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var a = await CreateAsync(client, _fixture.WorkspaceA, "A");
        var b = await CreateAsync(client, _fixture.WorkspaceA, "B");
        var c = await CreateAsync(client, _fixture.WorkspaceA, "C");

        // C to the top, then A to the bottom, then B between them.
        var toTop = await MoveAsync(client, _fixture.WorkspaceA, c, "ToDo", before: IdOf(a));
        Assert.Equal(HttpStatusCode.OK, toTop.StatusCode);
        Assert.Equal(["C", "A", "B"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));

        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, _fixture.WorkspaceA, a, "ToDo")).StatusCode);
        Assert.Equal(["C", "B", "A"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));

        var between = await MoveAsync(client, _fixture.WorkspaceA, await BodyOf(toTop), "ToDo", before: IdOf(a));
        Assert.Equal(HttpStatusCode.OK, between.StatusCode);
        Assert.Equal(["B", "C", "A"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));

        // Three reorders, and the history still says only that each card was created: no transition is claimed
        // that did not happen, and nothing was audited as a move.
        var detail = await BodyOf(between);
        Assert.Equal(0, detail.GetProperty("card").GetProperty("moveCount").GetInt32());
        Assert.Single(detail.GetProperty("history").EnumerateArray());

        foreach (var card in new[] { a, b, c })
        {
            Assert.Equal(1, await HistoryCountAsync(_fixture.WorkspaceA, IdOf(card)));
        }

        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardMoved));
    }

    [Fact]
    public async Task A_note_with_no_move_to_keep_it_on_is_refused_and_the_card_stays_where_it_was()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var a = await CreateAsync(client, _fixture.WorkspaceA, "A");
        var b = await CreateAsync(client, _fixture.WorkspaceA, "B");

        var response = await MoveAsync(client, _fixture.WorkspaceA, b, "ToDo", before: IdOf(a), note: "Why I moved it up.");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CardInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty("note", out _));
        Assert.Equal(["A", "B"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));
    }

    [Fact]
    public async Task A_note_that_is_only_space_is_no_note_and_a_reorder_among_tied_cards_is_judged_by_place()
    {
        // Three cards sharing one rank: only the store's order tells them apart.
        var tied = new[]
        {
            Seeded("Tied one", ContentCardColumn.Approved, 1024),
            Seeded("Tied two", ContentCardColumn.Approved, 1024),
            Seeded("Tied three", ContentCardColumn.Approved, 1024),
        };
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.AddRange(tied);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var order = await ColumnAsync(client, _fixture.WorkspaceA, "Approved");
        var middle = tied.Single(card => card.Title == order[1]);
        var last = tied.Single(card => card.Title == order[2]);
        var detail = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{middle.Id}", Ct));

        // Already immediately before the last of the three, though no rank says so: nothing is written. And a
        // note of nothing but space is no note, so it is not a note on a reorder.
        var unchanged = await MoveAsync(client, _fixture.WorkspaceA, detail, "Approved", before: last.Id, note: "   ");

        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(order, await ColumnAsync(client, _fixture.WorkspaceA, "Approved"));
        Assert.All((await RanksAsync(_fixture.WorkspaceA)).Values, rank => Assert.Equal(1024, rank));
    }

    [Fact]
    public async Task A_card_that_is_already_exactly_there_is_left_alone()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var a = await CreateAsync(client, _fixture.WorkspaceA, "A");
        var b = await CreateAsync(client, _fixture.WorkspaceA, "B");
        var before = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == IdOf(a), Ct));

        // A is already immediately before B, and B is already at the bottom.
        var first = await MoveAsync(client, _fixture.WorkspaceA, a, "ToDo", before: IdOf(b));
        var second = await MoveAsync(client, _fixture.WorkspaceA, b, "ToDo");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var after = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == IdOf(a), Ct));
        Assert.Equal((before.SortRank, before.UpdatedAt, before.UpdatedByMembershipId), (after.SortRank, after.UpdatedAt, after.UpdatedByMembershipId));
        Assert.Equal(["A", "B"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));
    }

    // ---- Making room ----

    [Theory]
    [InlineData(1024, 1025)]
    [InlineData(1024, 1024)]
    public async Task When_there_is_no_room_between_two_cards_the_column_is_re_spaced_and_keeps_its_order(long upper, long lower)
    {
        // Two neighbours with no whole number between their ranks, and one far below them.
        var cards = new[]
        {
            Seeded("Upper", ContentCardColumn.InProcess, upper),
            Seeded("Lower", ContentCardColumn.InProcess, lower),
            Seeded("Far below", ContentCardColumn.InProcess, 900_000),
            Seeded("Elsewhere", ContentCardColumn.Approved, upper),
        };
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.AddRange(cards);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var orderBefore = await ColumnAsync(client, _fixture.WorkspaceA, "InProcess");
        var mover = await CreateAsync(client, _fixture.WorkspaceA, "Dropped between");

        // Before whichever of the two the store reads second, which is "between them" whatever their ranks.
        var secondOfPair = cards.Single(card => card.Title == orderBefore[1]).Id;
        var response = await MoveAsync(client, _fixture.WorkspaceA, mover, "InProcess", before: secondOfPair);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [orderBefore[0], "Dropped between", orderBefore[1], "Far below"],
            await ColumnAsync(client, _fixture.WorkspaceA, "InProcess"));

        // Every card in the column is now one gap from the next, and no two share a rank.
        var ranks = await RanksAsync(_fixture.WorkspaceA);
        Assert.Equal(
            [ContentCardPolicy.RankGap, 2 * ContentCardPolicy.RankGap, 3 * ContentCardPolicy.RankGap, 4 * ContentCardPolicy.RankGap],
            new[] { orderBefore[0], "Dropped between", orderBefore[1], "Far below" }.Select(title => ranks[title]));

        // Another column is not this move's to touch.
        Assert.Equal(upper, ranks["Elsewhere"]);

        // Re-spacing changed nobody's words and is nobody's move.
        Assert.Equal(1, (await BodyOf(response)).GetProperty("card").GetProperty("moveCount").GetInt32());
        Assert.Equal(0, await HistoryCountAsync(_fixture.WorkspaceA, cards[0].Id));
    }

    // ---- What cannot be done ----

    [Theory]
    [InlineData("a card in another column")]
    [InlineData("the card itself")]
    [InlineData("a card that does not exist")]
    [InlineData("another workspace's card")]
    public async Task A_place_that_is_not_in_the_target_column_is_refused_the_same_way_whatever_the_reason(string reason)
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var card = await CreateAsync(inA, _fixture.WorkspaceA, "Mine");
        var elsewhere = await CreateAsync(inA, _fixture.WorkspaceA, "In review", "ReadyForReview");

        // B's card, in the very column A is moving into: real, and in the right column — on B's board.
        var theirs = await CreateAsync(inB, _fixture.WorkspaceB, "Theirs", "InProcess");

        var target = reason switch
        {
            "a card in another column" => IdOf(elsewhere),
            "the card itself" => IdOf(card),
            "a card that does not exist" => Guid.NewGuid(),
            _ => IdOf(theirs),
        };

        var response = await MoveAsync(inA, _fixture.WorkspaceA, card, "InProcess", before: target);
        var unknown = await MoveAsync(inA, _fixture.WorkspaceA, card, "InProcess", before: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.CardMoveUnprocessable, Code(body));

        // The same answer in every part a client can read, apart from the request's own correlation id.
        foreach (var name in new[] { "code", "title", "status", "detail", "type", "errors" })
        {
            Assert.Equal(
                body.TryGetProperty(name, out var left) ? left.ToString() : null,
                unknownBody.TryGetProperty(name, out var right) ? right.ToString() : null);
        }

        // Nothing moved, and nothing was recorded.
        Assert.Equal(["Mine"], await ColumnAsync(inA, _fixture.WorkspaceA, "ToDo"));
        Assert.Equal(1, await HistoryCountAsync(_fixture.WorkspaceA, IdOf(card)));
        Assert.Equal(["Theirs"], await ColumnAsync(inB, _fixture.WorkspaceB, "InProcess"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Done")]
    public async Task A_move_that_names_no_column_is_a_bad_request(string? column)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "Mine");

        var response = await client.PostAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{IdOf(card)}/move",
            new Dictionary<string, object?>
            {
                ["expectedConcurrencyToken"] = card.GetProperty("card").GetProperty("concurrencyToken").GetString(),
                ["column"] = column,
            },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Mine"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));
    }

    [Fact]
    public async Task A_note_that_is_too_long_is_a_bad_request_naming_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "Mine");

        var response = await MoveAsync(
            client, _fixture.WorkspaceA, card, "InProcess", note: new string('n', ContentCardPolicy.MoveNoteMaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("note", out _));
        Assert.Equal(["Mine"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));
    }

    // ---- Conflict ----

    [Fact]
    public async Task A_token_the_card_does_not_hold_is_a_conflict_and_nothing_moves()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "Mine");

        var response = await MoveAsync(client, _fixture.WorkspaceA, card, "Approved", note: "From an old read.", token: TokenOfAnotherState);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CardStale, Code(await BodyOf(response)));
        Assert.Equal(["Mine"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));
        Assert.Equal(1, await HistoryCountAsync(_fixture.WorkspaceA, IdOf(card)));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardMoved));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task A_missing_or_malformed_token_is_a_bad_request_not_a_conflict(string? token)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "Mine");

        var response = await client.PostAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{IdOf(card)}/move",
            new Dictionary<string, object?> { ["expectedConcurrencyToken"] = token, ["column"] = "Approved" },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
        Assert.Equal(["Mine"], await ColumnAsync(client, _fixture.WorkspaceA, "ToDo"));
    }

    // ---- Who may move, and across workspaces ----

    [Fact]
    public async Task A_viewer_may_not_move_a_card_and_an_editor_may()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, "B's card");

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await MoveAsync(viewer, _fixture.WorkspaceB, inB, "Approved")).StatusCode);
        Assert.Equal(["B's card"], await ColumnAsync(ownerB, _fixture.WorkspaceB, "ToDo"));

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, "A's card");

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(editor, _fixture.WorkspaceA, inA, "Approved")).StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_card_and_an_unknown_card_answer_identically_and_nothing_moves()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var cardA = await CreateAsync(inA, _fixture.WorkspaceA, "A's card");
        var body = new
        {
            expectedConcurrencyToken = cardA.GetProperty("card").GetProperty("concurrencyToken").GetString(),
            column = "Approved",
            note = "Taken over.",
        };

        // B, on its own route, naming A's card with A's real token; and naming a card nobody has.
        var foreign = await inB.PostAsJsonAsync($"{CardsIn(_fixture.WorkspaceB)}/{IdOf(cardA)}/move", body, Ct);
        var unknown = await inB.PostAsJsonAsync($"{CardsIn(_fixture.WorkspaceB)}/{Guid.NewGuid()}/move", body, Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var foreignBody = await BodyOf(foreign);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.CardNotFound, Code(foreignBody));

        foreach (var name in new[] { "code", "title", "status", "detail", "type" })
        {
            Assert.Equal(
                foreignBody.TryGetProperty(name, out var left) ? left.ToString() : null,
                unknownBody.TryGetProperty(name, out var right) ? right.ToString() : null);
        }

        // And B through A's own route is a workspace it is not in.
        Assert.Equal(HttpStatusCode.NotFound, (await inB.PostAsJsonAsync($"{CardsIn(_fixture.WorkspaceA)}/{IdOf(cardA)}/move", body, Ct)).StatusCode);

        Assert.Equal(["A's card"], await ColumnAsync(inA, _fixture.WorkspaceA, "ToDo"));
        Assert.Equal(1, await HistoryCountAsync(_fixture.WorkspaceA, IdOf(cardA)));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardMoved));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, ContentAuditActions.ContentCardMoved));
    }

    [Fact]
    public async Task One_workspaces_moves_and_re_spacing_change_nothing_on_anothers_board()
    {
        // The same crowded column on both boards: two cards with no room between them.
        foreach (var workspace in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
        {
            await InWorkspaceAsync(workspace, async db =>
            {
                db.AddRange(Seeded("Upper", ContentCardColumn.InProcess, 1024), Seeded("Lower", ContentCardColumn.InProcess, 1025));
                await db.SaveChangesAsync(Ct);

                return 0;
            });
        }

        var ranksInBBefore = await RanksAsync(_fixture.WorkspaceB);
        var lowerInA = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking()
            .Where(card => card.Title == "Lower").Select(card => card.Id).SingleAsync(Ct));

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var mover = await CreateAsync(inA, _fixture.WorkspaceA, "A's mover");

        // Forces A's column to be re-spaced.
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(inA, _fixture.WorkspaceA, mover, "InProcess", before: lowerInA)).StatusCode);

        var ranksInA = await RanksAsync(_fixture.WorkspaceA);
        Assert.Equal(3 * ContentCardPolicy.RankGap, ranksInA["Lower"]);

        // B's column is exactly as it was, crowded and all, and holds none of A's history or audit.
        Assert.Equal(ranksInBBefore, await RanksAsync(_fixture.WorkspaceB));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, ContentAuditActions.ContentCardMoved));
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.CardMoveHistory.CountAsync(Ct)));
    }

    // ---- Audit ----

    [Fact]
    public async Task A_move_writes_an_audit_entry_naming_the_columns_and_never_the_note()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, "A very private title");

        await MoveAsync(client, _fixture.WorkspaceA, card, "ReadyForReview", note: "A very private reason.");

        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Action == ContentAuditActions.ContentCardMoved, Ct));

        Assert.Equal(IdOf(card).ToString("D"), entry.ResourceId);
        Assert.Equal(("ToDo", "ReadyForReview"), (entry.BeforeReference, entry.AfterReference));
        Assert.Contains("ReadyForReview", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    private Task<int> HistoryCountAsync(SeededWorkspace workspace, Guid cardId) =>
        InWorkspaceAsync(workspace, db => db.CardMoveHistory.CountAsync(move => move.ContentCardId == cardId, Ct));

    private Task<int> AuditCountAsync(SeededWorkspace workspace, string action) =>
        InWorkspaceAsync(workspace, db => db.AuditLogs.CountAsync(log => log.Action == action, Ct));

    private Task<Dictionary<string, long>> RanksAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.ContentCards.AsNoTracking().ToDictionaryAsync(card => card.Title, card => card.SortRank, Ct));

    private static ContentCard Seeded(string title, ContentCardColumn column, long rank) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Column = column,
        SortRank = rank,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
