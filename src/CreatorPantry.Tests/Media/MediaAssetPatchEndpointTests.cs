using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>PATCH /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}</c> (DAM-004) through the real Gateway:
/// what a partial edit changes, what a <c>null</c> clears, what a stale token refuses, and what the other
/// workspace cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// The interesting assertions are the ones about fields nobody mentioned. A hand-rolled patch's characteristic
/// failure is blanking a field the client never sent, so almost every test here checks what was <em>not</em>
/// submitted as well as what was.
/// </para>
/// <para>
/// Over the gateway's SQLite host. <c>RowVersion</c> is the one thing SQLite cannot give honestly — it has no
/// <c>rowversion</c> column — so the token's physical half is proved against SQL Server by
/// <see cref="MediaAssetPatchSqlServerTests"/> and what is proved here is the contract around it.
/// </para>
/// </remarks>
public sealed class MediaAssetPatchEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AssetIn(SeededWorkspace workspace, Guid assetId) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}";

    // ---- Partial edits ----

    /// <summary>
    /// One field submitted, and the eight the body never mentioned all survive. This is the test a patch that
    /// deserializes into a plain nullable record fails: every unmentioned field arrives as null and gets written.
    /// </summary>
    [Fact]
    public async Task A_patch_changes_only_what_it_submits()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" });

        Assert.Equal("Renamed", body.GetProperty("title").GetString());
        Assert.Equal("Overhead on linen.", body.GetProperty("description").GetString());
        Assert.Equal("A round loaf.", body.GetProperty("altText").GetString());
        Assert.Equal("instagram", body.GetProperty("channelKey").GetString());
        Assert.Equal("reels", body.GetProperty("platformKey").GetString());
        Assert.Equal("Wednesday", body.GetProperty("day").GetString());
        Assert.Equal("overhead-linen", body.GetProperty("styleKey").GetString());
        Assert.Equal("Sam Okafor", body.GetProperty("rightsHolder").GetString());
        Assert.Equal("Photo: Sam Okafor", body.GetProperty("attributionText").GetString());
    }

    [Fact]
    public async Task Every_editorial_field_can_be_set()
    {
        var (id, token) = await SeededAsync();
        var cuisine = await SeedCuisineAsync("thai", "Thai");
        var course = await SeedCourseAsync("main", "Main");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new
        {
            expectedConcurrencyToken = token,
            title = "Renamed",
            description = "On marble.",
            altText = "A sliced loaf.",
            channelKey = "blog",
            platformKey = "web",
            day = "Sunday",
            styleKey = "closeup",
            cuisineId = cuisine,
            courseId = course,
            rightsHolder = "Ada Rao",
            attributionText = "Photo: Ada Rao",
        });

        Assert.Equal("Renamed", body.GetProperty("title").GetString());
        Assert.Equal("On marble.", body.GetProperty("description").GetString());
        Assert.Equal("A sliced loaf.", body.GetProperty("altText").GetString());
        Assert.Equal("blog", body.GetProperty("channelKey").GetString());
        Assert.Equal("web", body.GetProperty("platformKey").GetString());
        Assert.Equal("Sunday", body.GetProperty("day").GetString());
        Assert.Equal("closeup", body.GetProperty("styleKey").GetString());
        Assert.Equal(cuisine, body.GetProperty("cuisineId").GetGuid());
        Assert.Equal(course, body.GetProperty("courseId").GetGuid());
        Assert.Equal("Ada Rao", body.GetProperty("rightsHolder").GetString());
        Assert.Equal("Photo: Ada Rao", body.GetProperty("attributionText").GetString());
    }

    /// <summary>
    /// The response is the whole detail shape, so an editor rebinds from one response rather than following every
    /// save with a read.
    /// </summary>
    /// <remarks>
    /// That the token it carries is a <em>new</em> one is not asserted here and cannot be: SQLite gives
    /// <c>RowVersion</c> a <c>randomblob(8)</c> default on insert and never bumps it on update, so every token
    /// looks unchanged whatever the server did. <see cref="MediaAssetPatchSqlServerTests"/> proves the refresh
    /// against the engine that generates it.
    /// </remarks>
    [Fact]
    public async Task A_patch_returns_the_whole_asset()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" });

        // The detail shape, not a reduced one: versions, counts and lineage all travel with it.
        Assert.Equal("Renamed", body.GetProperty("title").GetString());
        Assert.Equal(1, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal(1, body.GetProperty("versionCount").GetInt32());
        Assert.Equal(0, body.GetProperty("utilizationCount").GetInt32());
        Assert.Empty(body.GetProperty("recipeLinks").EnumerateArray());
        Assert.Empty(body.GetProperty("prompts").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("concurrencyToken").GetString()));
    }

    [Fact]
    public async Task An_edit_records_who_made_it_and_when()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await PatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" });

        var asset = await ReloadAsync(id);

        Assert.Equal("Renamed", asset.Title);
        Assert.NotEqual(Now, asset.UpdatedAt);
        Assert.NotEqual(asset.CreatedByMembershipId, asset.UpdatedByMembershipId);

        // Authorship and creation are not editable, so neither moved.
        Assert.Equal(Now, asset.CreatedAt);
    }

    // ---- Clearing ----

    /// <summary>
    /// An explicit <c>null</c> clears, and is distinguishable from absence — which is the whole reason
    /// <c>PatchField</c> exists. The same request sent twice, once with nulls and once omitting those keys, must
    /// do two different things.
    /// </summary>
    [Fact]
    public async Task A_null_clears_a_field_where_omitting_it_would_not()
    {
        var (cleared, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var afterNull = await PatchAsync(client, cleared, new
        {
            expectedConcurrencyToken = token,
            description = (string?)null,
            altText = (string?)null,
            channelKey = (string?)null,
            day = (DayOfWeek?)null,
            rightsHolder = (string?)null,
        });

        Assert.Equal(JsonValueKind.Null, afterNull.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterNull.GetProperty("altText").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterNull.GetProperty("channelKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterNull.GetProperty("day").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterNull.GetProperty("rightsHolder").ValueKind);

        // The same fields left out of a second asset's patch, which must survive untouched.
        var (kept, keptToken) = await SeededAsync();
        var afterOmit = await PatchAsync(client, kept, new { expectedConcurrencyToken = keptToken });

        Assert.Equal("Overhead on linen.", afterOmit.GetProperty("description").GetString());
        Assert.Equal("A round loaf.", afterOmit.GetProperty("altText").GetString());
        Assert.Equal("instagram", afterOmit.GetProperty("channelKey").GetString());
        Assert.Equal("Wednesday", afterOmit.GetProperty("day").GetString());
        Assert.Equal("Sam Okafor", afterOmit.GetProperty("rightsHolder").GetString());
    }

    /// <summary>
    /// A title is required, so clearing it is refused by the same check a creation runs — which is the point of
    /// validating the merged result rather than the patch.
    /// </summary>
    [Fact]
    public async Task Clearing_the_title_is_refused_naming_the_field()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = token, title = (string?)null },
            Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MediaErrorCodes.AssetInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("title", out _));

        // And nothing was written, so the token the caller holds is still the asset's.
        Assert.Equal("Soda bread hero", (await ReloadAsync(id)).Title);
    }

    // ---- Tags ----

    [Fact]
    public async Task A_submitted_tag_list_replaces_the_whole_set()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var freezer = await SeedTagAsync(_fixture.WorkspaceA, "Freezer");
        var spring = await SeedTagAsync(_fixture.WorkspaceA, "Spring");
        var (id, token) = await SeededAsync(tagIds: [weeknight, freezer]);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new
        {
            expectedConcurrencyToken = token,
            tags = new[] { freezer, spring },
        });

        // Replaced, not merged: weeknight is gone although the patch never named it.
        Assert.Equal(
            ["Freezer", "Spring"],
            body.GetProperty("tags").EnumerateArray()
                .Select(tag => tag.GetProperty("name").GetString()).Order());
    }

    [Fact]
    public async Task An_empty_list_and_an_explicit_null_both_clear_every_tag()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var (byEmpty, emptyToken) = await SeededAsync(tagIds: [weeknight]);
        var afterEmpty = await PatchAsync(
            client, byEmpty, new { expectedConcurrencyToken = emptyToken, tags = Array.Empty<Guid>() });

        var (byNull, nullToken) = await SeededAsync(tagIds: [weeknight]);
        var afterNull = await PatchAsync(
            client, byNull, new { expectedConcurrencyToken = nullToken, tags = (Guid[]?)null });

        Assert.Empty(afterEmpty.GetProperty("tags").EnumerateArray());
        Assert.Empty(afterNull.GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task Omitting_tags_leaves_them_alone()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var (id, token) = await SeededAsync(tagIds: [weeknight]);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" });

        Assert.Equal(
            "Weeknight",
            body.GetProperty("tags").EnumerateArray().Single().GetProperty("name").GetString());
    }

    /// <summary>
    /// A tag id this workspace does not own is refused, never created. Both workspaces have a "Weeknight", so the
    /// refusal is on ownership rather than on the name.
    /// </summary>
    [Fact]
    public async Task A_tag_from_the_other_workspace_is_refused()
    {
        await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var theirs = await SeedTagAsync(_fixture.WorkspaceB, "Weeknight");
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = token, tags = new[] { theirs } },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("workspaceTagIds", out _));
    }

    /// <summary>
    /// A cuisine or course id that names nothing is refused by name, not by a foreign-key violation.
    /// </summary>
    /// <remarks>
    /// Both are client-supplied ids into shared reference vocabulary with <c>Restrict</c> foreign keys, so without
    /// an existence check the patch reaches <c>SaveChanges</c> and throws <c>DbUpdateException</c> — which is not
    /// the <c>DbUpdateConcurrencyException</c> the write path catches, so it surfaces as a 500. A creator typing a
    /// stale id deserves a named field.
    /// </remarks>
    [Fact]
    public async Task An_unknown_cuisine_or_course_is_refused_by_name()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var field in (string[])["cuisineId", "courseId"])
        {
            var (id, token) = await SeededAsync();
            var body = new Dictionary<string, object?>
            {
                ["expectedConcurrencyToken"] = token,
                [field] = Guid.NewGuid(),
            };

            var response = await client.PatchAsJsonAsync(AssetIn(_fixture.WorkspaceA, id), body, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
            Assert.Equal("Soda bread hero", (await ReloadAsync(id)).Title);
        }
    }

    // ---- Concurrency ----

    /// <summary>
    /// A token this API could never have issued is a bad request naming the field, not a 409: a conflict says
    /// "somebody saved first", which would be a lie about a token that never existed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public async Task A_malformed_token_is_a_bad_request_rather_than_a_conflict(string? token)
    {
        var (id, _) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = token, title = "Renamed" },
            Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    /// <summary>
    /// An edit that asks for what is already there writes nothing: the token it quoted is still valid afterwards,
    /// which it would not be if the row had been touched.
    /// </summary>
    [Fact]
    public async Task An_edit_that_changes_nothing_writes_nothing()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var (id, token) = await SeededAsync(tagIds: [weeknight]);
        var before = await ReloadAsync(id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new
        {
            expectedConcurrencyToken = token,
            title = "Soda bread hero",
            description = "Overhead on linen.",

            // The same set, in the other order: tags compare as sets, so re-sending the whole panel is not a change.
            tags = new[] { weeknight },
        });

        // UpdatedAt and the actor are what prove nothing was written. The token coming back unchanged is not
        // evidence here — SQLite never bumps it — which is why the same guarantee is asserted again in
        // MediaAssetPatchSqlServerTests, where an unchanged token means the row was genuinely untouched.
        var after = await ReloadAsync(id);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.UpdatedByMembershipId, after.UpdatedByMembershipId);
        Assert.Equal(before.Title, after.Title);
    }

    /// <summary>
    /// A retry of the same edit under one idempotency key is replayed rather than being told somebody saved
    /// first — where the somebody was itself. Without this a dropped connection after a successful commit leaves a
    /// client unable to tell a conflict from its own success.
    /// </summary>
    [Fact]
    public async Task A_retry_under_one_idempotency_key_is_replayed_not_refused()
    {
        var (id, token) = await SeededAsync();
        var key = Guid.NewGuid().ToString();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await SendPatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" }, key);
        var second = await SendPatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" }, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(
            (await ReadAsync(first)).GetProperty("concurrencyToken").GetString(),
            (await ReadAsync(second)).GetProperty("concurrencyToken").GetString());
    }

    /// <summary>
    /// A well-formed token that is not this asset's is refused with the published 409 and its code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is testable on SQLite after all, and the first pass wrongly assumed it was not.</strong> The
    /// two-editor case genuinely is not — SQLite never moves a token, so an edit followed by a second edit with the
    /// same token still matches. But a token that was never this row's mismatches deterministically, because
    /// <c>SqliteModelCustomizer</c> seeds every row with <c>randomblob(8)</c>: eight zero bytes are not that.
    /// </para>
    /// <para>
    /// So the HTTP contract around a stale token — which status, which code — is proved here where it belongs, and
    /// <see cref="MediaAssetPatchSqlServerTests"/> proves the thing only a real <c>rowversion</c> can: that an
    /// edit composed against an earlier read of the <em>same</em> asset is refused.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_token_that_is_not_this_assets_is_refused_as_a_conflict()
    {
        var (id, _) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new
            {
                expectedConcurrencyToken = Convert.ToBase64String(new byte[MediaConcurrencyToken.ByteLength]),
                title = "Overwritten",
            },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetStaleToken,
            (await ReadAsync(response)).GetProperty("code").GetString());

        // Nothing was written, which is what separates a refusal from a failed write.
        Assert.Equal("Soda bread hero", (await ReloadAsync(id)).Title);
    }

    /// <summary>
    /// Another asset's token cannot be spent on this one. Rowversions are database-global, so this is the closest a
    /// caller can get to a token that is real but not theirs.
    /// </summary>
    [Fact]
    public async Task Another_assets_token_cannot_be_used()
    {
        var (mine, _) = await SeededAsync();
        var (other, otherToken) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, mine),
            new { expectedConcurrencyToken = otherToken, title = "Overwritten" },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Soda bread hero", (await ReloadAsync(mine)).Title);
        Assert.Equal("Soda bread hero", (await ReloadAsync(other)).Title);
    }

    /// <summary>
    /// One key, two different edits: the second is not replayed as the first. Without the patch body in the
    /// fingerprint it would be — the executor would match on asset and token alone, hand back the first edit's
    /// response with <c>Idempotent-Replayed: true</c>, and the second edit would vanish with the client told it
    /// succeeded.
    /// </summary>
    [Fact]
    public async Task One_key_with_a_different_edit_is_not_replayed_as_the_first()
    {
        var (id, token) = await SeededAsync();
        var key = Guid.NewGuid().ToString();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await SendPatchAsync(client, id, new { expectedConcurrencyToken = token, title = "First" }, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await SendPatchAsync(
            client, id, new { expectedConcurrencyToken = token, title = "Different" }, key);

        // Refused as a reused key rather than silently served the first answer.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("First", (await ReloadAsync(id)).Title);
    }

    // ---- What a patch cannot reach ----

    /// <summary>
    /// The fields that are not on the view model at all. Sent anyway, they are ignored rather than applied —
    /// unrepresentable beats validated, because there is no rule anybody can forget to write.
    /// </summary>
    [Fact]
    public async Task Nothing_about_the_bytes_or_the_lineage_can_be_patched()
    {
        var (id, token) = await SeededAsync();
        var before = await ReloadAsync(id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await PatchAsync(client, id, new
        {
            expectedConcurrencyToken = token,
            title = "Renamed",

            // Every one of these is absent from the contract.
            kind = "AiGenerated",
            currentVersionNumber = 99,
            objectKey = "assets/elsewhere.jpg",
            mediaType = "image/png",
            width = 1,
            height = 1,
            sizeBytes = 1,
            contentChecksum = "sha256:0",
            workspaceId = _fixture.WorkspaceB.Id,
            deletedAt = Now,
            createdAt = Now.AddYears(-1),
            createdByMembershipId = Guid.NewGuid(),
        });

        var after = await ReloadAsync(id);

        Assert.Equal("Renamed", after.Title);
        Assert.Equal(MediaAssetKind.Original, after.Kind);
        Assert.Equal(before.CurrentVersionNumber, after.CurrentVersionNumber);
        Assert.Equal(before.WorkspaceId, after.WorkspaceId);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.CreatedByMembershipId, after.CreatedByMembershipId);
        Assert.Null(after.DeletedAt);

        // The published kind did not move either, so nothing downstream read the ignored value.
        Assert.Equal("Original", body.GetProperty("kind").GetString());
    }

    /// <summary>
    /// The version's own row is untouched: a patch changes what was said about the bytes, never the bytes or the
    /// record of them. The object key in particular is the thing a patch must never be able to repoint.
    /// </summary>
    [Fact]
    public async Task A_patch_leaves_the_version_row_exactly_as_it_was()
    {
        var (id, token) = await SeededAsync();
        var before = await ReloadVersionAsync(id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await PatchAsync(client, id, new { expectedConcurrencyToken = token, title = "Renamed" });

        var after = await ReloadVersionAsync(id);

        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ObjectKey, after.ObjectKey);
        Assert.Equal(before.ContentChecksum, after.ContentChecksum);
        Assert.Equal(before.MediaType, after.MediaType);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    // ---- Not found and isolation ----

    [Fact]
    public async Task An_unknown_asset_is_a_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, Guid.NewGuid()),
            new { expectedConcurrencyToken = Convert.ToBase64String(new byte[8]), title = "Renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A patch is not a way to edit or restore something deleted, and the answer is the same 404 an unknown id
    /// gets — so it cannot be used to probe which.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_a_not_found()
    {
        var (id, token) = await SeededAsync(deletedAt: Now);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = token, title = "Renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal("Soda bread hero", (await ReloadAsync(id)).Title);
    }

    /// <summary>
    /// A's asset patched from B's route, by B's owner: 404, the same answer an unknown id gets, and A's asset
    /// unchanged. The two-workspace coverage tenancy.md requires of a write path.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_patch_the_others_asset()
    {
        var (inA, token) = await SeededAsync();

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var route in (string[])[AssetIn(_fixture.WorkspaceB, inA), AssetIn(_fixture.WorkspaceA, inA)])
        {
            var response = await ownerB.PatchAsJsonAsync(
                route, new { expectedConcurrencyToken = token, title = "B was here" }, Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal("Soda bread hero", (await ReloadAsync(inA)).Title);
    }

    /// <summary>
    /// A viewer may read the library but not change it. Checked at the facade as well as the route, so the policy
    /// is not the only thing standing between a viewer and a write.
    /// </summary>
    [Fact]
    public async Task A_viewer_cannot_patch()
    {
        var (id, token) = await SeededAsync();
        await DemoteMemberAsync(_fixture.WorkspaceA, WorkspaceRole.Viewer);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await member.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = token, title = "Renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Soda bread hero", (await ReloadAsync(id)).Title);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail, cancellationToken: Ct);

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    private Task<HttpResponseMessage> SendPatchAsync(
        GatewayClient client, Guid assetId, object body, string idempotencyKey) =>
        client.SendAsync(
            HttpMethod.Patch,
            AssetIn(_fixture.WorkspaceA, assetId),
            body,
            new Dictionary<string, string> { [IdempotencyPolicy.KeyHeader] = idempotencyKey },
            Ct);

    /// <summary>Patches and insists it succeeded, so a test about what changed cannot pass on a refusal.</summary>
    private async Task<JsonElement> PatchAsync(GatewayClient client, Guid assetId, object body)
    {
        var response = await client.PatchAsJsonAsync(AssetIn(_fixture.WorkspaceA, assetId), body, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadAsync(response);
    }

    /// <summary>
    /// An asset with every editable field filled, and its current concurrency token.
    /// </summary>
    /// <remarks>
    /// Every field is populated deliberately: a patch's characteristic failure is blanking what it did not
    /// mention, and that is invisible against an asset whose fields were null to begin with.
    /// </remarks>
    private async Task<(Guid Id, string Token)> SeededAsync(
        IReadOnlyList<Guid>? tagIds = null, DateTimeOffset? deletedAt = null)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread hero",
            Description = "Overhead on linen.",
            AltText = "A round loaf.",
            Kind = MediaAssetKind.Original,
            ChannelKey = "instagram",
            PlatformKey = "reels",
            Day = DayOfWeek.Wednesday,
            StyleKey = "overhead-linen",
            RightsHolder = "Sam Okafor",
            AttributionText = "Photo: Sam Okafor",
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        asset.Versions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            MediaAssetId = asset.Id,
            VersionNumber = 1,
            MediaType = "image/jpeg",
            SizeBytes = 204_800,
            Width = 1600,
            Height = 1200,
            ContentChecksum = $"sha256:{Convert.ToHexString(Guid.NewGuid().ToByteArray())}",
            ObjectKey = $"assets/{asset.Id:D}/1.jpg",
            OriginalFileName = "soda-bread.jpg",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = Now,
        });

        foreach (var tagId in tagIds ?? [])
        {
            asset.Tags.Add(new MediaAssetTag { MediaAssetId = asset.Id, WorkspaceTagId = tagId });
        }

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return (asset.Id, MediaConcurrencyToken.From(asset.RowVersion));
    }

    private async Task<MediaAsset> ReloadAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(asset => asset.Id == assetId, Ct);
    }

    private async Task<MediaAssetVersion> ReloadVersionAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssetVersions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(version => version.MediaAssetId == assetId, Ct);
    }

    private async Task<Guid> SeedTagAsync(SeededWorkspace workspace, string name)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var tag = new WorkspaceTag
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            CreatedAt = Now,
        };

        db.WorkspaceTags.Add(tag);
        await db.SaveChangesAsync(Ct);

        return tag.Id;
    }

    private async Task<Guid> SeedCuisineAsync(string code, string displayName)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var cuisine = new Domain.Modules.Vocabulary.Data.Entities.Cuisine
        {
            Id = Guid.NewGuid(),
            Code = code,
            DisplayName = displayName,
        };

        db.Cuisines.Add(cuisine);
        await db.SaveChangesAsync(Ct);

        return cuisine.Id;
    }

    private async Task<Guid> SeedCourseAsync(string code, string displayName)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var course = new Domain.Modules.Vocabulary.Data.Entities.Course
        {
            Id = Guid.NewGuid(),
            Code = code,
            DisplayName = displayName,
        };

        db.Courses.Add(course);
        await db.SaveChangesAsync(Ct);

        return course.Id;
    }

    /// <summary>Puts the non-owner member at <paramref name="role"/>, for the role-gate tests.</summary>
    private async Task DemoteMemberAsync(SeededWorkspace workspace, WorkspaceRole role)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var membership = await db.WorkspaceMemberships
            .IgnoreQueryFilters()
            .Where(candidate => candidate.WorkspaceId == workspace.Id)
            .ToListAsync(Ct);

        foreach (var candidate in membership.Where(candidate => candidate.Role != WorkspaceRole.Owner))
        {
            candidate.Role = role;
        }

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
