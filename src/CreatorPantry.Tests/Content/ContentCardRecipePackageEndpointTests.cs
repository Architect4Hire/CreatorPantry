using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// <c>POST .../content-cards/recipe-packages</c> through the real Gateway (13.7, RCPUB-006): a card pinned to
/// the exact approved versions, what refuses a package, what only blocks it, replay, a write that fails
/// part-way, and the boundary between two workspaces.
/// </summary>
public sealed class ContentCardRecipePackageEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SaveFault _fault = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real audit writer, behind a switch that makes the one save a package performs fail.
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

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CardsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/content-cards";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    /// <summary>Sends a package. A key is generated unless one is given; pass an empty string to send none.</summary>
    private static Task<HttpResponseMessage> PackageAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? key = null) =>
        key == string.Empty
            ? client.PostAsJsonAsync($"{CardsIn(workspace)}/recipe-packages", body, Ct)
            : client.PostAsJsonAsync($"{CardsIn(workspace)}/recipe-packages", body, key ?? Guid.NewGuid().ToString(), Ct);

    /// <summary>The whole package: the recipe version, its editorial, the Instagram post, a picture, a second channel.</summary>
    private static object Whole(Sources sources, bool? allowNeedsReview = null, object? window = null) => new
    {
        recipeId = sources.RecipeId,
        recipeVersionId = sources.VersionId,
        creativeContextId = sources.ContextId,
        editorialRevisionId = sources.EditorialRevisionId,
        channels = new object[]
        {
            new { channelKey = "instagram", socialRevisionId = sources.PostRevisionId },
            new { channelKey = "blog" },
        },
        assets = new[] { new { mediaAssetId = sources.AssetId, versionNumber = 1 } },
        window,
        allowNeedsReview,
    };

    // ---- The contract ----

    [Fact]
    public async Task A_package_is_a_card_pinned_to_the_exact_approved_versions_and_copies_nothing()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var detail = await BodyOf(response);
        var card = detail.GetProperty("card");
        var id = card.GetProperty("id").GetGuid();
        Assert.EndsWith($"{CardsIn(_fixture.WorkspaceA)}/{id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The recipe's own title when none was given, the first column, and a creation row.
        Assert.Equal("Buttermilk Soda Bread", card.GetProperty("title").GetString());
        Assert.Equal("ToDo", card.GetProperty("column").GetString());
        Assert.True(card.GetProperty("hasPackage").GetBoolean());
        Assert.Single(detail.GetProperty("history").EnumerateArray());
        Assert.Equal(sources.RecipeId, card.GetProperty("lineage").GetProperty("recipeId").GetGuid());
        Assert.Equal(sources.VersionId, card.GetProperty("lineage").GetProperty("recipeVersionId").GetGuid());

        var package = detail.GetProperty("package");
        Assert.Equal(sources.VersionId, package.GetProperty("recipeVersionId").GetGuid());
        Assert.Equal(sources.EditorialRevisionId, package.GetProperty("editorialRevisionId").GetGuid());
        Assert.Equal(sources.EditorialProposalId, package.GetProperty("editorialProposalId").GetGuid());
        Assert.Equal(sources.SocialPackageId, package.GetProperty("socialPackageId").GetGuid());
        Assert.Equal(sources.AssetId, package.GetProperty("assets")[0].GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(1, package.GetProperty("assets")[0].GetProperty("versionNumber").GetInt32());

        var channels = package.GetProperty("channels").EnumerateArray().ToList();
        Assert.Equal(["instagram", "blog"], channels.Select(channel => channel.GetProperty("channelKey").GetString()));
        Assert.Equal(sources.PostRevisionId, channels[0].GetProperty("socialRevisionId").GetGuid());

        // A channel named with no post pinned says where the work is going without claiming words exist for it.
        Assert.Equal(JsonValueKind.Null, channels[1].GetProperty("socialRevisionId").ValueKind);

        // Every source still stands.
        Assert.Empty(package.GetProperty("needsReview").EnumerateArray());
        Assert.False(package.GetProperty("blocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, package.GetProperty("needsReviewAllowedAt").ValueKind);

        // Pins, not copies: none of the words of what was pinned are in the package.
        var text = detail.ToString();
        Assert.DoesNotContain("Editorial words", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Post words", text, StringComparison.Ordinal);

        // And the sources themselves were not written to.
        Assert.Equal(sources.Counts, await SourceCountsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_window_is_sent_as_local_times_and_stored_both_ways_in_the_workspaces_zone()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var window = new { localStartsAt = "2026-10-16T09:00:00", localEndsAt = "2026-10-17T23:30:00" };

        // No time zone yet: there is no honest conversion, so the request is refused rather than read as UTC.
        var noZone = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources, window: window));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noZone.StatusCode);
        Assert.True((await BodyOf(noZone)).GetProperty("errors").TryGetProperty("window", out _));
        Assert.Equal(0, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.CountAsync(Ct)));

        var brand = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/brand-profile", new { brandName = "Cozy Fall", timeZoneId = "America/Chicago" }, Ct);
        Assert.Equal(HttpStatusCode.Created, brand.StatusCode);

        var response = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources, window: window));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = (await BodyOf(response)).GetProperty("package").GetProperty("window");
        Assert.Equal("America/Chicago", stored.GetProperty("timeZoneId").GetString());
        Assert.Equal("2026-10-16T09:00:00", stored.GetProperty("localStartsAt").GetString());

        // Chicago is five hours behind UTC in October.
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.Zero), stored.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 4, 30, 0, TimeSpan.Zero), stored.GetProperty("endsAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("no recipe", "recipeId")]
    [InlineData("no version", "recipeVersionId")]
    [InlineData("post without a piece of work", "creativeContextId")]
    [InlineData("channel named twice", "channels[1].channelKey")]
    [InlineData("unknown channel", "channels[0].channelKey")]
    [InlineData("picture with no version", "assets[0].versionNumber")]
    [InlineData("picture pinned twice", "assets[1].mediaAssetId")]
    [InlineData("half a window", "window")]
    [InlineData("window with an offset", "window")]
    [InlineData("window that ends first", "window")]
    public async Task A_package_that_cannot_be_read_is_a_bad_request_naming_the_field_and_nothing_is_created(string fault, string field)
    {
        var s = await SeedSourcesAsync(_fixture.WorkspaceA);

        object body = fault switch
        {
            "no recipe" => new { recipeVersionId = s.VersionId },
            "no version" => new { recipeId = s.RecipeId },
            "post without a piece of work" => new
            {
                recipeId = s.RecipeId, recipeVersionId = s.VersionId,
                channels = new[] { new { channelKey = "instagram", socialRevisionId = s.PostRevisionId } },
            },
            "channel named twice" => new
            {
                recipeId = s.RecipeId, recipeVersionId = s.VersionId,
                channels = new[] { new { channelKey = "blog" }, new { channelKey = "blog" } },
            },
            "unknown channel" => new { recipeId = s.RecipeId, recipeVersionId = s.VersionId, channels = new[] { new { channelKey = "myspace" } } },
            "picture with no version" => new { recipeId = s.RecipeId, recipeVersionId = s.VersionId, assets = new[] { new { mediaAssetId = s.AssetId } } },
            "picture pinned twice" => new
            {
                recipeId = s.RecipeId, recipeVersionId = s.VersionId,
                assets = new[] { new { mediaAssetId = s.AssetId, versionNumber = 1 }, new { mediaAssetId = s.AssetId, versionNumber = 1 } },
            },
            "half a window" => new { recipeId = s.RecipeId, recipeVersionId = s.VersionId, window = new { localStartsAt = "2026-10-16T09:00:00" } },
            "window with an offset" => new
            {
                recipeId = s.RecipeId, recipeVersionId = s.VersionId,
                window = new { localStartsAt = "2026-10-16T09:00:00Z", localEndsAt = "2026-10-17T09:00:00Z" },
            },
            _ => new
            {
                recipeId = s.RecipeId, recipeVersionId = s.VersionId,
                window = new { localStartsAt = "2026-10-17T09:00:00", localEndsAt = "2026-10-16T09:00:00" },
            },
        };

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await PackageAsync(client, _fixture.WorkspaceA, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CardInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);
    }

    // ---- Missing links, and the boundary between workspaces ----

    [Theory]
    [InlineData("recipeId")]
    [InlineData("recipeVersionId")]
    [InlineData("creativeContextId")]
    [InlineData("editorialRevisionId")]
    [InlineData("channels[0].socialRevisionId")]
    [InlineData("assets[0].mediaAssetId")]
    public async Task A_pin_on_another_workspaces_record_is_refused_exactly_as_one_that_does_not_exist(string field)
    {
        var mine = await SeedSourcesAsync(_fixture.WorkspaceA);
        var theirs = await SeedSourcesAsync(_fixture.WorkspaceB);

        // A's own package with one pin swapped for the other id: B's real, approved record, or nobody's.
        object Body(Sources other) => new
        {
            recipeId = field == "recipeId" ? other.RecipeId : mine.RecipeId,
            recipeVersionId = field == "recipeVersionId" ? other.VersionId : mine.VersionId,
            creativeContextId = field == "creativeContextId" ? other.ContextId : mine.ContextId,
            editorialRevisionId = field == "editorialRevisionId" ? other.EditorialRevisionId : mine.EditorialRevisionId,
            channels = new[]
            {
                new { channelKey = "instagram", socialRevisionId = field == "channels[0].socialRevisionId" ? other.PostRevisionId : mine.PostRevisionId },
            },
            assets = new[] { new { mediaAssetId = field == "assets[0].mediaAssetId" ? other.AssetId : mine.AssetId, versionNumber = 1 } },
        };

        var nobodys = new Sources(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), default);

        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var foreign = await PackageAsync(inA, _fixture.WorkspaceA, Body(theirs));
        var unknown = await PackageAsync(inA, _fixture.WorkspaceA, Body(nobodys));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var foreignBody = await BodyOf(foreign);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.CardPackageUnprocessable, Code(foreignBody));
        Assert.True(foreignBody.GetProperty("errors").TryGetProperty(field, out _), foreignBody.ToString());

        // The same answer in every part a client can read, apart from the request's own correlation id.
        foreach (var name in new[] { "code", "title", "status", "detail", "type", "errors" })
        {
            Assert.Equal(
                foreignBody.TryGetProperty(name, out var left) ? left.ToString() : null,
                unknownBody.TryGetProperty(name, out var right) ? right.ToString() : null);
        }

        await AssertNothingCreatedAsync(_fixture.WorkspaceA);
        await AssertNothingCreatedAsync(_fixture.WorkspaceB);
    }

    [Fact]
    public async Task Pins_have_to_belong_together_within_the_workspace_too()
    {
        var first = await SeedSourcesAsync(_fixture.WorkspaceA);
        var second = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // Another recipe's editorial, another piece of work's post, and the right post under the wrong channel.
        var otherEditorial = await PackageAsync(client, _fixture.WorkspaceA, new
        {
            recipeId = first.RecipeId, recipeVersionId = first.VersionId, editorialRevisionId = second.EditorialRevisionId,
        });
        var otherWorksPost = await PackageAsync(client, _fixture.WorkspaceA, new
        {
            recipeId = first.RecipeId, recipeVersionId = first.VersionId, creativeContextId = first.ContextId,
            channels = new[] { new { channelKey = "instagram", socialRevisionId = second.PostRevisionId } },
        });
        var wrongChannel = await PackageAsync(client, _fixture.WorkspaceA, new
        {
            recipeId = first.RecipeId, recipeVersionId = first.VersionId, creativeContextId = first.ContextId,
            channels = new[] { new { channelKey = "blog", socialRevisionId = first.PostRevisionId } },
        });

        Assert.All(new[] { otherEditorial, otherWorksPost, wrongChannel }, response =>
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.True((await BodyOf(otherEditorial)).GetProperty("errors").TryGetProperty("editorialRevisionId", out _));
        Assert.True((await BodyOf(otherWorksPost)).GetProperty("errors").TryGetProperty("channels[0].socialRevisionId", out _));
        Assert.True((await BodyOf(wrongChannel)).GetProperty("errors").TryGetProperty("channels[0].socialRevisionId", out _));
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);
    }

    // ---- Not approved ----

    [Fact]
    public async Task A_version_that_was_never_approved_refuses_the_package()
    {
        // A draft recipe at a draft version, an editorial still only proposed, and a post nobody accepted.
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA, approved: false);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await BodyOf(response)).GetProperty("errors");

        // Every pin at fault is named in one answer.
        Assert.True(errors.TryGetProperty("recipeVersionId", out _));
        Assert.True(errors.TryGetProperty("editorialRevisionId", out _));
        Assert.True(errors.TryGetProperty("channels[0].socialRevisionId", out _));
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);
    }

    // ---- Stale: a source that has moved blocks the package rather than refusing it ----

    [Theory]
    [InlineData("recipe has a newer version", "RecipeVersionSuperseded")]
    [InlineData("editorial needs review", "EditorialNeedsReview")]
    [InlineData("post needs review", "PostNeedsReview")]
    public async Task A_source_that_needs_review_creates_a_blocked_package_naming_why(string moved, string reason)
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        await MoveSourceAsync(_fixture.WorkspaceA, sources, moved);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var package = (await BodyOf(response)).GetProperty("package");
        Assert.True(package.GetProperty("blocked").GetBoolean());
        Assert.Contains(reason, package.GetProperty("needsReview").EnumerateArray().Select(entry => entry.GetProperty("reason").GetString()));

        if (moved == "post needs review")
        {
            Assert.Equal("instagram", package.GetProperty("needsReview").EnumerateArray()
                .Single(entry => entry.GetProperty("reason").GetString() == reason).GetProperty("channelKey").GetString());
        }

        // The pins are exactly what was asked for: a blocked package is not a re-pointed one.
        Assert.Equal(sources.VersionId, package.GetProperty("recipeVersionId").GetGuid());
        Assert.Equal(sources.EditorialRevisionId, package.GetProperty("editorialRevisionId").GetGuid());
    }

    [Fact]
    public async Task A_package_that_was_clear_becomes_blocked_when_a_source_moves_afterwards()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var created = await BodyOf(await PackageAsync(client, _fixture.WorkspaceA, Whole(sources)));
        var id = created.GetProperty("card").GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("package").GetProperty("blocked").GetBoolean());

        await MoveSourceAsync(_fixture.WorkspaceA, sources, "recipe has a newer version");

        // Nothing wrote to the package. It is blocked because the recipe says so now.
        var read = (await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{id}", Ct))).GetProperty("package");
        Assert.True(read.GetProperty("blocked").GetBoolean());
        Assert.Equal("RecipeVersionSuperseded", read.GetProperty("needsReview")[0].GetProperty("reason").GetString());
        Assert.Equal(sources.VersionId, read.GetProperty("recipeVersionId").GetGuid());
    }

    [Fact]
    public async Task An_editor_may_allow_a_package_to_stand_and_that_is_recorded_and_nobody_else_may()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        await MoveSourceAsync(_fixture.WorkspaceA, sources, "editorial needs review");

        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);

        var response = await PackageAsync(editor, _fixture.WorkspaceA, Whole(sources, allowNeedsReview: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await BodyOf(response);
        var package = detail.GetProperty("package");

        // Still says what needs review; no longer blocked; and says when it was allowed.
        Assert.NotEmpty(package.GetProperty("needsReview").EnumerateArray());
        Assert.False(package.GetProperty("blocked").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, package.GetProperty("needsReviewAllowedAt").ValueKind);

        var id = detail.GetProperty("card").GetProperty("id").GetGuid();
        var link = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.RecipeContentPackageLinks.AsNoTracking().SingleAsync(item => item.ContentCardId == id, Ct));
        Assert.NotNull(link.NeedsReviewAllowedByMembershipId);
        Assert.Equal(link.CreatedByMembershipId, link.NeedsReviewAllowedByMembershipId);

        // A Viewer cannot create a package at all, with or without the allowance.
        var theirs = await SeedSourcesAsync(_fixture.WorkspaceB);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await PackageAsync(viewer, _fixture.WorkspaceB, Whole(theirs, allowNeedsReview: true))).StatusCode);
        await AssertNothingCreatedAsync(_fixture.WorkspaceB);
    }

    [Fact]
    public async Task An_allowance_does_not_cover_a_source_that_moves_afterwards()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        await MoveSourceAsync(_fixture.WorkspaceA, sources, "editorial needs review");
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var created = await BodyOf(await PackageAsync(client, _fixture.WorkspaceA, Whole(sources, allowNeedsReview: true)));
        var id = created.GetProperty("card").GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("package").GetProperty("blocked").GetBoolean());

        // What was looked at was the editorial. Now the post needs review too, which nobody looked at.
        await MoveSourceAsync(_fixture.WorkspaceA, sources, "post needs review");

        var read = (await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{id}", Ct))).GetProperty("package");
        Assert.True(read.GetProperty("blocked").GetBoolean());
        Assert.Equal(
            ["EditorialNeedsReview", "PostNeedsReview"],
            read.GetProperty("needsReview").EnumerateArray().Select(entry => entry.GetProperty("reason").GetString()));

        // The allowance is still on record: it was given, and it still covers what it covered.
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("needsReviewAllowedAt").ValueKind);
    }

    [Fact]
    public async Task A_post_written_about_a_different_recipe_cannot_be_pinned()
    {
        var mine = await SeedSourcesAsync(_fixture.WorkspaceA);
        var other = await SeedSourcesAsync(_fixture.WorkspaceA);

        // The other recipe's accepted post, in its own piece of work, on the right channel — offered as the
        // post for this recipe.
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await PackageAsync(client, _fixture.WorkspaceA, new
        {
            recipeId = mine.RecipeId,
            recipeVersionId = mine.VersionId,
            creativeContextId = other.ContextId,
            channels = new[] { new { channelKey = "instagram", socialRevisionId = other.PostRevisionId } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("channels[0].socialRevisionId", out _));
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);
    }

    [Fact]
    public async Task A_window_at_a_local_time_the_clocks_skip_is_refused()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/brand-profile", new { brandName = "Cozy Fall", timeZoneId = "America/Chicago" }, Ct)).StatusCode);

        // 14 March 2027, 02:30 in Chicago: the clocks go from 02:00 straight to 03:00.
        var response = await PackageAsync(client, _fixture.WorkspaceA, Whole(
            sources, window: new { localStartsAt = "2027-03-14T02:30:00", localEndsAt = "2027-03-14T09:00:00" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("window", out _));
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);

        // An hour later exists, and is stored with the offset that applies after the change.
        var later = await PackageAsync(client, _fixture.WorkspaceA, Whole(
            sources, window: new { localStartsAt = "2027-03-14T03:30:00", localEndsAt = "2027-03-14T09:00:00" }));

        Assert.Equal(HttpStatusCode.Created, later.StatusCode);
        Assert.Equal(
            new DateTimeOffset(2027, 3, 14, 8, 30, 0, TimeSpan.Zero),
            (await BodyOf(later)).GetProperty("package").GetProperty("window").GetProperty("startsAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Allowing_a_package_with_nothing_to_allow_records_no_allowance()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var package = (await BodyOf(await PackageAsync(client, _fixture.WorkspaceA, Whole(sources, allowNeedsReview: true)))).GetProperty("package");

        Assert.Empty(package.GetProperty("needsReview").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, package.GetProperty("needsReviewAllowedAt").ValueKind);

        // So it is blocked like any other when a source moves later: an allowance is of what was looked at.
        await MoveSourceAsync(_fixture.WorkspaceA, sources, "recipe has a newer version");
        var id = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.Select(card => card.Id).SingleAsync(Ct));
        var read = (await BodyOf(await client.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{id}", Ct))).GetProperty("package");
        Assert.True(read.GetProperty("blocked").GetBoolean());
    }

    // ---- Replay ----

    [Fact]
    public async Task A_package_needs_an_idempotency_key_and_replaying_one_returns_the_first_card()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var keyless = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources), key: string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, keyless.StatusCode);
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);

        var key = Guid.NewGuid().ToString();
        var first = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources), key);
        var replay = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await BodyOf(first)).GetProperty("card").GetProperty("id").GetGuid(),
            (await BodyOf(replay)).GetProperty("card").GetProperty("id").GetGuid());

        // One card, one creation row, one package with its two channels and one picture, one audit entry.
        Assert.Equal((1, 1, 1, 1, 2, 1), await WrittenAsync(_fixture.WorkspaceA));

        // The same key with a different body is a different request: refused, and nothing more is created.
        var reused = await PackageAsync(client, _fixture.WorkspaceA, new { recipeId = sources.RecipeId, recipeVersionId = sources.VersionId }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal((1, 1, 1, 1, 2, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    // ---- Transaction failure ----

    [Fact]
    public async Task A_write_that_fails_leaves_no_card_no_package_and_no_pins_and_a_key_that_still_works()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var keysBefore = await IdempotencyRecordsAsync();

        // The audit entry is the last row staged and is made unstorable. The card, its creation row, the
        // package and its three pins are already in the same save: if any could commit alone, it shows here.
        _fault.Arm();
        var failed = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources), key);
        _fault.Disarm();

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(ContentErrorCodes.CardNotSaved, Code(await BodyOf(failed)));
        await AssertNothingCreatedAsync(_fixture.WorkspaceA);
        Assert.Equal(keysBefore, await IdempotencyRecordsAsync());
        Assert.Equal(sources.Counts, await SourceCountsAsync(_fixture.WorkspaceA));

        var retried = await PackageAsync(client, _fixture.WorkspaceA, Whole(sources), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((1, 1, 1, 1, 2, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Each_workspace_packages_only_its_own_work_and_a_key_does_not_cross()
    {
        var mine = await SeedSourcesAsync(_fixture.WorkspaceA);
        var theirs = await SeedSourcesAsync(_fixture.WorkspaceB);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        var a = await PackageAsync(inA, _fixture.WorkspaceA, Whole(mine), key);
        var b = await PackageAsync(inB, _fixture.WorkspaceB, Whole(theirs), key);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotent-Replayed"));

        var cardA = (await BodyOf(a)).GetProperty("card").GetProperty("id").GetGuid();
        var packageB = (await BodyOf(b)).GetProperty("package");
        Assert.Equal(theirs.VersionId, packageB.GetProperty("recipeVersionId").GetGuid());

        Assert.Equal((1, 1, 1, 1, 2, 1), await WrittenAsync(_fixture.WorkspaceA));
        Assert.Equal((1, 1, 1, 1, 2, 1), await WrittenAsync(_fixture.WorkspaceB));

        // B cannot read A's packaged card, and A's whole package named from B's route is a workspace B is not in.
        Assert.Equal(HttpStatusCode.NotFound, (await inB.GetAsync($"{CardsIn(_fixture.WorkspaceB)}/{cardA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PackageAsync(inB, _fixture.WorkspaceA, Whole(mine))).StatusCode);

        // A source moving in A blocks A's package and says nothing about B's.
        await MoveSourceAsync(_fixture.WorkspaceA, mine, "recipe has a newer version");
        var cardB = await InWorkspaceAsync(_fixture.WorkspaceB, db => db.ContentCards.Select(card => card.Id).SingleAsync(Ct));

        Assert.True((await BodyOf(await inA.GetAsync($"{CardsIn(_fixture.WorkspaceA)}/{cardA}", Ct))).GetProperty("package").GetProperty("blocked").GetBoolean());
        Assert.False((await BodyOf(await inB.GetAsync($"{CardsIn(_fixture.WorkspaceB)}/{cardB}", Ct))).GetProperty("package").GetProperty("blocked").GetBoolean());
    }

    // ---- Audit ----

    [Fact]
    public async Task A_package_leaves_an_audit_entry_that_counts_what_was_pinned_and_quotes_nothing()
    {
        var sources = await SeedSourcesAsync(_fixture.WorkspaceA);
        await MoveSourceAsync(_fixture.WorkspaceA, sources, "post needs review");
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var detail = await BodyOf(await PackageAsync(client, _fixture.WorkspaceA, Whole(sources)));

        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Action == ContentAuditActions.ContentCardPackaged, Ct));

        Assert.Equal(detail.GetProperty("card").GetProperty("id").GetGuid().ToString("D"), entry.ResourceId);
        Assert.Equal("package:blocked", entry.AfterReference);
        Assert.Contains("1 picture(s), 2 channel(s)", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Soda Bread", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    /// <summary>Everything one package pins, and how many of each source row the workspace holds.</summary>
    private sealed record Sources(
        Guid RecipeId,
        Guid VersionId,
        Guid ContextId,
        Guid EditorialProposalId,
        Guid EditorialRevisionId,
        Guid SocialPackageId,
        Guid ChannelId,
        Guid PostRevisionId,
        Guid AssetId,
        (int Recipes, int Versions, int Revisions, int Posts) Counts);

    /// <summary>
    /// One recipe with one version, its editorial, one piece of work with an Instagram post, and one picture —
    /// all approved, or none of them.
    /// </summary>
    private async Task<Sources> SeedSourcesAsync(SeededWorkspace workspace, bool approved = true)
    {
        var sources = await InWorkspaceAsync(workspace, async db =>
        {
            var author = Guid.NewGuid();
            var recipe = new Recipe
            {
                Id = Guid.NewGuid(), Title = "Buttermilk Soda Bread", Status = approved ? RecipeStatus.Approved : RecipeStatus.Draft,
                CreatedByMembershipId = author, UpdatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now,
            };
            var version = NewVersion(recipe, 1, approved ? RecipeVersionReadiness.Ready : RecipeVersionReadiness.Draft, author);
            var context = CreativeContextSeeds.NewContext(Now);
            db.AddRange(recipe, version, context);
            await db.SaveChangesAsync(Ct);

            var proposal = new ContentProposal
            {
                Id = Guid.NewGuid(), RecipeId = recipe.Id, Kind = ContentPackageKind.Editorial, Status = ContentProposalStatus.Proposed,
                CreatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now,
            };
            var editorial = new ContentRevision
            {
                Id = Guid.NewGuid(), ContentProposalId = proposal.Id, RecipeId = recipe.Id, RevisionNumber = 1,
                Source = ContentRevisionSource.CreatorEdit, RecipeVersionId = version.Id, SchemaVersion = 1,
                Content = "{\"introduction\":\"Editorial words.\"}", CreatedByMembershipId = author, CreatedAt = Now,
            };
            var social = new SocialPackage { Id = Guid.NewGuid(), CreativeContextId = context.Id, CreatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now };
            var channel = new SocialPackageChannel
            {
                Id = Guid.NewGuid(), SocialPackageId = social.Id, ChannelKey = "instagram", Status = ContentProposalStatus.Proposed, CreatedAt = Now, UpdatedAt = Now,
            };
            var post = new SocialRevision
            {
                Id = Guid.NewGuid(), SocialPackageChannelId = channel.Id, RevisionNumber = 1, Source = ContentRevisionSource.CreatorEdit,
                Body = "Post words.", RecipeId = recipe.Id, RecipeVersionId = version.Id, CreatedByMembershipId = author, CreatedAt = Now,
            };
            db.AddRange(proposal, editorial, social, channel, post);
            await db.SaveChangesAsync(Ct);

            if (approved)
            {
                proposal.Status = ContentProposalStatus.Accepted;
                proposal.AcceptedRevisionId = editorial.Id;
                channel.Status = ContentProposalStatus.Accepted;
                channel.AcceptedRevisionId = post.Id;
                channel.AcceptedAt = Now;
                await db.SaveChangesAsync(Ct);
            }

            var assetId = await CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now, Ct);

            return new Sources(recipe.Id, version.Id, context.Id, proposal.Id, editorial.Id, social.Id, channel.Id, post.Id, assetId, default);
        });

        return sources with { Counts = await SourceCountsAsync(workspace) };
    }

    private static RecipeVersion NewVersion(Recipe recipe, int number, RecipeVersionReadiness readiness, Guid author)
    {
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(), RecipeId = recipe.Id, VersionNumber = number, Source = RecipeVersionSource.CreatorEdit,
            Readiness = readiness, CreatedByMembershipId = author, CreatedAt = Now.AddMinutes(number),
            SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        };
        version.Snapshot = new RecipeVersionSnapshot
        {
            RecipeVersionId = version.Id,
            Document = RecipeSnapshotSerializer.Serialize(new RecipeSnapshotDocument
            {
                SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
                Recipe = new RecipeSnapshotHeader { Title = recipe.Title, Status = recipe.Status },
                IngredientGroups = [],
                InstructionGroups = [],
            }),
        };

        return version;
    }

    /// <summary>Moves one approved source on, the way its own module would.</summary>
    private Task MoveSourceAsync(SeededWorkspace workspace, Sources sources, string moved) =>
        InWorkspaceAsync(workspace, async db =>
        {
            switch (moved)
            {
                case "recipe has a newer version":
                    var recipe = await db.Recipes.SingleAsync(item => item.Id == sources.RecipeId, Ct);
                    db.Add(NewVersion(recipe, 2, RecipeVersionReadiness.Ready, Guid.NewGuid()));
                    break;

                case "editorial needs review":
                    var proposal = await db.ContentProposals.SingleAsync(item => item.Id == sources.EditorialProposalId, Ct);
                    proposal.Status = ContentProposalStatus.NeedsReview;
                    proposal.StaleSince = Now;
                    proposal.StaleReasons = ContentStaleReasons.BrandChanged;
                    break;

                default:
                    var channel = await db.SocialPackageChannels.SingleAsync(item => item.Id == sources.ChannelId, Ct);
                    channel.Status = ContentProposalStatus.NeedsReview;
                    channel.StaleSince = Now;
                    channel.StaleReasons = ContentStaleReasons.BrandChanged;
                    break;
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private Task<(int Recipes, int Versions, int Revisions, int Posts)> SourceCountsAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db => (
            await db.Recipes.CountAsync(Ct),
            await db.RecipeVersions.CountAsync(Ct),
            await db.ContentRevisions.CountAsync(Ct),
            await db.SocialRevisions.CountAsync(Ct)));

    private async Task AssertNothingCreatedAsync(SeededWorkspace workspace) =>
        Assert.Equal((0, 0, 0, 0, 0, 0), await WrittenAsync(workspace));

    /// <summary>Cards, history rows, packages, picture pins, channel pins and package audit entries in one workspace.</summary>
    private Task<(int Cards, int Moves, int Packages, int Assets, int Channels, int AuditEntries)> WrittenAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db => (
            await db.ContentCards.CountAsync(Ct),
            await db.CardMoveHistory.CountAsync(Ct),
            await db.RecipeContentPackageLinks.CountAsync(Ct),
            await db.RecipeContentPackageLinkAssets.CountAsync(Ct),
            await db.RecipeContentPackageLinkChannels.CountAsync(Ct),
            await db.AuditLogs.CountAsync(log => log.ResourceType == ContentAuditActions.ContentCardResourceType, Ct)));

    private async Task<int> IdempotencyRecordsAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().IdempotencyRecords.CountAsync(Ct);
    }

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>A switch a test arms to make the next package's save fail.</summary>
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
