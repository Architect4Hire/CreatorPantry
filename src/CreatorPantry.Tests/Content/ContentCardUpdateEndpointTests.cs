using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// <c>PATCH .../content-cards/{cardId}</c> through the real Gateway (13.6a, KAN-004): what a patch changes and
/// what it leaves alone, clearing, the rules that stop a link being dropped by accident, the concurrency token,
/// the role bar, and the boundary between two workspaces.
/// </summary>
/// <remarks>
/// <strong>What SQLite cannot show here.</strong> Its <c>RowVersion</c> is filled on insert and never moves, so
/// a successful edit returns the same token it was sent and a second edit with the old token still lands. The
/// token <em>check</em> is exercised here with a token the card does not hold; the row version really moving,
/// and a write that loses a race at the store, are <c>ContentBoardTests</c>' on SQL Server.
/// </remarks>
public sealed class ContentCardUpdateEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly string TokenOfAnotherState = Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9]);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Cuisines.AddRange(
            new Cuisine { Id = Guid.NewGuid(), Code = "irish", DisplayName = "Irish", IsActive = true },
            new Cuisine { Id = Guid.NewGuid(), Code = "thai", DisplayName = "Thai", IsActive = true });
        db.Courses.Add(new Course { Id = Guid.NewGuid(), Code = "bread", DisplayName = "Bread", IsActive = true });
        db.CookingTechniques.Add(new CookingTechnique { Id = Guid.NewGuid(), Code = "bake", DisplayName = "Bake", IsActive = true });
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CardsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/content-cards";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    /// <summary>Creates a card through the real route and returns its detail.</summary>
    private static async Task<JsonElement> CreateAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(CardsIn(workspace), body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    /// <summary>Patches a card, quoting the token of the detail it was read as unless one is given.</summary>
    private static Task<HttpResponseMessage> PatchAsync(
        GatewayClient client, SeededWorkspace workspace, JsonElement detail, Dictionary<string, object?> fields, string? token = null)
    {
        var card = detail.GetProperty("card");
        fields["expectedConcurrencyToken"] = token ?? card.GetProperty("concurrencyToken").GetString();

        return client.PatchAsJsonAsync($"{CardsIn(workspace)}/{card.GetProperty("id").GetGuid()}", fields, Ct);
    }

    private static object FullCard(Guid? recipeId = null, Guid? recipeVersionId = null, Guid? promptId = null) => new
    {
        title = "Soda bread, Friday",
        description = "Hero shot and the three posts.",
        column = "InProcess",
        channelKey = "instagram",
        plannedDate = "2026-10-16",
        cuisineCode = "irish",
        dishTypeCode = "bread",
        methodCode = "bake",
        recipeId,
        recipeVersionId,
        promptRecordId = promptId,
    };

    // ---- Partial ----

    [Theory]
    [InlineData("title", "Soda bread, Saturday")]
    [InlineData("description", "Just the hero shot.")]
    [InlineData("channelKey", "blog")]
    [InlineData("plannedDate", "2026-10-19")]
    [InlineData("cuisineCode", "thai")]
    public async Task A_patch_changes_the_one_field_it_mentions_and_leaves_every_other_as_it_was(string field, string value)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        var before = await StoredAsync(_fixture.WorkspaceA, created);

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { [field] = value });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var after = await StoredAsync(_fixture.WorkspaceA, created);
        var expected = new Dictionary<string, object?>
        {
            ["title"] = before.Title,
            ["description"] = before.Description,
            ["channelKey"] = before.ChannelKey,
            ["plannedDate"] = before.PlannedDate?.ToString("yyyy-MM-dd"),
            ["cuisineCode"] = before.CuisineCode,
            ["dishTypeCode"] = before.DishTypeCode,
            ["methodCode"] = before.MethodCode,
            [field] = value,
        };

        Assert.Equal(expected["title"], after.Title);
        Assert.Equal(expected["description"], after.Description);
        Assert.Equal(expected["channelKey"], after.ChannelKey);
        Assert.Equal(expected["plannedDate"], after.PlannedDate?.ToString("yyyy-MM-dd"));
        Assert.Equal(expected["cuisineCode"], after.CuisineCode);
        Assert.Equal(expected["dishTypeCode"], after.DishTypeCode);
        Assert.Equal(expected["methodCode"], after.MethodCode);

        // An edit is not a move: the column, the place and the history are exactly as they were.
        Assert.Equal((before.Column, before.SortRank), (after.Column, after.SortRank));
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.CardMoveHistory.CountAsync(Ct)));

        // And the response is the card as it now is.
        var detail = await BodyOf(response);
        Assert.Equal(after.Title, detail.GetProperty("card").GetProperty("title").GetString());
        Assert.Equal(0, detail.GetProperty("card").GetProperty("moveCount").GetInt32());
    }

    [Fact]
    public async Task Changing_the_date_re_derives_the_day_and_the_day_cannot_be_sent()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        Assert.Equal("Friday", created.GetProperty("card").GetProperty("plannedDay").GetString());

        // A weekday that is not the new date's is not a field of the request, so it is not read.
        var moved = await BodyOf(await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["plannedDate"] = "2026-10-19", ["plannedDay"] = "Sunday" }));

        Assert.Equal("2026-10-19", moved.GetProperty("card").GetProperty("plannedDate").GetString());
        Assert.Equal("Monday", moved.GetProperty("card").GetProperty("plannedDay").GetString());

        var stored = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal(DayOfWeek.Monday, stored.PlannedDayOfWeek);
    }

    [Fact]
    public async Task A_patch_that_changes_nothing_writes_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        var before = await StoredAsync(_fixture.WorkspaceA, created);

        // The same values, one of them differing only in the space around it, and one field not mentioned.
        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["title"] = "  Soda bread, Friday  ",
            ["channelKey"] = "instagram",
            ["plannedDate"] = "2026-10-16",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            created.GetProperty("card").GetProperty("concurrencyToken").GetString(),
            (await BodyOf(response)).GetProperty("card").GetProperty("concurrencyToken").GetString());

        var after = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.UpdatedByMembershipId, after.UpdatedByMembershipId);
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardUpdated));
    }

    // ---- Clear ----

    [Fact]
    public async Task A_field_sent_as_null_is_cleared_and_clearing_the_date_clears_the_day()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["description"] = null,
            ["channelKey"] = null,
            ["plannedDate"] = null,
            ["cuisineCode"] = null,
            ["dishTypeCode"] = "   ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = (await BodyOf(response)).GetProperty("card");
        Assert.Equal(JsonValueKind.Null, card.GetProperty("plannedDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("plannedDay").ValueKind);

        var stored = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Null(stored.Description);
        Assert.Null(stored.ChannelKey);
        Assert.Null(stored.PlannedDate);
        Assert.Null(stored.PlannedDayOfWeek);
        Assert.Null(stored.CuisineCode);

        // Whitespace is nothing, the form every optional text field is stored in.
        Assert.Null(stored.DishTypeCode);

        // Not mentioned, so untouched.
        Assert.Equal("bake", stored.MethodCode);
        Assert.Equal("Soda bread, Friday", stored.Title);
    }

    [Theory]
    [InlineData("title", null)]
    [InlineData("title", "   ")]
    [InlineData("column", "Approved")]
    [InlineData("channelKey", "myspace")]
    [InlineData("cuisineCode", "martian")]
    [InlineData("mediaAssetVersionNumber", 0)]
    public async Task A_patch_that_cannot_be_applied_is_a_bad_request_naming_the_field_and_changes_nothing(string field, object? value)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        var before = await StoredAsync(_fixture.WorkspaceA, created);

        // Sent beside a change that would otherwise be fine: a refused patch applies none of itself.
        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { [field] = value, ["description"] = "Changed." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CardInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));

        var after = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Column, after.Column);
    }

    [Fact]
    public async Task A_channel_or_code_the_card_already_holds_stays_valid_when_it_is_no_longer_offered()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        var id = created.GetProperty("card").GetProperty("id").GetGuid();

        // The card as it would be after its cuisine and channel were retired: it holds values nothing offers.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var card = await db.ContentCards.SingleAsync(item => item.Id == id, Ct);
            card.CuisineCode = "retired-cuisine";
            card.ChannelKey = "retired-channel";
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var reread = await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{id}", Ct));

        // An unrelated edit, and one that sends the retired values back unchanged: neither chooses them anew.
        var edited = await PatchAsync(client, _fixture.WorkspaceA, reread, new()
        {
            ["title"] = "Renamed",
            ["cuisineCode"] = "retired-cuisine",
            ["channelKey"] = "retired-channel",
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var stored = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal(("Renamed", "retired-cuisine", "retired-channel"), (stored.Title, stored.CuisineCode, stored.ChannelKey));

        // But neither can be newly chosen on a card that does not hold it.
        var other = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Another" });

        foreach (var (field, value) in new[] { ("cuisineCode", "retired-cuisine"), ("channelKey", "retired-channel") })
        {
            var refused = await PatchAsync(client, _fixture.WorkspaceA, other, new() { [field] = value });

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.True((await BodyOf(refused)).GetProperty("errors").TryGetProperty(field, out _));
        }
    }

    // ---- Lineage: explicit unlinking, and nothing dropped by accident ----

    [Fact]
    public async Task A_prompt_is_unlinked_only_by_sending_it_as_null()
    {
        var (_, promptId, _) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard(promptId: promptId));

        // Edits that do not mention the prompt leave it linked, however much else they change.
        var edited = await BodyOf(await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["title"] = "Renamed",
            ["description"] = null,
            ["channelKey"] = null,
            ["plannedDate"] = null,
        }));
        Assert.Equal(promptId, (await StoredAsync(_fixture.WorkspaceA, created)).PromptRecordId);
        Assert.Contains(edited.GetProperty("lineage").EnumerateArray(), entry => entry.GetProperty("kind").GetString() == "PromptRecord");

        var unlinked = await PatchAsync(client, _fixture.WorkspaceA, edited, new() { ["promptRecordId"] = null });

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Null((await StoredAsync(_fixture.WorkspaceA, created)).PromptRecordId);
        Assert.DoesNotContain(
            (await BodyOf(unlinked)).GetProperty("lineage").EnumerateArray(),
            entry => entry.GetProperty("kind").GetString() == "PromptRecord");

        // Unlinking removes the link and nothing else: the prompt is still in the library.
        Assert.True(await InWorkspaceAsync(_fixture.WorkspaceA, db => db.PromptRecords.AnyAsync(item => item.Id == promptId, Ct)));
    }

    [Fact]
    public async Task A_version_pin_is_never_dropped_as_a_side_effect()
    {
        var first = await CreateRecipeAsync(_fixture.WorkspaceA, "First bread");
        var second = await CreateRecipeAsync(_fixture.WorkspaceA, "Second bread");
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard(first.RecipeId, first.VersionId));

        // Replacing or clearing the recipe while saying nothing about the pin: refused, and nothing changes.
        foreach (var fields in new[]
                 {
                     new Dictionary<string, object?> { ["recipeId"] = second.RecipeId },
                     new Dictionary<string, object?> { ["recipeId"] = null },
                 })
        {
            var refused = await PatchAsync(client, _fixture.WorkspaceA, created, fields);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.True((await BodyOf(refused)).GetProperty("errors").TryGetProperty("recipeVersionId", out _));
        }

        var untouched = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal((first.RecipeId, first.VersionId), (untouched.RecipeId!.Value, untouched.RecipeVersionId!.Value));

        // The new recipe with the old recipe's version is still the wrong pin.
        var wrongPin = await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["recipeId"] = second.RecipeId, ["recipeVersionId"] = first.VersionId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongPin.StatusCode);
        Assert.True((await BodyOf(wrongPin)).GetProperty("errors").TryGetProperty("recipeVersionId", out _));

        // Said in full, each of these is allowed: a new recipe at its own version, then the pin dropped on
        // purpose, then the recipe itself unlinked.
        var replaced = await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["recipeId"] = second.RecipeId, ["recipeVersionId"] = second.VersionId });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        var unpinned = await PatchAsync(client, _fixture.WorkspaceA, await BodyOf(replaced), new() { ["recipeVersionId"] = null });
        Assert.Equal(HttpStatusCode.OK, unpinned.StatusCode);
        var afterUnpin = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal(second.RecipeId, afterUnpin.RecipeId);
        Assert.Null(afterUnpin.RecipeVersionId);

        var unlinked = await PatchAsync(client, _fixture.WorkspaceA, await BodyOf(unpinned), new() { ["recipeId"] = null });
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Null((await StoredAsync(_fixture.WorkspaceA, created)).RecipeId);

        // A version with no recipe is a pin on nothing.
        var orphanPin = await PatchAsync(client, _fixture.WorkspaceA, await BodyOf(unlinked), new() { ["recipeVersionId"] = second.VersionId });
        Assert.Equal(HttpStatusCode.BadRequest, orphanPin.StatusCode);
    }

    [Fact]
    public async Task A_picture_pin_follows_the_same_rules_and_can_be_dropped_after_the_picture_is_removed()
    {
        var (_, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(
            client, _fixture.WorkspaceA, new { title = "Pinned picture", mediaAssetId = assetId, mediaAssetVersionNumber = 1 });

        // Unlinking the picture while saying nothing about its pin is refused.
        var silent = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["mediaAssetId"] = null });
        Assert.Equal(HttpStatusCode.BadRequest, silent.StatusCode);
        Assert.True((await BodyOf(silent)).GetProperty("errors").TryGetProperty("mediaAssetVersionNumber", out _));

        // A version the picture does not have is not a pin that can be made.
        var noSuchVersion = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["mediaAssetVersionNumber"] = 7 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSuchVersion.StatusCode);
        Assert.Equal(1, (await StoredAsync(_fixture.WorkspaceA, created)).MediaAssetVersionNumber);

        // The picture is removed from the library. Dropping the pin links nothing new, so it is still allowed.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now;
            asset.DeletedByMembershipId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var unpinned = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["mediaAssetVersionNumber"] = null });

        Assert.Equal(HttpStatusCode.OK, unpinned.StatusCode);
        var stored = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal(assetId, stored.MediaAssetId);
        Assert.Null(stored.MediaAssetVersionNumber);
    }

    [Fact]
    public async Task The_posts_and_the_piece_of_work_a_card_names_have_to_stay_the_same_works()
    {
        var (work, otherWork, posts) = await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var one = CreativeContextSeeds.NewContext(Now, "One");
            var two = CreativeContextSeeds.NewContext(Now, "Two");
            var package = new SocialPackage
            {
                Id = Guid.NewGuid(), CreativeContextId = one.Id, CreatedByMembershipId = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now,
            };
            db.AddRange(one, two, package);
            await db.SaveChangesAsync(Ct);

            return (one.Id, two.Id, package.Id);
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Matched", creativeContextId = work, socialPackageId = posts });

        // Moving the card to another piece of work while keeping the first one's posts: refused, on the posts.
        var mismatched = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["creativeContextId"] = otherWork });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatched.StatusCode);
        Assert.True((await BodyOf(mismatched)).GetProperty("errors").TryGetProperty("socialPackageId", out _));

        var untouched = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal((work, posts), (untouched.CreativeContextId!.Value, untouched.SocialPackageId!.Value));

        // Said in full — the new work, and the posts unlinked on purpose — it is allowed.
        var moved = await PatchAsync(
            client, _fixture.WorkspaceA, created, new() { ["creativeContextId"] = otherWork, ["socialPackageId"] = null });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var afterMove = await BodyOf(moved);

        // And the first work's posts cannot then be put back beside the second work.
        var putBack = await PatchAsync(client, _fixture.WorkspaceA, afterMove, new() { ["socialPackageId"] = posts });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, putBack.StatusCode);

        // With no piece of work named, the posts stand on their own.
        var alone = await PatchAsync(
            client, _fixture.WorkspaceA, afterMove, new() { ["creativeContextId"] = null, ["socialPackageId"] = posts });
        Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
    }

    [Fact]
    public async Task What_the_server_decides_is_ignored_when_sent()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        var before = await StoredAsync(_fixture.WorkspaceA, created);

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["title"] = "Renamed",
            ["workspaceId"] = _fixture.WorkspaceB.Id,
            ["sortRank"] = -5,
            ["plannedDay"] = "Sunday",
            ["createdByMembershipId"] = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal("Renamed", after.Title);
        Assert.Equal(
            (before.WorkspaceId, before.SortRank, before.PlannedDayOfWeek, before.CreatedByMembershipId),
            (after.WorkspaceId, after.SortRank, after.PlannedDayOfWeek, after.CreatedByMembershipId));
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceB, db => db.ContentCards.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_record_the_card_already_points_at_is_not_re_checked_by_an_unrelated_edit()
    {
        var (contextId, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Linked", creativeContextId = contextId, mediaAssetId = assetId });

        // Both go out of use after the card was linked to them.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.CreativeContexts.SingleAsync(item => item.Id == contextId, Ct)).ArchivedAt = Now;
            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now;
            asset.DeletedByMembershipId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["title"] = "Still editable" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(_fixture.WorkspaceA, created);
        Assert.Equal("Still editable", stored.Title);

        // Not silently unlinked either: the card still says what it pointed at.
        Assert.Equal((contextId, assetId), (stored.CreativeContextId!.Value, stored.MediaAssetId!.Value));
    }

    // ---- Conflict ----

    [Fact]
    public async Task A_token_the_card_does_not_hold_is_a_conflict_and_nothing_is_written()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());

        var response = await PatchAsync(client, _fixture.WorkspaceA, created, new() { ["title"] = "From an old read" }, TokenOfAnotherState);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CardStale, Code(await BodyOf(response)));
        Assert.Equal("Soda bread, Friday", (await StoredAsync(_fixture.WorkspaceA, created)).Title);
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardUpdated));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAA")]
    public async Task A_missing_or_malformed_token_is_a_bad_request_not_a_conflict(string? token)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());
        var id = created.GetProperty("card").GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{id}",
            new Dictionary<string, object?> { ["title"] = "No token", ["expectedConcurrencyToken"] = token },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
        Assert.Equal("Soda bread, Friday", (await StoredAsync(_fixture.WorkspaceA, created)).Title);
    }

    // ---- Who may edit, and across workspaces ----

    [Fact]
    public async Task A_viewer_may_not_edit_and_an_editor_may()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, new { title = "B's card" });

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var refused = await PatchAsync(viewer, _fixture.WorkspaceB, inB, new() { ["title"] = "Not mine to change" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("B's card", (await StoredAsync(_fixture.WorkspaceB, inB)).Title);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, new { title = "A's card" });
        var creator = (await StoredAsync(_fixture.WorkspaceA, inA)).CreatedByMembershipId;

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(editor, _fixture.WorkspaceA, inA, new() { ["title"] = "Edited" })).StatusCode);

        // Who created it does not change; who last edited it is the editor, from their membership.
        var stored = await StoredAsync(_fixture.WorkspaceA, inA);
        Assert.Equal(creator, stored.CreatedByMembershipId);
        Assert.NotEqual(creator, stored.UpdatedByMembershipId);
    }

    [Fact]
    public async Task Another_workspaces_card_and_an_unknown_card_answer_identically_and_nothing_changes()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var cardA = await CreateAsync(inA, _fixture.WorkspaceA, FullCard());
        var id = cardA.GetProperty("card").GetProperty("id").GetGuid();
        var token = cardA.GetProperty("card").GetProperty("concurrencyToken").GetString();
        var body = new Dictionary<string, object?> { ["title"] = "Taken over", ["expectedConcurrencyToken"] = token };

        // B, on its own route, naming A's card with A's real token; and naming a card nobody has.
        var foreign = await inB.PatchAsJsonAsync($"{CardsIn(_fixture.WorkspaceB)}/{id}", body, Ct);
        var unknown = await inB.PatchAsJsonAsync($"{CardsIn(_fixture.WorkspaceB)}/{Guid.NewGuid()}", body, Ct);

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
        Assert.Equal(HttpStatusCode.NotFound, (await inB.PatchAsJsonAsync($"{CardsIn(_fixture.WorkspaceA)}/{id}", body, Ct)).StatusCode);

        Assert.Equal("Soda bread, Friday", (await StoredAsync(_fixture.WorkspaceA, cardA)).Title);
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardUpdated));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, ContentAuditActions.ContentCardUpdated));
    }

    [Theory]
    [InlineData("creativeContextId")]
    [InlineData("recipeId")]
    [InlineData("promptRecordId")]
    [InlineData("mediaAssetId")]
    [InlineData("socialPackageId")]
    public async Task A_patch_cannot_link_another_workspaces_record_and_is_refused_as_for_one_that_does_not_exist(string field)
    {
        var recipeB = await CreateRecipeAsync(_fixture.WorkspaceB, "B's bread");
        var (contextB, promptB, assetB) = await SeedLineageAsync(_fixture.WorkspaceB);
        var postsB = await InWorkspaceAsync(_fixture.WorkspaceB, async db =>
        {
            var posts = new SocialPackage
            {
                Id = Guid.NewGuid(), CreativeContextId = contextB, CreatedByMembershipId = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now,
            };
            db.Add(posts);
            await db.SaveChangesAsync(Ct);

            return posts.Id;
        });

        var theirs = field switch
        {
            "creativeContextId" => contextB,
            "recipeId" => recipeB.RecipeId,
            "promptRecordId" => promptB,
            "mediaAssetId" => assetB,
            _ => postsB,
        };

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var card = await CreateAsync(inA, _fixture.WorkspaceA, new { title = "A's card" });

        var foreign = await PatchAsync(inA, _fixture.WorkspaceA, card, new() { [field] = theirs, ["title"] = "Changed" });
        var unknown = await PatchAsync(inA, _fixture.WorkspaceA, card, new() { [field] = Guid.NewGuid(), ["title"] = "Changed" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var foreignBody = await BodyOf(foreign);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.CardLineageUnprocessable, Code(foreignBody));
        Assert.True(foreignBody.GetProperty("errors").TryGetProperty(field, out _));

        foreach (var name in new[] { "code", "title", "status", "detail", "type", "errors" })
        {
            Assert.Equal(
                foreignBody.TryGetProperty(name, out var left) ? left.ToString() : null,
                unknownBody.TryGetProperty(name, out var right) ? right.ToString() : null);
        }

        // Refused whole: the title sent beside it did not change either.
        var stored = await StoredAsync(_fixture.WorkspaceA, card);
        Assert.Equal("A's card", stored.Title);
        Assert.Null(stored.CreativeContextId);
        Assert.Null(stored.RecipeId);
        Assert.Null(stored.PromptRecordId);
        Assert.Null(stored.MediaAssetId);
        Assert.Null(stored.SocialPackageId);
    }

    [Fact]
    public async Task Each_workspaces_edit_touches_only_its_own_card()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var cardA = await CreateAsync(inA, _fixture.WorkspaceA, new { title = "The same title" });
        var cardB = await CreateAsync(inB, _fixture.WorkspaceB, new { title = "The same title" });

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(inA, _fixture.WorkspaceA, cardA, new() { ["title"] = "A's new title" })).StatusCode);

        Assert.Equal("A's new title", (await StoredAsync(_fixture.WorkspaceA, cardA)).Title);
        Assert.Equal("The same title", (await StoredAsync(_fixture.WorkspaceB, cardB)).Title);
        Assert.Equal(1, await AuditCountAsync(_fixture.WorkspaceA, ContentAuditActions.ContentCardUpdated));
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, ContentAuditActions.ContentCardUpdated));
    }

    // ---- Audit ----

    [Fact]
    public async Task An_edit_writes_an_audit_entry_naming_the_fields_and_none_of_the_creators_words()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await CreateAsync(client, _fixture.WorkspaceA, FullCard());

        await PatchAsync(client, _fixture.WorkspaceA, created, new()
        {
            ["title"] = "A very private new title",
            ["description"] = "Private new words.",
            ["plannedDate"] = null,
        });

        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Action == ContentAuditActions.ContentCardUpdated, Ct));

        Assert.Equal(created.GetProperty("card").GetProperty("id").GetGuid().ToString("D"), entry.ResourceId);
        Assert.Contains("title", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("description", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("plannedDate", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("channelKey", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    private Task<ContentCard> StoredAsync(SeededWorkspace workspace, JsonElement detail)
    {
        var id = detail.GetProperty("card").GetProperty("id").GetGuid();

        return InWorkspaceAsync(workspace, db => db.ContentCards.AsNoTracking().SingleAsync(card => card.Id == id, Ct));
    }

    private Task<int> AuditCountAsync(SeededWorkspace workspace, string action) =>
        InWorkspaceAsync(workspace, db => db.AuditLogs.CountAsync(log => log.Action == action, Ct));

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
