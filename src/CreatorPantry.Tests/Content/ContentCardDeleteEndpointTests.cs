using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// <c>DELETE .../content-cards/{cardId}</c> through the real Gateway (13.6c, KAN-006): what goes with a card and
/// what does not, the picture that is never removed unless the request says so, replay, a removal that fails
/// part-way, the role bar, and the boundary between two workspaces.
/// </summary>
/// <remarks>
/// <para>
/// As for the library's own delete, the assertions that matter most are the negative ones: almost every test
/// here checks what <em>survived</em>.
/// </para>
/// <para>
/// SQLite never moves a <c>RowVersion</c>, so a token check is exercised with a token the row does not hold. A
/// deletion that loses a real race is <c>ContentBoardTests</c>' on SQL Server.
/// </para>
/// </remarks>
public sealed class ContentCardDeleteEndpointTests : IAsyncLifetime
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

    private static string TokenOf(JsonElement detail) => detail.GetProperty("card").GetProperty("concurrencyToken").GetString()!;

    /// <summary>Creates a card through the real route, so it has its creation row, and returns its detail.</summary>
    private static async Task<JsonElement> CreateAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(CardsIn(workspace), body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    /// <summary>Sends a deletion. A key is generated unless one is given; pass an empty string to send none.</summary>
    private static Task<HttpResponseMessage> DeleteAsync(
        GatewayClient client, SeededWorkspace workspace, Guid cardId, object body, string? key = null) =>
        client.SendAsync(
            HttpMethod.Delete,
            $"{CardsIn(workspace)}/{cardId}",
            body,
            key == string.Empty ? null : new Dictionary<string, string> { ["Idempotency-Key"] = key ?? Guid.NewGuid().ToString() },
            Ct);

    private static object Confirmed(JsonElement detail) => new { confirmed = true, expectedConcurrencyToken = TokenOf(detail) };

    // ---- What goes with the card, and what does not ----

    [Fact]
    public async Task Deleting_a_card_takes_its_history_and_its_package_and_nothing_it_pointed_at()
    {
        var recipe = await CreateRecipeAsync(_fixture.WorkspaceA, "Buttermilk Soda Bread");
        var (contextId, promptId, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var card = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            title = "A very private title",
            description = "Private words.",
            creativeContextId = contextId,
            recipeId = recipe.RecipeId,
            recipeVersionId = recipe.VersionId,
            promptRecordId = promptId,
            mediaAssetId = assetId,
            mediaAssetVersionNumber = 1,
        });
        var id = IdOf(card);

        // Two moves, and a recipe package with a picture and a channel pinned in it.
        card = await MoveAsync(client, card, "InProcess", "A private note.");
        card = await MoveAsync(client, card, "Approved");
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var link = new RecipeContentPackageLink
            {
                Id = Guid.NewGuid(), ContentCardId = id, RecipeId = recipe.RecipeId, RecipeVersionId = recipe.VersionId,
                CreatedByMembershipId = Guid.NewGuid(), CreatedAt = Now,
            };
            db.AddRange(
                link,
                new RecipeContentPackageLinkAsset { Id = Guid.NewGuid(), RecipeContentPackageLinkId = link.Id, MediaAssetId = assetId, MediaAssetVersionNumber = 1, SortOrder = 0 },
                new RecipeContentPackageLinkChannel { Id = Guid.NewGuid(), RecipeContentPackageLinkId = link.Id, ChannelKey = "instagram", SortOrder = 0 });
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var response = await DeleteAsync(client, _fixture.WorkspaceA, id, Confirmed(card));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var deleted = await BodyOf(response);
        Assert.Equal(id, deleted.GetProperty("id").GetGuid());
        Assert.Equal("Approved", deleted.GetProperty("column").GetString());
        Assert.Equal(2, deleted.GetProperty("moveCount").GetInt32());
        Assert.True(deleted.GetProperty("hadPackage").GetBoolean());
        Assert.Equal("Kept", deleted.GetProperty("linkedAsset").GetProperty("outcome").GetString());
        Assert.Equal(assetId, deleted.GetProperty("linkedAsset").GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("linkedAsset").GetProperty("affected").ValueKind);

        // None of the creator's words come back with the answer.
        Assert.DoesNotContain("private", deleted.ToString(), StringComparison.OrdinalIgnoreCase);

        // The card and everything that was only ever about the card are gone.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{id}", Ct)).StatusCode);
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            Assert.False(await db.ContentCards.AnyAsync(Ct));
            Assert.False(await db.CardMoveHistory.AnyAsync(Ct));
            Assert.False(await db.RecipeContentPackageLinks.AnyAsync(Ct));
            Assert.False(await db.RecipeContentPackageLinkAssets.AnyAsync(Ct));
            Assert.False(await db.RecipeContentPackageLinkChannels.AnyAsync(Ct));

            // Everything it pointed at is exactly where it was.
            Assert.True(await db.Recipes.AnyAsync(item => item.Id == recipe.RecipeId, Ct));
            Assert.True(await db.RecipeVersions.AnyAsync(item => item.Id == recipe.VersionId, Ct));
            Assert.True(await db.CreativeContexts.AnyAsync(item => item.Id == contextId, Ct));
            Assert.True(await db.PromptRecords.AnyAsync(item => item.Id == promptId, Ct));

            return 0;
        });

        // The picture was not asked about, so it was not touched: no tombstone, and still in the library.
        var asset = await AssetAsync(_fixture.WorkspaceA, assetId);
        Assert.Null(asset.DeletedAt);
        Assert.Null(asset.DeletedByMembershipId);
    }

    [Fact]
    public async Task A_card_with_no_picture_says_so_and_keep_is_what_saying_nothing_means()
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var bare = await CreateAsync(client, _fixture.WorkspaceA, new { title = "No picture" });
        var none = await BodyOf(await DeleteAsync(client, _fixture.WorkspaceA, IdOf(bare), Confirmed(bare)));
        Assert.Equal("None", none.GetProperty("linkedAsset").GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("linkedAsset").GetProperty("mediaAssetId").ValueKind);
        Assert.Equal(0, none.GetProperty("moveCount").GetInt32());
        Assert.False(none.GetProperty("hadPackage").GetBoolean());

        var pictured = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Pictured", mediaAssetId = assetId });
        var kept = await BodyOf(await DeleteAsync(
            client, _fixture.WorkspaceA, IdOf(pictured), new { confirmed = true, expectedConcurrencyToken = TokenOf(pictured), linkedAsset = "Keep" }));

        Assert.Equal("Kept", kept.GetProperty("linkedAsset").GetProperty("outcome").GetString());
        Assert.Null((await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt);
    }

    // ---- The picture, removed only when asked ----

    [Fact]
    public async Task Remove_takes_the_picture_out_of_the_library_without_deleting_its_bytes_or_cutting_other_links()
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        var versionBefore = await AssetVersionAsync(_fixture.WorkspaceA, assetId);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Going", mediaAssetId = assetId });
        var other = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Staying", mediaAssetId = assetId });

        var response = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(card),
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, assetId),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var linked = (await BodyOf(response)).GetProperty("linkedAsset");
        Assert.Equal("Removed", linked.GetProperty("outcome").GetString());
        Assert.False(linked.GetProperty("alreadyRemoved").GetBoolean());

        // The other card that points at it is counted, so the creator is told what they have just affected.
        Assert.Equal(1, linked.GetProperty("otherCardCount").GetInt32());

        // What else points at it is counted and never named: this answer is stored for a replay, and the title
        // of a recipe is the creator's own words.
        var affected = linked.GetProperty("affected");
        Assert.Equal(
            ["any", "brandProfileCount", "recipeCount", "testAttachmentCount"],
            affected.EnumerateObject().Select(property => property.Name).Order());
        Assert.False(affected.GetProperty("any").GetBoolean());

        // A tombstone, by the library's own rules: nothing physical is removed.
        var asset = await AssetAsync(_fixture.WorkspaceA, assetId);
        Assert.NotNull(asset.DeletedAt);
        Assert.NotNull(asset.DeletedByMembershipId);

        var versionAfter = await AssetVersionAsync(_fixture.WorkspaceA, assetId);
        Assert.Equal((versionBefore.Id, versionBefore.ObjectKey, versionBefore.ContentChecksum), (versionAfter.Id, versionAfter.ObjectKey, versionAfter.ContentChecksum));

        // And the other card's link is left standing: it still says what it pointed at, and says it is unavailable.
        var stillThere = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{IdOf(other)}", Ct));
        var entry = stillThere.GetProperty("lineage").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "MediaAsset");
        Assert.Equal(assetId, entry.GetProperty("id").GetGuid());
        Assert.False(entry.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Remove_reaches_only_the_cards_own_picture_never_one_pinned_in_its_package()
    {
        var recipe = await CreateRecipeAsync(_fixture.WorkspaceA, "Bread");
        var (_, _, own) = await SeedLineageAsync(_fixture.WorkspaceA);
        var (_, _, pinned) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Packaged", mediaAssetId = own });

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var link = new RecipeContentPackageLink
            {
                Id = Guid.NewGuid(), ContentCardId = IdOf(card), RecipeId = recipe.RecipeId, RecipeVersionId = recipe.VersionId,
                CreatedByMembershipId = Guid.NewGuid(), CreatedAt = Now,
            };
            db.AddRange(link, new RecipeContentPackageLinkAsset
            {
                Id = Guid.NewGuid(), RecipeContentPackageLinkId = link.Id, MediaAssetId = pinned, MediaAssetVersionNumber = 1, SortOrder = 0,
            });
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var response = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(card),
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, own),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await AssetAsync(_fixture.WorkspaceA, own)).DeletedAt);
        Assert.Null((await AssetAsync(_fixture.WorkspaceA, pinned)).DeletedAt);
    }

    [Fact]
    public async Task When_the_picture_cannot_be_removed_as_asked_the_card_is_not_deleted_either()
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Both or neither", mediaAssetId = assetId });
        card = await MoveAsync(client, card, "InProcess");
        var key = Guid.NewGuid().ToString();
        var body = new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(card),
            linkedAsset = "Remove",

            // A token the picture does not hold: the creator's read of it is out of date.
            linkedAssetConcurrencyToken = TokenOfAnotherState,
        };

        var response = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), body, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CardLinkedAssetConflict, Code(await BodyOf(response)));

        // The card's deletion had already been written when the picture's removal was refused. It was undone:
        // the card, its two history rows, and no trace of a deletion.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{IdOf(card)}", Ct)).StatusCode);
        Assert.Equal(2, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.CardMoveHistory.CountAsync(Ct)));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardDeleted));
        Assert.Null((await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt);

        // Nothing was remembered against the key, so the corrected request under the same key simply works.
        var corrected = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(card),
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, assetId),
        }, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.NotNull((await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt);
    }

    [Theory]
    [InlineData("not confirmed", "confirmed")]
    [InlineData("confirmed false", "confirmed")]
    [InlineData("remove with no picture token", "linkedAssetConcurrencyToken")]
    [InlineData("picture token without remove", "linkedAssetConcurrencyToken")]
    [InlineData("remove on a card with no picture", "linkedAsset")]
    [InlineData("no card token", "expectedConcurrencyToken")]
    [InlineData("malformed card token", "expectedConcurrencyToken")]
    public async Task A_deletion_that_does_not_say_what_it_means_is_a_bad_request_and_deletes_nothing(string fault, string field)
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var pictured = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Pictured", mediaAssetId = assetId });
        var bare = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Bare" });
        var assetToken = await AssetTokenAsync(_fixture.WorkspaceA, assetId);

        var (card, body) = fault switch
        {
            "not confirmed" => (pictured, (object)new { expectedConcurrencyToken = TokenOf(pictured) }),
            "confirmed false" => (pictured, new { confirmed = false, expectedConcurrencyToken = TokenOf(pictured) }),
            "remove with no picture token" => (pictured, new { confirmed = true, expectedConcurrencyToken = TokenOf(pictured), linkedAsset = "Remove" }),
            "picture token without remove" => (pictured, new { confirmed = true, expectedConcurrencyToken = TokenOf(pictured), linkedAssetConcurrencyToken = assetToken }),
            "remove on a card with no picture" => (bare, new { confirmed = true, expectedConcurrencyToken = TokenOf(bare), linkedAsset = "Remove", linkedAssetConcurrencyToken = assetToken }),
            "no card token" => (pictured, new { confirmed = true }),
            _ => (pictured, new { confirmed = true, expectedConcurrencyToken = "not-a-token" }),
        };

        var response = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CardInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));

        Assert.Equal(2, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.CountAsync(Ct)));
        Assert.Null((await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt);
    }

    // ---- Conflict ----

    [Fact]
    public async Task A_token_the_card_does_not_hold_is_a_conflict_and_nothing_is_deleted()
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Mine", mediaAssetId = assetId });

        // Even asking for the picture to go, with the picture's real token: the card's token is checked first.
        var response = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOfAnotherState,
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, assetId),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CardStale, Code(await BodyOf(response)));
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.CountAsync(Ct)));
        Assert.Null((await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt);
    }

    // ---- Replay ----

    [Fact]
    public async Task A_deletion_needs_an_idempotency_key()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Mine" });

        var response = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), Confirmed(card), key: string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.CountAsync(Ct)));
    }

    [Fact]
    public async Task Replaying_a_deletion_returns_the_first_answer_and_deletes_nothing_more()
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Going", mediaAssetId = assetId });
        var bystander = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Bystander" });
        var key = Guid.NewGuid().ToString();
        var body = new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(card),
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, assetId),
        };

        var first = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), body, key);
        var removedAt = (await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt;
        var replay = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));

        // The whole first answer — including what became of the picture, which nothing else could now say.
        Assert.Equal((await BodyOf(first)).ToString(), (await BodyOf(replay)).ToString());

        Assert.Equal(1, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardDeleted));
        Assert.Equal(removedAt, (await AssetAsync(_fixture.WorkspaceA, assetId)).DeletedAt);

        // The one entry says what was done about the picture, by id and state.
        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Action == ContentAuditActions.ContentCardDeleted, Ct));
        Assert.Equal("picture:removed", entry.AfterReference);
        Assert.Contains(assetId.ToString("D"), entry.Summary, StringComparison.Ordinal);

        // And what is stored against the key is the answer itself, which holds nobody's words.
        await using (var scope = _fixture.Api.Factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().IdempotencyRecords
                .AsNoTracking().Where(record => record.Key == key).Select(record => record.ResultJson).SingleAsync(Ct);

            Assert.DoesNotContain("Going", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("\"recipes\"", stored, StringComparison.OrdinalIgnoreCase);
        }

        // The same key against another card is a different request, not a replay: refused, and that card stays.
        var reused = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(bystander), Confirmed(bystander), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(["Bystander"], await TitlesAsync(_fixture.WorkspaceA));

        // Without the key, a second request can only say the card is not there.
        var again = await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), body);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    // ---- Who may delete, and across workspaces ----

    [Fact]
    public async Task A_viewer_may_not_delete_and_an_editor_may()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, new { title = "B's card" });

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(viewer, _fixture.WorkspaceB, IdOf(inB), Confirmed(inB))).StatusCode);
        Assert.Equal(["B's card"], await TitlesAsync(_fixture.WorkspaceB));

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, new { title = "A's card" });

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(editor, _fixture.WorkspaceA, IdOf(inA), Confirmed(inA))).StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_card_and_an_unknown_card_answer_identically_and_nothing_is_deleted()
    {
        var (_, _, assetA) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var cardA = await CreateAsync(inA, _fixture.WorkspaceA, new { title = "A's card", mediaAssetId = assetA });

        // B, on its own route, naming A's card with A's real tokens and asking for A's picture to go too.
        var body = new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(cardA),
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, assetA),
        };

        var foreign = await DeleteAsync(inB, _fixture.WorkspaceB, IdOf(cardA), body);
        var unknown = await DeleteAsync(inB, _fixture.WorkspaceB, Guid.NewGuid(), body);

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
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(inB, _fixture.WorkspaceA, IdOf(cardA), body)).StatusCode);

        Assert.Equal(["A's card"], await TitlesAsync(_fixture.WorkspaceA));
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.CardMoveHistory.CountAsync(Ct)));
        Assert.Null((await AssetAsync(_fixture.WorkspaceA, assetA)).DeletedAt);
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardDeleted));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, ContentAuditActions.ContentCardDeleted));
    }

    [Fact]
    public async Task A_deletion_in_one_workspace_touches_nothing_in_the_other_and_a_key_does_not_cross()
    {
        var (_, _, assetA) = await SeedLineageAsync(_fixture.WorkspaceA);
        var (_, _, assetB) = await SeedLineageAsync(_fixture.WorkspaceB);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var cardA = await CreateAsync(inA, _fixture.WorkspaceA, new { title = "The same title", mediaAssetId = assetA });
        var cardB = await CreateAsync(inB, _fixture.WorkspaceB, new { title = "The same title", mediaAssetId = assetB });
        var key = Guid.NewGuid().ToString();

        var deleted = await DeleteAsync(inA, _fixture.WorkspaceA, IdOf(cardA), new
        {
            confirmed = true,
            expectedConcurrencyToken = TokenOf(cardA),
            linkedAsset = "Remove",
            linkedAssetConcurrencyToken = await AssetTokenAsync(_fixture.WorkspaceA, assetA),
        }, key);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // B's card, its history and its picture are exactly as they were.
        Assert.Equal(["The same title"], await TitlesAsync(_fixture.WorkspaceB));
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.CardMoveHistory.CountAsync(Ct)));
        Assert.Null((await AssetAsync(_fixture.WorkspaceB, assetB)).DeletedAt);
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, ContentAuditActions.ContentCardDeleted));

        // The same key in B is B's own first request, not a replay of A's.
        var inOther = await DeleteAsync(inB, _fixture.WorkspaceB, IdOf(cardB), Confirmed(cardB), key);

        Assert.Equal(HttpStatusCode.OK, inOther.StatusCode);
        Assert.False(inOther.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(IdOf(cardB), (await BodyOf(inOther)).GetProperty("id").GetGuid());
        Assert.Null((await AssetAsync(_fixture.WorkspaceB, assetB)).DeletedAt);
    }

    // ---- Audit ----

    [Fact]
    public async Task A_deletion_leaves_an_audit_entry_that_names_the_card_and_none_of_the_creators_words()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(client, _fixture.WorkspaceA, new { title = "A very private title", description = "Private words." });
        card = await MoveAsync(client, card, "ReadyForReview", "A private note.");

        await DeleteAsync(client, _fixture.WorkspaceA, IdOf(card), Confirmed(card));

        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Action == ContentAuditActions.ContentCardDeleted, Ct));

        // With the history gone, this is what remains of the card: its id, where it was, how often it moved.
        Assert.Equal(IdOf(card).ToString("D"), entry.ResourceId);
        Assert.Equal("ReadyForReview", entry.BeforeReference);
        Assert.Contains("ReadyForReview", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("1 time", entry.Summary, StringComparison.Ordinal);
        Assert.Equal("picture:none", entry.AfterReference);
        Assert.False(string.IsNullOrEmpty(entry.ActorUserId));
        Assert.DoesNotContain("private", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    private async Task<JsonElement> MoveAsync(GatewayClient client, JsonElement card, string column, string? note = null)
    {
        var response = await client.PostAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{IdOf(card)}/move",
            new { expectedConcurrencyToken = TokenOf(card), column, note },
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private Task<int> AuditCountAsync(SeededWorkspace workspace, string action) =>
        InWorkspaceAsync(workspace, db => db.AuditLogs.CountAsync(log => log.Action == action, Ct));

    private Task<List<string>> TitlesAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.ContentCards.AsNoTracking().OrderBy(card => card.Title).Select(card => card.Title).ToListAsync(Ct));

    /// <summary>The picture whether or not it has been removed: a removed one is hidden from an ordinary read.</summary>
    private Task<MediaAsset> AssetAsync(SeededWorkspace workspace, Guid assetId) =>
        InWorkspaceAsync(workspace, db => db.MediaAssets.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(asset => asset.Id == assetId && asset.WorkspaceId == workspace.Id, Ct));

    private Task<MediaAssetVersion> AssetVersionAsync(SeededWorkspace workspace, Guid assetId) =>
        InWorkspaceAsync(workspace, db => db.MediaAssetVersions.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(version => version.MediaAssetId == assetId && version.WorkspaceId == workspace.Id, Ct));

    private async Task<string> AssetTokenAsync(SeededWorkspace workspace, Guid assetId) =>
        MediaConcurrencyToken.From((await AssetAsync(workspace, assetId)).RowVersion);

    private Task<(Guid ContextId, Guid PromptId, Guid AssetId)> SeedLineageAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return (
                context.Id,
                await CreativeContextSeeds.PromptRecordAsync(db, Now, Ct),
                await CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now, Ct));
        });

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>A recipe and its first version, created through the real route so the pins are real.</summary>
    private async Task<(Guid RecipeId, Guid VersionId)> CreateRecipeAsync(SeededWorkspace workspace, string title)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("recipeId").GetGuid(), body.GetProperty("versionId").GetGuid());
    }
}
