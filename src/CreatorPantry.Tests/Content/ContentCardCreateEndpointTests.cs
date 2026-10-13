using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
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
/// <c>POST .../content-cards</c> through the real Gateway (13.6, KAN-003): the contract, what the server
/// decides for itself, replay, a write that fails part-way, the role bar, and the boundary between two
/// workspaces.
/// </summary>
public sealed class ContentCardCreateEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SaveFault _fault = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real audit writer, behind a switch that makes the one save a create performs fail.
            var audit = services.Single(descriptor => descriptor.ServiceType == typeof(IAuditWriter));
            services.Remove(audit);
            services.Add(new ServiceDescriptor(
                typeof(IAuditWriter),
                provider => new FaultingAuditWriter(
                    audit.ImplementationFactory is { } factory
                        ? (IAuditWriter)factory(provider)
                        : (IAuditWriter)ActivatorUtilities.CreateInstance(provider, audit.ImplementationType!),
                    _fault),
                audit.Lifetime));
        });

        // The cooking vocabulary is platform reference data, shared by both workspaces.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Cuisines.Add(new Cuisine { Id = Guid.NewGuid(), Code = "irish", DisplayName = "Irish", IsActive = true });
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

    private static Task<HttpResponseMessage> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? idempotencyKey = null) =>
        idempotencyKey is null
            ? client.PostAsJsonAsync(CardsIn(workspace), body, Ct)
            : client.PostAsJsonAsync(CardsIn(workspace), body, idempotencyKey, Ct);

    // ---- The contract ----

    [Fact]
    public async Task A_title_is_enough_and_the_server_decides_everything_else()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CreateAsync(client, _fixture.WorkspaceA, new { title = "  Soda bread  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var detail = await BodyOf(response);
        var card = detail.GetProperty("card");
        var id = card.GetProperty("id").GetGuid();

        Assert.EndsWith($"{CardsIn(_fixture.WorkspaceA)}/{id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("Soda bread", card.GetProperty("title").GetString());
        Assert.Equal("ToDo", card.GetProperty("column").GetString());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("plannedDay").ValueKind);
        Assert.Equal(0, card.GetProperty("moveCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(card.GetProperty("concurrencyToken").GetString()));

        // Its creation is its first history entry: it entered its column from nowhere.
        var created = Assert.Single(detail.GetProperty("history").EnumerateArray());
        Assert.Equal(1, created.GetProperty("sequence").GetInt32());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("fromColumn").ValueKind);
        Assert.Equal("ToDo", created.GetProperty("toColumn").GetString());

        // And the detail route agrees with what the create returned.
        var read = await BodyOf(await client.GetAsync(response.Headers.Location!.ToString(), Ct));
        Assert.Equal(id, read.GetProperty("card").GetProperty("id").GetGuid());

        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == id, Ct));
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);
        Assert.NotEqual(Guid.Empty, stored.CreatedByMembershipId);
        Assert.Equal(stored.CreatedByMembershipId, stored.UpdatedByMembershipId);

        var move = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.CardMoveHistory.AsNoTracking().SingleAsync(item => item.ContentCardId == id, Ct));
        Assert.Equal(stored.CreatedByMembershipId, move.ActorMembershipId);
    }

    [Fact]
    public async Task A_full_card_keeps_what_was_sent_derives_its_day_and_names_what_it_points_at()
    {
        var recipe = await CreateRecipeAsync(_fixture.WorkspaceA, "Buttermilk Soda Bread");
        var (contextId, promptId, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            title = "Soda bread, Friday",
            description = "Hero shot and the three posts.",
            column = "InProcess",
            channelKey = "instagram",
            plannedDate = "2026-10-16",
            cuisineCode = "irish",
            dishTypeCode = "bread",
            methodCode = "bake",
            creativeContextId = contextId,
            recipeId = recipe.RecipeId,
            recipeVersionId = recipe.VersionId,
            promptRecordId = promptId,
            mediaAssetId = assetId,
            mediaAssetVersionNumber = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await BodyOf(response);
        var card = detail.GetProperty("card");

        Assert.Equal("InProcess", card.GetProperty("column").GetString());
        Assert.Equal("instagram", card.GetProperty("channelKey").GetString());
        Assert.Equal("2026-10-16", card.GetProperty("plannedDate").GetString());
        Assert.Equal("Friday", card.GetProperty("plannedDay").GetString());
        Assert.Equal(("irish", "bread", "bake"), (
            card.GetProperty("cuisineCode").GetString(), card.GetProperty("dishTypeCode").GetString(), card.GetProperty("methodCode").GetString()));
        Assert.Equal("Hero shot and the three posts.", detail.GetProperty("description").GetString());
        Assert.Equal("InProcess", detail.GetProperty("history")[0].GetProperty("toColumn").GetString());

        var lineage = detail.GetProperty("lineage").EnumerateArray().ToDictionary(entry => entry.GetProperty("kind").GetString()!);
        Assert.Equal(["CreativeContext", "MediaAsset", "PromptRecord", "Recipe"], lineage.Keys.Order());
        Assert.Equal("Buttermilk Soda Bread", lineage["Recipe"].GetProperty("label").GetString());
        Assert.Equal(recipe.VersionId, lineage["Recipe"].GetProperty("versionId").GetGuid());
        Assert.Equal(1, lineage["MediaAsset"].GetProperty("versionNumber").GetInt32());
        Assert.All(lineage.Values, entry => Assert.True(entry.GetProperty("available").GetBoolean()));
    }

    [Fact]
    public async Task What_the_server_decides_cannot_be_sent()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // A weekday that is not the date's, another workspace, a place, an author and a history: none is a
        // field of the request, so none is read.
        var response = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            title = "Trying it on",
            plannedDate = "2026-10-16",
            plannedDay = "Monday",
            plannedDayOfWeek = 1,
            workspaceId = _fixture.WorkspaceB.Id,
            sortRank = -5,
            createdByMembershipId = Guid.NewGuid(),
            moveCount = 12,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var card = (await BodyOf(response)).GetProperty("card");
        var id = card.GetProperty("id").GetGuid();

        Assert.Equal("Friday", card.GetProperty("plannedDay").GetString());
        Assert.Equal(0, card.GetProperty("moveCount").GetInt32());

        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(item => item.Id == id, Ct));
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);
        Assert.Equal(ContentCardPolicy.RankGap, stored.SortRank);
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task A_new_card_goes_to_the_bottom_of_its_own_column()
    {
        // A column that already has a card far down it, and another column that must not be affected.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.AddRange(Seeded("Already there", ContentCardColumn.ToDo, rank: 5000), Seeded("Elsewhere", ContentCardColumn.Approved, rank: 9000));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        foreach (var title in new[] { "First", "Second", "Third" })
        {
            Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, _fixture.WorkspaceA, new { title })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, _fixture.WorkspaceA, new { title = "Review me", column = "ReadyForReview" })).StatusCode);

        var page = await BodyOf(await client.GetAsync(CardsIn(_fixture.WorkspaceA), Ct));
        Assert.Equal(
            ["Already there", "First", "Second", "Third", "Review me", "Elsewhere"],
            page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()));

        var ranks = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().ToDictionaryAsync(card => card.Title, card => card.SortRank, Ct));
        Assert.Equal(5000 + ContentCardPolicy.RankGap, ranks["First"]);
        Assert.Equal(ranks["First"] + ContentCardPolicy.RankGap, ranks["Second"]);

        // An empty column starts one gap in, whatever the other columns hold.
        Assert.Equal(ContentCardPolicy.RankGap, ranks["Review me"]);
    }

    [Theory]
    [InlineData("no title", "title")]
    [InlineData("blank title", "title")]
    [InlineData("long title", "title")]
    [InlineData("long description", "description")]
    [InlineData("version without recipe", "recipeVersionId")]
    [InlineData("asset version without asset", "mediaAssetVersionNumber")]
    [InlineData("asset version zero", "mediaAssetVersionNumber")]
    [InlineData("unknown channel", "channelKey")]
    [InlineData("unknown cuisine", "cuisineCode")]
    [InlineData("unknown dish type", "dishTypeCode")]
    [InlineData("unknown method", "methodCode")]
    public async Task A_card_that_cannot_be_read_is_a_bad_request_naming_the_field_and_nothing_is_written(string fault, string field)
    {
        object body = fault switch
        {
            "no title" => new { description = "No name." },
            "blank title" => new { title = "   " },
            "long title" => new { title = new string('t', ContentCardPolicy.TitleMaxLength + 1) },
            "long description" => new { title = "T", description = new string('d', ContentCardPolicy.DescriptionMaxLength + 1) },
            "version without recipe" => new { title = "T", recipeVersionId = Guid.NewGuid() },
            "asset version without asset" => new { title = "T", mediaAssetVersionNumber = 1 },
            "asset version zero" => new { title = "T", mediaAssetId = Guid.NewGuid(), mediaAssetVersionNumber = 0 },
            "unknown channel" => new { title = "T", channelKey = "myspace" },
            "unknown cuisine" => new { title = "T", cuisineCode = "martian" },
            "unknown dish type" => new { title = "T", dishTypeCode = "irish" },
            _ => new { title = "T", methodCode = "teleport" },
        };

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await CreateAsync(client, _fixture.WorkspaceA, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CardInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        await AssertNothingWrittenAsync(_fixture.WorkspaceA);
    }

    // ---- Lineage and the boundary between workspaces ----

    [Theory]
    [InlineData("creativeContextId")]
    [InlineData("recipeId")]
    [InlineData("recipeVersionId")]
    [InlineData("promptRecordId")]
    [InlineData("mediaAssetId")]
    [InlineData("mediaAssetVersionNumber")]
    public async Task Another_workspaces_record_is_refused_exactly_as_one_that_does_not_exist(string field)
    {
        // B's records, named from A. Each is real and linkable — in B.
        var recipeB = await CreateRecipeAsync(_fixture.WorkspaceB, "B's bread");
        var (contextB, promptB, assetB) = await SeedLineageAsync(_fixture.WorkspaceB);
        var recipeA = await CreateRecipeAsync(_fixture.WorkspaceA, "A's bread");

        object Body(Guid context, Guid recipe, Guid version, Guid prompt, Guid asset) => field switch
        {
            "creativeContextId" => new { title = "T", creativeContextId = context },
            "recipeId" => new { title = "T", recipeId = recipe },

            // A's own recipe, pinned at a version that is not one of its own.
            "recipeVersionId" => new { title = "T", recipeId = recipeA.RecipeId, recipeVersionId = version },
            "promptRecordId" => new { title = "T", promptRecordId = prompt },
            "mediaAssetId" => new { title = "T", mediaAssetId = asset },
            _ => new { title = "T", mediaAssetId = asset, mediaAssetVersionNumber = 1 },
        };

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var foreign = await CreateAsync(inA, _fixture.WorkspaceA, Body(contextB, recipeB.RecipeId, recipeB.VersionId, promptB, assetB));
        var unknown = await CreateAsync(
            inA, _fixture.WorkspaceA, Body(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var foreignBody = await BodyOf(foreign);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.CardLineageUnprocessable, Code(foreignBody));
        Assert.True(foreignBody.GetProperty("errors").TryGetProperty(field, out _));

        // The same answer in every part a client can read, apart from the request's own correlation id.
        foreach (var name in new[] { "code", "title", "status", "detail", "type", "errors" })
        {
            Assert.Equal(
                foreignBody.TryGetProperty(name, out var left) ? left.ToString() : null,
                unknownBody.TryGetProperty(name, out var right) ? right.ToString() : null);
        }

        await AssertNothingWrittenAsync(_fixture.WorkspaceA);
    }

    [Fact]
    public async Task A_record_that_can_no_longer_be_used_is_refused_too()
    {
        var (contextId, _, assetId) = await SeedLineageAsync(_fixture.WorkspaceA);

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.CreativeContexts.SingleAsync(item => item.Id == contextId, Ct)).ArchivedAt = Now;
            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now;
            asset.DeletedByMembershipId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await CreateAsync(client, _fixture.WorkspaceA, new { title = "T", creativeContextId = contextId, mediaAssetId = assetId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await BodyOf(response)).GetProperty("errors");

        // Every field at fault is named in one answer, not the first one found.
        Assert.True(errors.TryGetProperty("creativeContextId", out _));
        Assert.True(errors.TryGetProperty("mediaAssetId", out _));
        await AssertNothingWrittenAsync(_fixture.WorkspaceA);
    }

    [Fact]
    public async Task Lineage_has_to_hold_together_within_the_workspace_too()
    {
        var first = await CreateRecipeAsync(_fixture.WorkspaceA, "First bread");
        var second = await CreateRecipeAsync(_fixture.WorkspaceA, "Second bread");

        // Two pieces of work, and the posts written for the second.
        var (work, otherWork, otherPosts) = await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            var one = CreativeContextSeeds.NewContext(Now, "One");
            var two = CreativeContextSeeds.NewContext(Now, "Two");
            var posts = new SocialPackage
            {
                Id = Guid.NewGuid(),
                CreativeContextId = two.Id,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            db.AddRange(one, two, posts);
            await db.SaveChangesAsync(Ct);

            return (one.Id, two.Id, posts.Id);
        });

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // Both recipes are this workspace's own, but the version is the other recipe's.
        var wrongVersion = await CreateAsync(
            client, _fixture.WorkspaceA, new { title = "T", recipeId = first.RecipeId, recipeVersionId = second.VersionId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongVersion.StatusCode);
        Assert.True((await BodyOf(wrongVersion)).GetProperty("errors").TryGetProperty("recipeVersionId", out _));

        // One piece of work, and the posts written for a different one.
        var wrongPosts = await CreateAsync(
            client, _fixture.WorkspaceA, new { title = "T", creativeContextId = work, socialPackageId = otherPosts });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongPosts.StatusCode);
        Assert.True((await BodyOf(wrongPosts)).GetProperty("errors").TryGetProperty("socialPackageId", out _));

        await AssertNothingWrittenAsync(_fixture.WorkspaceA);

        // The same posts with their own piece of work, or on their own, are fine.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(
            client, _fixture.WorkspaceA, new { title = "Matched", creativeContextId = otherWork, socialPackageId = otherPosts })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(
            client, _fixture.WorkspaceA, new { title = "Posts only", socialPackageId = otherPosts })).StatusCode);
    }

    [Fact]
    public async Task Each_workspace_creates_only_on_its_own_board()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(inA, _fixture.WorkspaceA, new { title = "A's card" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(inB, _fixture.WorkspaceB, new { title = "B's card" })).StatusCode);

        // A member of B, through A's route: an unknown workspace and one they are not in answer alike.
        var trespass = await CreateAsync(inB, _fixture.WorkspaceA, new { title = "Planted" });
        Assert.Equal(HttpStatusCode.NotFound, trespass.StatusCode);

        Assert.Equal(["A's card"], await TitlesAsync(_fixture.WorkspaceA));
        Assert.Equal(["B's card"], await TitlesAsync(_fixture.WorkspaceB));

        // Each board's first card is one gap in: neither column's order knows the other exists.
        Assert.All(
            new[] { _fixture.WorkspaceA, _fixture.WorkspaceB },
            workspace => Assert.Equal(
                ContentCardPolicy.RankGap,
                InWorkspaceAsync(workspace, db => db.ContentCards.AsNoTracking().Select(card => card.SortRank).SingleAsync(Ct)).GetAwaiter().GetResult()));
    }

    // ---- Who may create ----

    [Fact]
    public async Task A_viewer_may_not_create_and_an_editor_may()
    {
        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var refused = await CreateAsync(viewer, _fixture.WorkspaceB, new { title = "Not mine to add" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await AssertNothingWrittenAsync(_fixture.WorkspaceB);

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(editor, _fixture.WorkspaceA, new { title = "An editor's card" })).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_request_is_refused()
    {
        using var anonymous = _fixture.Gateway.CreateClient(CreatorPantry.Tests.Gateway.GatewayTestHost.HttpsClient);

        var response = await anonymous.PostAsJsonAsync(CardsIn(_fixture.WorkspaceA), new { title = "Nobody's" }, Ct);

        Assert.NotEqual(HttpStatusCode.Created, response.StatusCode);
        await AssertNothingWrittenAsync(_fixture.WorkspaceA);
    }

    // ---- Replay ----

    [Fact]
    public async Task Replaying_a_create_returns_the_first_card_and_writes_nothing_more()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var body = new { title = "Soda bread", plannedDate = "2026-10-16" };

        var first = await CreateAsync(client, _fixture.WorkspaceA, body, key);
        var replay = await CreateAsync(client, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await BodyOf(first)).GetProperty("card").GetProperty("id").GetGuid(),
            (await BodyOf(replay)).GetProperty("card").GetProperty("id").GetGuid());

        // One card, one creation row, one audit entry — however many times it was asked for.
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused_and_without_a_key_two_requests_are_two_cards()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, _fixture.WorkspaceA, new { title = "One" }, key)).StatusCode);

        var reused = await CreateAsync(client, _fixture.WorkspaceA, new { title = "Something else" }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(["One"], await TitlesAsync(_fixture.WorkspaceA));

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, _fixture.WorkspaceA, new { title = "Twin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, _fixture.WorkspaceA, new { title = "Twin" })).StatusCode);
        Assert.Equal(["One", "Twin", "Twin"], await TitlesAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_key_does_not_replay_across_workspaces()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var body = new { title = "The same words" };

        var a = await CreateAsync(inA, _fixture.WorkspaceA, body, key);
        var b = await CreateAsync(inB, _fixture.WorkspaceB, body, key);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(
            (await BodyOf(a)).GetProperty("card").GetProperty("id").GetGuid(),
            (await BodyOf(b)).GetProperty("card").GetProperty("id").GetGuid());
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceB));
    }

    // ---- Rollback ----

    [Fact]
    public async Task A_write_that_fails_leaves_no_card_no_history_no_audit_entry_and_a_key_that_still_works()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var body = new { title = "Soda bread" };
        var keysBefore = await IdempotencyRecordsAsync();

        // The audit entry is the last row staged, and it is made unstorable: the card and its creation row are
        // already in the same save, so if any of the three could commit alone this is where it would show.
        _fault.Arm();
        var failed = await CreateAsync(client, _fixture.WorkspaceA, body, key);
        _fault.Disarm();

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(ContentErrorCodes.CardNotSaved, Code(await BodyOf(failed)));
        await AssertNothingWrittenAsync(_fixture.WorkspaceA);
        Assert.Equal(keysBefore, await IdempotencyRecordsAsync());

        // Nothing was recorded against the key either, so the same request sent again simply works.
        var retried = await CreateAsync(client, _fixture.WorkspaceA, body, key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    // ---- Audit ----

    [Fact]
    public async Task Creating_writes_an_audit_entry_that_names_the_card_and_none_of_the_creators_words()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await CreateAsync(
            client, _fixture.WorkspaceA, new { title = "A very private title", description = "Private words.", column = "Approved" });
        var id = (await BodyOf(response)).GetProperty("card").GetProperty("id").GetGuid();

        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Action == ContentAuditActions.ContentCardCreated, Ct));

        Assert.Equal(ContentAuditActions.ContentCardResourceType, entry.ResourceType);
        Assert.Equal(id.ToString("D"), entry.ResourceId);
        Assert.False(string.IsNullOrEmpty(entry.ActorUserId));
        Assert.Contains("Approved", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    private async Task AssertNothingWrittenAsync(SeededWorkspace workspace) =>
        Assert.Equal((0, 0, 0), await WrittenAsync(workspace));

    /// <summary>Cards, history rows and card audit entries in one workspace.</summary>
    private Task<(int Cards, int Moves, int AuditEntries)> WrittenAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db => (
            await db.ContentCards.CountAsync(Ct),
            await db.CardMoveHistory.CountAsync(Ct),
            await db.AuditLogs.CountAsync(log => log.ResourceType == ContentAuditActions.ContentCardResourceType, Ct)));

    private Task<int> CountAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.ContentCards.CountAsync(Ct));

    private Task<List<string>> TitlesAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, db => db.ContentCards.AsNoTracking()
            .OrderBy(card => card.Column).ThenBy(card => card.SortRank).ThenBy(card => card.Title)
            .Select(card => card.Title)
            .ToListAsync(Ct));

    private async Task<int> IdempotencyRecordsAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().IdempotencyRecords.CountAsync(Ct);
    }

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

    /// <summary>A recipe and its first version, created through the real route so the pins are real.</summary>
    private async Task<(Guid RecipeId, Guid VersionId)> CreateRecipeAsync(SeededWorkspace workspace, string title)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("recipeId").GetGuid(), body.GetProperty("versionId").GetGuid());
    }

    /// <summary>A switch a test arms to make the next card create's save fail.</summary>
    private sealed class SaveFault
    {
        private volatile bool _armed;

        public bool Armed => _armed;

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;
    }

    /// <summary>
    /// The real writer, except that while the fault is armed a card's audit entry is staged without its
    /// action — which the store refuses, failing the whole save from inside it.
    /// </summary>
    private sealed class FaultingAuditWriter(IAuditWriter inner, SaveFault fault) : IAuditWriter
    {
        public void Record(AuditEntry entry) =>
            inner.Record(
                fault.Armed && entry.ResourceType == ContentAuditActions.ContentCardResourceType
                    ? entry with { Action = null! }
                    : entry);
    }
}
