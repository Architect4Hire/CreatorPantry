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
/// The <c>.../content-cards</c> read routes through the real Gateway (13.5): the board a page at a time and one
/// card in full — the contract, the filters, the order, and the boundary between two workspaces.
/// </summary>
/// <remarks>
/// No route writes a card yet (13.6 onward), so every card here is seeded straight into the store inside its
/// workspace's resolved context, the way a request would have written it.
/// </remarks>
public sealed class ContentCardsEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Friday = new(2026, 10, 16);
    private static readonly DateOnly NextFriday = new(2026, 10, 23);
    private static readonly DateOnly Monday = new(2026, 10, 19);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CardsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/content-cards";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static List<string> Titles(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()!)];

    private async Task<JsonElement> ListAsync(GatewayClient client, SeededWorkspace workspace, string query = "")
    {
        var response = await client.GetAsync($"{CardsIn(workspace)}{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    // ---- The list ----

    [Fact]
    public async Task An_empty_board_is_an_empty_page_with_all_four_columns_at_zero()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(CardsIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var page = await BodyOf(response);
        Assert.Empty(Titles(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.Equal(
            [("ToDo", 0), ("InProcess", 0), ("ReadyForReview", 0), ("Approved", 0)],
            page.GetProperty("columns").EnumerateArray()
                .Select(column => (column.GetProperty("column").GetString()!, column.GetProperty("count").GetInt32())));
    }

    [Fact]
    public async Task The_board_reads_in_column_order_and_then_by_place_in_the_column()
    {
        await SeedAsync(
            _fixture.WorkspaceA,
            Card("Approved first", ContentCardColumn.Approved, rank: 1024),
            Card("To do, second", ContentCardColumn.ToDo, rank: 2048),
            Card("In process", ContentCardColumn.InProcess, rank: 512),
            Card("To do, first", ContentCardColumn.ToDo, rank: 1024),
            Card("For review", ContentCardColumn.ReadyForReview, rank: 4096));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var page = await ListAsync(client, _fixture.WorkspaceA);

        Assert.Equal(["To do, first", "To do, second", "In process", "For review", "Approved first"], Titles(page));
        Assert.Equal(
            [("ToDo", 2), ("InProcess", 1), ("ReadyForReview", 1), ("Approved", 1)],
            page.GetProperty("columns").EnumerateArray()
                .Select(column => (column.GetProperty("column").GetString()!, column.GetProperty("count").GetInt32())));
    }

    [Fact]
    public async Task A_row_is_a_summary_and_carries_nothing_it_should_not()
    {
        var long_description = new string('d', ContentCardPolicy.DescriptionPreviewMaxLength + 50);
        var card = Card("Soda bread, Friday", ContentCardColumn.InProcess, rank: 1024, channel: "instagram", date: Friday);
        card.Description = long_description;
        card.CuisineCode = "irish";
        card.DishTypeCode = "bread";
        card.MethodCode = "baking";
        await SeedAsync(_fixture.WorkspaceA, card);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var row = (await ListAsync(client, _fixture.WorkspaceA)).GetProperty("items")[0];

        Assert.Equal(card.Id, row.GetProperty("id").GetGuid());
        Assert.Equal("InProcess", row.GetProperty("column").GetString());
        Assert.Equal("instagram", row.GetProperty("channelKey").GetString());
        Assert.Equal("2026-10-16", row.GetProperty("plannedDate").GetString());
        Assert.Equal("Friday", row.GetProperty("plannedDay").GetString());
        Assert.Equal("irish", row.GetProperty("cuisineCode").GetString());
        Assert.Equal(ContentCardPolicy.DescriptionPreviewMaxLength, row.GetProperty("descriptionPreview").GetString()!.Length);
        Assert.Equal(long_description.Length, row.GetProperty("descriptionLength").GetInt32());
        Assert.Equal(0, row.GetProperty("moveCount").GetInt32());
        Assert.False(row.GetProperty("hasPackage").GetBoolean());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("concurrencyToken").GetString()));

        // Null until publication records exist: a card says nothing about delivery on its own.
        Assert.Equal(JsonValueKind.Null, row.GetProperty("publishing").ValueKind);

        // No place-in-column number, no workspace, nobody's identity, and nothing of a provider's.
        var names = row.EnumerateObject().Select(property => property.Name).ToList();
        Assert.DoesNotContain(names, name => name.Contains("rank", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("membership", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("provider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("?channel=instagram", "Insta Friday|Insta next Friday")]
    [InlineData("?channel=INSTAGRAM", "")]
    [InlineData("?channel=myspace", "")]
    [InlineData("?day=Friday", "Insta Friday|Insta next Friday|Blog Friday")]
    [InlineData("?day=friday", "Insta Friday|Insta next Friday|Blog Friday")]
    [InlineData("?day=Monday", "Blog Monday")]
    [InlineData("?day=Sunday", "")]
    [InlineData("?date=2026-10-16", "Insta Friday|Blog Friday")]
    [InlineData("?date=2026-10-17", "")]
    [InlineData("?column=Approved", "Blog Friday")]
    [InlineData("?column=todo", "Insta Friday|Undated")]
    [InlineData("?channel=instagram&day=Friday&date=2026-10-23", "Insta next Friday")]
    [InlineData("?channel=blog&day=Friday", "Blog Friday")]
    [InlineData("?channel=blog&date=2026-10-23", "")]
    [InlineData("?day=Friday&column=InProcess", "Insta next Friday")]
    public async Task Each_filter_narrows_the_board_and_they_combine(string query, string expected)
    {
        await SeedFilterBoardAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var page = await ListAsync(client, _fixture.WorkspaceA, query);

        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), Titles(page));
    }

    [Fact]
    public async Task The_column_counts_follow_the_filters_and_count_every_page()
    {
        await SeedFilterBoardAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var fridays = await ListAsync(client, _fixture.WorkspaceA, "?day=Friday&limit=1");
        Assert.Single(Titles(fridays));
        Assert.Equal(
            [("ToDo", 1), ("InProcess", 1), ("ReadyForReview", 0), ("Approved", 1)],
            fridays.GetProperty("columns").EnumerateArray()
                .Select(column => (column.GetProperty("column").GetString()!, column.GetProperty("count").GetInt32())));

        // A named column is the only one the request could match, so it is the only one listed.
        var oneColumn = await ListAsync(client, _fixture.WorkspaceA, "?column=ToDo");
        Assert.Equal(
            [("ToDo", 2)],
            oneColumn.GetProperty("columns").EnumerateArray()
                .Select(column => (column.GetProperty("column").GetString()!, column.GetProperty("count").GetInt32())));
    }

    [Theory]
    [InlineData("?day=Fryday", "day")]
    [InlineData("?day=5", "day")]
    [InlineData("?date=16-10-2026", "date")]
    [InlineData("?date=2026-02-30", "date")]
    [InlineData("?column=Done", "column")]
    [InlineData("?column=2", "column")]
    public async Task A_filter_that_cannot_be_read_is_a_bad_request_naming_it(string query, string field)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CardListInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
    }

    // ---- Paging ----

    [Fact]
    public async Task Paging_walks_the_whole_board_once_across_columns_and_through_cards_that_share_a_place()
    {
        await SeedAsync(
            _fixture.WorkspaceA,
            Card("a", ContentCardColumn.ToDo, rank: 1024),

            // Three cards dropped into the same slot: one column, one rank. Only the id orders them.
            Card("b", ContentCardColumn.ToDo, rank: 2048),
            Card("c", ContentCardColumn.ToDo, rank: 2048),
            Card("d", ContentCardColumn.ToDo, rank: 2048),
            Card("e", ContentCardColumn.InProcess, rank: 1024),
            Card("f", ContentCardColumn.Approved, rank: 1));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var whole = Titles(await ListAsync(client, _fixture.WorkspaceA));
        Assert.Equal(6, whole.Count);
        Assert.Equal("a", whole[0]);
        Assert.Equal(["b", "c", "d"], whole.Skip(1).Take(3).Order());
        Assert.Equal(["e", "f"], whole.Skip(4));

        foreach (var limit in new[] { 1, 2, 4 })
        {
            var walked = new List<string>();
            string? cursor = null;

            do
            {
                var page = await ListAsync(
                    client, _fixture.WorkspaceA, $"?limit={limit}{(cursor is null ? "" : $"&cursor={cursor}")}");
                Assert.InRange(Titles(page).Count, 1, limit);
                walked.AddRange(Titles(page));
                cursor = page.GetProperty("nextCursor").GetString();
            }
            while (cursor is not null);

            // Every card once, in the same order as the unpaged read: none repeated, none skipped.
            Assert.Equal(whole, walked);
        }
    }

    [Fact]
    public async Task A_limit_out_of_range_is_clamped_rather_than_refused()
    {
        await SeedAsync(
            _fixture.WorkspaceA,
            Card("a", ContentCardColumn.ToDo, rank: 1024),
            Card("b", ContentCardColumn.ToDo, rank: 2048));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Single(Titles(await ListAsync(client, _fixture.WorkspaceA, "?limit=0")));
        Assert.Equal(2, Titles(await ListAsync(client, _fixture.WorkspaceA, "?limit=5000")).Count);
    }

    [Fact]
    public async Task A_cursor_is_bound_to_its_filters_and_its_workspace()
    {
        await SeedFilterBoardAsync(_fixture.WorkspaceA);
        await SeedFilterBoardAsync(_fixture.WorkspaceB);

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var cursor = (await ListAsync(inA, _fixture.WorkspaceA, "?day=Friday&limit=1")).GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        // The same filters in the same workspace: the next page.
        Assert.Single(Titles(await ListAsync(inA, _fixture.WorkspaceA, $"?day=Friday&limit=1&cursor={cursor}")));

        foreach (var (client, workspace, query) in new[]
                 {
                     (inA, _fixture.WorkspaceA, $"?day=Monday&cursor={cursor}"),
                     (inA, _fixture.WorkspaceA, $"?cursor={cursor}"),
                     (inB, _fixture.WorkspaceB, $"?day=Friday&cursor={cursor}"),
                     (inA, _fixture.WorkspaceA, "?cursor=not-a-cursor"),
                 })
        {
            var response = await client.GetAsync($"{CardsIn(workspace)}{query}", Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ContentErrorCodes.CardCursorInvalid, Code(await BodyOf(response)));
        }
    }

    // ---- Detail ----

    [Fact]
    public async Task A_card_reads_in_full_with_its_history_in_sequence_and_its_true_move_count()
    {
        var card = Card("Soda bread", ContentCardColumn.InProcess, rank: 1024, channel: "blog", date: Monday);
        card.Description = "Hero shot and the three posts.";
        await SeedAsync(
            _fixture.WorkspaceA,
            card,

            // Written out of order and at one instant, so only the sequence can order them.
            Move(card, 4, ContentCardColumn.ReadyForReview, ContentCardColumn.InProcess, "Needs a reshoot."),
            Move(card, 1, null, ContentCardColumn.ToDo),
            Move(card, 3, ContentCardColumn.InProcess, ContentCardColumn.ReadyForReview),
            Move(card, 2, ContentCardColumn.ToDo, ContentCardColumn.InProcess));

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{card.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var detail = await BodyOf(response);
        Assert.Equal("Soda bread", detail.GetProperty("card").GetProperty("title").GetString());
        Assert.Equal("Hero shot and the three posts.", detail.GetProperty("description").GetString());
        Assert.Equal(3, detail.GetProperty("card").GetProperty("moveCount").GetInt32());
        Assert.False(detail.GetProperty("historyTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("package").ValueKind);
        Assert.Empty(detail.GetProperty("lineage").EnumerateArray());

        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3, 4], history.Select(move => move.GetProperty("sequence").GetInt32()));
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("fromColumn").ValueKind);
        Assert.Equal("ToDo", history[0].GetProperty("toColumn").GetString());
        Assert.Equal("Needs a reshoot.", history[3].GetProperty("note").GetString());

        // Nobody's identity: membership ids never leave the server.
        Assert.DoesNotContain(
            history[1].EnumerateObject().Select(property => property.Name),
            name => name.Contains("actor", StringComparison.OrdinalIgnoreCase)
                || name.Contains("membership", StringComparison.OrdinalIgnoreCase));

        // The list agrees with the detail about how often it moved.
        var row = (await ListAsync(client, _fixture.WorkspaceA)).GetProperty("items")[0];
        Assert.Equal(3, row.GetProperty("moveCount").GetInt32());
    }

    [Fact]
    public async Task A_long_history_returns_its_latest_moves_and_says_there_were_more()
    {
        var card = Card("Back and forth", ContentCardColumn.ToDo, rank: 1024);
        var moves = new List<object> { card, Move(card, 1, null, ContentCardColumn.ToDo) };
        var total = ContentCardPolicy.DetailHistoryMaxRows + 5;

        for (var sequence = 2; sequence <= total; sequence++)
        {
            moves.Add(sequence % 2 == 0
                ? Move(card, sequence, ContentCardColumn.ToDo, ContentCardColumn.InProcess)
                : Move(card, sequence, ContentCardColumn.InProcess, ContentCardColumn.ToDo));
        }

        await SeedAsync(_fixture.WorkspaceA, [.. moves]);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var detail = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{card.Id}", Ct));
        var sequences = detail.GetProperty("history").EnumerateArray().Select(move => move.GetProperty("sequence").GetInt32()).ToList();

        Assert.True(detail.GetProperty("historyTruncated").GetBoolean());
        Assert.Equal(ContentCardPolicy.DetailHistoryMaxRows, sequences.Count);
        Assert.Equal(Enumerable.Range(6, ContentCardPolicy.DetailHistoryMaxRows), sequences);

        // Still the true count, whatever the history was cut to.
        Assert.Equal(total - 1, detail.GetProperty("card").GetProperty("moveCount").GetInt32());
    }

    [Fact]
    public async Task Lineage_names_what_the_card_points_at_and_says_when_it_can_no_longer_be_read()
    {
        var recipe = await CreateRecipeAsync(_fixture.WorkspaceA, "Buttermilk Soda Bread");
        var (contextId, promptId, assetId) = await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now, "Soda bread, autumn");
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return (
                context.Id,
                await CreativeContextSeeds.PromptRecordAsync(db, Now, Ct),
                await CreativeContextSeeds.MediaAssetAsync(db, _fixture.WorkspaceA.Id, Now, Ct));
        });

        var card = Card("Everything linked", ContentCardColumn.ToDo, rank: 1024);
        card.CreativeContextId = contextId;
        card.RecipeId = recipe.RecipeId;
        card.RecipeVersionId = recipe.VersionId;
        card.PromptRecordId = promptId;
        card.MediaAssetId = assetId;
        card.MediaAssetVersionNumber = 1;
        await SeedAsync(_fixture.WorkspaceA, card);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var detail = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{card.Id}", Ct));
        var lineage = detail.GetProperty("lineage").EnumerateArray().ToDictionary(entry => entry.GetProperty("kind").GetString()!);

        Assert.Equal(["CreativeContext", "Recipe", "PromptRecord", "MediaAsset"], detail.GetProperty("lineage").EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()));

        Assert.Equal("Soda bread, autumn", lineage["CreativeContext"].GetProperty("label").GetString());
        Assert.Equal("Buttermilk Soda Bread", lineage["Recipe"].GetProperty("label").GetString());
        Assert.Equal(recipe.VersionId, lineage["Recipe"].GetProperty("versionId").GetGuid());
        Assert.True(lineage["Recipe"].GetProperty("available").GetBoolean());
        Assert.Equal(promptId, lineage["PromptRecord"].GetProperty("id").GetGuid());
        Assert.Equal(1, lineage["MediaAsset"].GetProperty("versionNumber").GetInt32());
        Assert.True(lineage["MediaAsset"].GetProperty("available").GetBoolean());

        // A picture is named, never carried: no bytes, no address.
        Assert.DoesNotContain(
            lineage["MediaAsset"].EnumerateObject().Select(property => property.Name),
            name => name.Contains("url", StringComparison.OrdinalIgnoreCase) || name.Contains("content", StringComparison.OrdinalIgnoreCase));

        // The list row carries the same ids and no names.
        var row = (await ListAsync(client, _fixture.WorkspaceA)).GetProperty("items")[0].GetProperty("lineage");
        Assert.Equal(recipe.RecipeId, row.GetProperty("recipeId").GetGuid());
        Assert.Equal(assetId, row.GetProperty("mediaAssetId").GetGuid());
        Assert.False(row.TryGetProperty("label", out _));

        // The picture is removed from the library. The card still opens, and says so.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var removed = await db.MediaAssets.SingleAsync(asset => asset.Id == assetId, Ct);
            removed.DeletedAt = Now;
            removed.DeletedByMembershipId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var after = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{card.Id}", Ct));
        var asset = after.GetProperty("lineage").EnumerateArray().Single(entry => entry.GetProperty("kind").GetString() == "MediaAsset");
        Assert.False(asset.GetProperty("available").GetBoolean());
        Assert.Equal(assetId, asset.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_packaged_card_shows_its_pins_and_its_window()
    {
        var recipe = await CreateRecipeAsync(_fixture.WorkspaceA, "Buttermilk Soda Bread");
        var assetId = await InWorkspaceAsync(
            _fixture.WorkspaceA, db => CreativeContextSeeds.MediaAssetAsync(db, _fixture.WorkspaceA.Id, Now, Ct));
        var card = Card("Packaged", ContentCardColumn.Approved, rank: 1024);
        var link = new RecipeContentPackageLink
        {
            Id = Guid.NewGuid(),
            ContentCardId = card.Id,
            RecipeId = recipe.RecipeId,
            RecipeVersionId = recipe.VersionId,
            WindowStartsAt = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.Zero),
            WindowEndsAt = new DateTimeOffset(2026, 10, 18, 4, 59, 0, TimeSpan.Zero),
            WindowTimeZoneId = "America/Chicago",
            WindowLocalStartsAt = new DateTime(2026, 10, 16, 9, 0, 0),
            WindowLocalEndsAt = new DateTime(2026, 10, 17, 23, 59, 0),
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

        await SeedAsync(
            _fixture.WorkspaceA,
            card,
            link,
            new RecipeContentPackageLinkAsset { Id = Guid.NewGuid(), RecipeContentPackageLinkId = link.Id, MediaAssetId = assetId, MediaAssetVersionNumber = 1, SortOrder = 0 },

            // Stored out of order: the creator's order is the sort order, not the order of the rows.
            new RecipeContentPackageLinkChannel { Id = Guid.NewGuid(), RecipeContentPackageLinkId = link.Id, ChannelKey = "blog", SortOrder = 1 },
            new RecipeContentPackageLinkChannel { Id = Guid.NewGuid(), RecipeContentPackageLinkId = link.Id, ChannelKey = "instagram", SortOrder = 0 });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var detail = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{card.Id}", Ct));
        var package = detail.GetProperty("package");

        Assert.True(detail.GetProperty("card").GetProperty("hasPackage").GetBoolean());
        Assert.Equal(recipe.VersionId, package.GetProperty("recipeVersionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, package.GetProperty("editorialRevisionId").ValueKind);
        Assert.Equal(assetId, package.GetProperty("assets")[0].GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(1, package.GetProperty("assets")[0].GetProperty("versionNumber").GetInt32());
        Assert.Equal(
            ["instagram", "blog"],
            package.GetProperty("channels").EnumerateArray().Select(channel => channel.GetProperty("channelKey").GetString()));
        Assert.Equal("America/Chicago", package.GetProperty("window").GetProperty("timeZoneId").GetString());
        Assert.Equal(link.WindowStartsAt, package.GetProperty("window").GetProperty("startsAt").GetDateTimeOffset());
    }

    // ---- Who may read, and across workspaces ----

    [Fact]
    public async Task A_viewer_may_read_the_board_and_a_card()
    {
        var card = Card("B's card", ContentCardColumn.ToDo, rank: 1024);
        await SeedAsync(_fixture.WorkspaceB, card);

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(["B's card"], Titles(await ListAsync(viewer, _fixture.WorkspaceB)));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{CardsIn(_fixture.WorkspaceB)}/{card.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Each_workspace_reads_only_its_own_board_and_counts_only_its_own_cards()
    {
        await SeedAsync(
            _fixture.WorkspaceA,
            Card("A one", ContentCardColumn.ToDo, rank: 1024, channel: "instagram", date: Friday),
            Card("A two", ContentCardColumn.Approved, rank: 1024, channel: "instagram", date: Friday));
        await SeedAsync(_fixture.WorkspaceB, Card("B one", ContentCardColumn.ToDo, rank: 1024, channel: "instagram", date: Friday));

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        foreach (var query in new[] { "", "?channel=instagram", "?day=Friday", "?date=2026-10-16", "?column=ToDo" })
        {
            var a = await ListAsync(inA, _fixture.WorkspaceA, query);
            var b = await ListAsync(inB, _fixture.WorkspaceB, query);

            Assert.DoesNotContain("B one", Titles(a));
            Assert.Equal(["B one"], Titles(b));
            Assert.Equal(1, b.GetProperty("columns").EnumerateArray().Sum(column => column.GetProperty("count").GetInt32()));
            Assert.Equal(Titles(a).Count, a.GetProperty("columns").EnumerateArray().Sum(column => column.GetProperty("count").GetInt32()));
        }
    }

    [Fact]
    public async Task Another_workspaces_card_and_an_unknown_card_answer_identically()
    {
        var card = Card("A's card, in A's words.", ContentCardColumn.ToDo, rank: 1024);
        await SeedAsync(_fixture.WorkspaceA, card, Move(card, 1, null, ContentCardColumn.ToDo));

        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var foreign = await inB.GetAsync($"{CardsIn(_fixture.WorkspaceB)}/{card.Id}", Ct);
        var unknown = await inB.GetAsync($"{CardsIn(_fixture.WorkspaceB)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var foreignBody = await BodyOf(foreign);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.CardNotFound, Code(foreignBody));

        // The same answer in every part a client can read, apart from the request's own correlation id.
        foreach (var name in new[] { "code", "title", "status", "detail", "type" })
        {
            Assert.Equal(
                foreignBody.TryGetProperty(name, out var left) ? left.ToString() : null,
                unknownBody.TryGetProperty(name, out var right) ? right.ToString() : null);
        }

        Assert.Equal(
            foreignBody.EnumerateObject().Select(property => property.Name).Order(),
            unknownBody.EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain("A's card", foreignBody.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_anothers_board_by_its_route()
    {
        var card = Card("A's card", ContentCardColumn.ToDo, rank: 1024);
        await SeedAsync(_fixture.WorkspaceA, card);

        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        // An unknown workspace and one the caller is not in answer alike (tenancy.md).
        var list = await inB.GetAsync(CardsIn(_fixture.WorkspaceA), Ct);
        var detail = await inB.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{card.Id}", Ct);
        var nowhere = await inB.GetAsync("/api/v1/workspaces/no-such-workspace/content-cards", Ct);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
        Assert.Equal(Code(await BodyOf(nowhere)), Code(await BodyOf(list)));
    }

    [Fact]
    public async Task An_anonymous_request_is_refused()
    {
        using var anonymous = _fixture.Gateway.CreateClient(CreatorPantry.Tests.Gateway.GatewayTestHost.HttpsClient);

        var response = await anonymous.GetAsync(CardsIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Helpers ----

    /// <summary>Six cards across channels, days, dates and columns, for the filter tests.</summary>
    private Task SeedFilterBoardAsync(SeededWorkspace workspace) => SeedAsync(
        workspace,
        Card("Insta Friday", ContentCardColumn.ToDo, rank: 1024, channel: "instagram", date: Friday),
        Card("Undated", ContentCardColumn.ToDo, rank: 2048),
        Card("Insta next Friday", ContentCardColumn.InProcess, rank: 1024, channel: "instagram", date: NextFriday),
        Card("Blog Monday", ContentCardColumn.InProcess, rank: 2048, channel: "blog", date: Monday),
        Card("No channel, Tuesday", ContentCardColumn.ReadyForReview, rank: 1024, date: new DateOnly(2026, 10, 20)),
        Card("Blog Friday", ContentCardColumn.Approved, rank: 1024, channel: "blog", date: Friday));

    private static ContentCard Card(
        string title, ContentCardColumn column, long rank, string? channel = null, DateOnly? date = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Column = column,
        SortRank = rank,
        ChannelKey = channel,
        PlannedDate = date,
        PlannedDayOfWeek = date?.DayOfWeek,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static CardMoveHistory Move(
        ContentCard card, int sequence, ContentCardColumn? from, ContentCardColumn to, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentCardId = card.Id,
        Sequence = sequence,
        FromColumn = from,
        ToColumn = to,
        Note = note,
        ActorMembershipId = Guid.NewGuid(),
        OccurredAt = Now,
    };

    private Task SeedAsync(SeededWorkspace workspace, params object[] rows) =>
        InWorkspaceAsync(workspace, async db =>
        {
            db.AddRange(rows);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>A recipe and its first version, created through the real route so the title is a real one.</summary>
    private async Task<(Guid RecipeId, Guid VersionId)> CreateRecipeAsync(SeededWorkspace workspace, string title)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("recipeId").GetGuid(), body.GetProperty("versionId").GetGuid());
    }
}
