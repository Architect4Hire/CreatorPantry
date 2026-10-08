using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>DELETE /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}</c> (DAM-005): what a soft delete changes,
/// what it deliberately leaves alone, what a repeat does, and what the other workspace cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// The assertions that matter most are the negative ones. The restriction this prompt opens with is that nothing
/// physical is removed and no link is cut, so almost every test here checks what <em>survived</em> rather than what
/// changed — the version row, the object key, and all three kinds of link.
/// </para>
/// <para>
/// The concurrency ordering is proved against SQL Server by <see cref="MediaAssetDeleteSqlServerTests"/>, for the
/// reason the patch tests record: SQLite never moves a <c>rowversion</c>, so a token that this asset once had still
/// matches there.
/// </para>
/// </remarks>
public sealed class MediaAssetDeleteEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AssetIn(SeededWorkspace workspace, Guid assetId, string suffix = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}{suffix}";

    // ---- Deleting ----

    [Fact]
    public async Task A_delete_stamps_the_tombstone_and_reports_it()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await DeleteAsync(client, id, token);

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Soda bread hero", body.GetProperty("title").GetString());
        Assert.False(body.GetProperty("alreadyDeleted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("deletedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("deletedByMembershipId").ValueKind);

        var asset = await ReloadAsync(id);
        Assert.NotNull(asset.DeletedAt);
        Assert.NotNull(asset.DeletedByMembershipId);

        // The tombstone is the last thing that happened to the row, so UpdatedAt moved with it.
        Assert.Equal(asset.DeletedAt, asset.UpdatedAt);
        Assert.Equal(asset.DeletedByMembershipId, asset.UpdatedByMembershipId);
    }

    /// <summary>
    /// The restriction this prompt opens with: nothing physical is removed. The version row, its object key and its
    /// checksum all survive, because the bytes are still there and the row is the only record of where.
    /// </summary>
    [Fact]
    public async Task A_delete_removes_no_bytes_and_leaves_the_version_row_intact()
    {
        var (id, token) = await SeededAsync();
        var before = await ReloadVersionAsync(id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await DeleteAsync(client, id, token);

        var after = await ReloadVersionAsync(id);

        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ObjectKey, after.ObjectKey);
        Assert.Equal(before.ContentChecksum, after.ContentChecksum);
        Assert.Equal(before.MediaType, after.MediaType);
        Assert.Equal(before.CreatedAt, after.CreatedAt);

        // And the asset keeps pointing at it, so nothing has to be repaired to bring the asset back.
        var asset = await ReloadAsync(id);
        Assert.Equal(1, asset.CurrentVersionNumber);

        // Ownership and authorship are not what a deletion changes, so neither moved. Asserted because a
        // tombstone writes four fields and the test should say which four.
        Assert.Equal(_fixture.WorkspaceA.Id, asset.WorkspaceId);
        Assert.Equal(Now, asset.CreatedAt);
        Assert.NotEqual(Guid.Empty, asset.CreatedByMembershipId);
        Assert.Equal(MediaAssetKind.Original, asset.Kind);
        Assert.Equal("Soda bread hero", asset.Title);
    }

    /// <summary>
    /// auth.md names destructive deletion among the operations that require an audit event, and
    /// <c>AuditLog</c> requires the entry carry no creator content.
    /// </summary>
    /// <remarks>
    /// <strong>The title is asserted absent, which is a correction.</strong> The summary first read
    /// <c>Soft-deleted the asset "{Title}"</c>, and that is content: <c>RecipeBusiness</c> spells the rule out as
    /// "no title, no creator text" where it writes its own entries, so a title here would have been this module
    /// deciding the rule applies less to it. The asset is identified by <c>ResourceId</c>.
    /// </remarks>
    [Fact]
    public async Task A_delete_writes_one_audit_entry_carrying_no_creator_content()
    {
        var (id, token) = await SeededAsync(description: "Overhead on linen, shot at f2.8.");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await DeleteAsync(client, id, token);

        var entry = Assert.Single(await AuditAsync(id));

        Assert.Equal(MediaAuditActions.Deleted, entry.Action);
        Assert.Equal(MediaAuditActions.ResourceType, entry.ResourceType);
        Assert.Equal(id.ToString("D"), entry.ResourceId);

        // A pointer, not content: which version the asset stood at when it went.
        Assert.Equal("1", entry.BeforeReference);

        foreach (var creatorText in (string[])["Soda bread hero", "f2.8", "assets/", "soda-bread.jpg"])
        {
            Assert.DoesNotContain(creatorText, entry.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(creatorText, entry.BeforeReference ?? "", StringComparison.Ordinal);
        }

        // Stamped server-side by the ownership interceptor, like any other workspace-owned insert — so an entry
        // cannot be filed against the wrong workspace.
        Assert.Equal(_fixture.WorkspaceA.Id, entry.WorkspaceId);

        // And the other workspace has none, which is the half of "one entry" a single-workspace count misses.
        Assert.Equal(0, await AuditCountAsync(_fixture.WorkspaceB, id));
    }

    // ---- Linked content ----

    /// <summary>
    /// All three link types survive, and the response names what is now pointing at a tombstone. This is the
    /// prompt's restriction and the behaviour approved for it.
    /// </summary>
    [Fact]
    public async Task Every_link_survives_and_the_response_reports_the_impact()
    {
        var soda = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var cake = await SeedRecipeAsync(_fixture.WorkspaceA, "Olive oil cake");
        var (id, token) = await SeededAsync();

        await LinkRecipeAsync(_fixture.WorkspaceA, id, soda, RecipeAssetRole.Hero);
        await LinkRecipeAsync(_fixture.WorkspaceA, id, cake, RecipeAssetRole.Gallery, sortOrder: 1);
        await LinkBrandAsync(_fixture.WorkspaceA, id);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var affected = (await DeleteAsync(client, id, token)).GetProperty("affected");

        Assert.True(affected.GetProperty("any").GetBoolean());
        Assert.Equal(2, affected.GetProperty("recipeCount").GetInt32());
        Assert.Equal(
            ["Olive oil cake", "Soda bread"],
            affected.GetProperty("recipes").EnumerateArray()
                .Select(recipe => recipe.GetProperty("title").GetString()).Order());
        Assert.Equal(1, affected.GetProperty("brandProfileCount").GetInt32());
        Assert.Equal(0, affected.GetProperty("testAttachmentCount").GetInt32());

        // Nothing was cut. The links are exactly as many as before, pointing at the same asset.
        Assert.Equal(2, await RecipeLinkCountAsync(id));
        Assert.Equal(1, await BrandLinkCountAsync(id));
    }

    /// <summary>
    /// An asset linked twice to one recipe is one recipe to go and fix, so the count is distinct recipes rather
    /// than link rows.
    /// </summary>
    [Fact]
    public async Task A_recipe_linked_twice_is_reported_once()
    {
        var soda = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var (id, token) = await SeededAsync();

        await LinkRecipeAsync(_fixture.WorkspaceA, id, soda, RecipeAssetRole.Hero);
        await LinkRecipeAsync(_fixture.WorkspaceA, id, soda, RecipeAssetRole.Gallery, sortOrder: 1);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var affected = (await DeleteAsync(client, id, token)).GetProperty("affected");

        Assert.Equal(1, affected.GetProperty("recipeCount").GetInt32());
        Assert.Single(affected.GetProperty("recipes").EnumerateArray());
        Assert.Equal(2, await RecipeLinkCountAsync(id));
    }

    [Fact]
    public async Task An_unlinked_asset_reports_no_impact()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var affected = (await DeleteAsync(client, id, token)).GetProperty("affected");

        Assert.False(affected.GetProperty("any").GetBoolean());
        Assert.Equal(0, affected.GetProperty("recipeCount").GetInt32());
        Assert.Empty(affected.GetProperty("recipes").EnumerateArray());
        Assert.Equal(0, affected.GetProperty("brandProfileCount").GetInt32());
        Assert.Equal(0, affected.GetProperty("testAttachmentCount").GetInt32());
    }

    /// <summary>
    /// A test run's attachments are counted, with the other workspace holding its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this closes:</strong> every other test asserted <c>testAttachmentCount == 0</c>, so the
    /// <c>TestAttachmentLinks</c> query in <c>FindReferencesAsync</c> could have been miswired — counting the wrong
    /// column, or nothing at all — and nothing would have failed. A count nobody ever makes non-zero is not a
    /// tested count.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does not claim.</strong> It is not evidence about the workspace filter on that
    /// query, and cannot be: adding <c>IgnoreQueryFilters()</c> there changes no result. <c>TestAttachmentLink</c>
    /// carries <c>(WorkspaceId, MediaAssetId)</c> to <c>(WorkspaceId, Id)</c> on <c>MediaAsset</c>, so every
    /// attachment row for a given asset is in that asset's workspace by construction — and the asset id was already
    /// resolved through the filtered read. The <c>MediaAssetId</c> predicate does the isolating, and the same holds
    /// for the recipe and brand counts beside it. Worth writing down, because "the filter is untested here" and
    /// "the filter is not what protects this" look identical from the outside.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Test_attachments_are_counted_and_the_other_workspace_keeps_its_own()
    {
        // B gets two attachments on its own asset, so a lost filter would show up as 2 rather than 0 below.
        var (inB, _) = await SeededAsync(workspace: _fixture.WorkspaceB);
        await AttachAsync(_fixture.WorkspaceB, inB, count: 2);

        var (inA, tokenA) = await SeededAsync();
        await AttachAsync(_fixture.WorkspaceA, inA, count: 3);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var affected = (await DeleteAsync(client, inA, tokenA)).GetProperty("affected");

        Assert.True(affected.GetProperty("any").GetBoolean());
        Assert.Equal(3, affected.GetProperty("testAttachmentCount").GetInt32());

        // Attachments survive the deletion, like every other link.
        Assert.Equal(3, await TestAttachmentCountAsync(inA));
        Assert.Equal(2, await TestAttachmentCountAsync(inB));
    }

    // ---- Exclusion from ordinary reads ----

    /// <summary>
    /// What a tombstone actually costs the asset: it leaves the library, leaves the detail read, and takes its
    /// history with it. The one way back in is the explicit flag 12.9c added.
    /// </summary>
    [Fact]
    public async Task A_removed_asset_drops_out_of_every_ordinary_read()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);

        // Present before.
        Assert.Single((await ReadAsync(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets", Ct)))
            .GetProperty("items").EnumerateArray());

        await DeleteAsync(client, id, token);

        var library = await ReadAsync(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets", Ct));

        Assert.Empty(library.GetProperty("items").EnumerateArray());
        Assert.Equal(0, library.GetProperty("totalCount").GetInt32());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, "/utilization"), Ct)).StatusCode);

        // And the one route that still answers, which is how DAM-005 reports who removed it and when.
        var tombstone = await ReadAsync(await client.GetAsync(
            AssetIn(_fixture.WorkspaceA, id, "?includeDeleted=true"), Ct));

        Assert.NotEqual(JsonValueKind.Null, tombstone.GetProperty("deletedAt").ValueKind);
    }

    /// <summary>A removed asset cannot be edited: the patch path finds only live assets.</summary>
    [Fact]
    public async Task A_removed_asset_cannot_be_patched()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await DeleteAsync(client, id, token);

        var response = await client.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = await TokenAsync(id), title = "Renamed after removal" },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Soda bread hero", (await ReloadAsync(id)).Title);
    }

    // ---- Repeat ----

    /// <summary>
    /// A repeat with a current token succeeds, returns the original tombstone, and writes nothing — no second
    /// timestamp, no second actor, no second audit entry.
    /// </summary>
    [Fact]
    public async Task Removing_an_already_removed_asset_changes_nothing()
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await DeleteAsync(client, id, token);
        var after = await ReloadAsync(id);

        // The token as it now stands. On SQLite that is the same value as before, because nothing ever moves a
        // rowversion there — so this test does NOT prove the token-before-already-deleted ordering, whatever the
        // sequence of calls suggests. MediaAssetDeleteSqlServerTests carries that proof. What is proved here is
        // the no-op itself: the original timestamp and actor come back and nothing is written.
        var repeat = await DeleteAsync(client, id, MediaConcurrencyToken.From(after.RowVersion));

        Assert.True(repeat.GetProperty("alreadyDeleted").GetBoolean());
        Assert.Equal(
            first.GetProperty("deletedAt").GetDateTimeOffset(),
            repeat.GetProperty("deletedAt").GetDateTimeOffset());
        Assert.Equal(
            first.GetProperty("deletedByMembershipId").GetGuid(),
            repeat.GetProperty("deletedByMembershipId").GetGuid());

        var again = await ReloadAsync(id);
        Assert.Equal(after.DeletedAt, again.DeletedAt);
        Assert.Equal(after.UpdatedAt, again.UpdatedAt);

        // One entry for one deletion.
        Assert.Single(await AuditAsync(id));
    }

    /// <summary>A repeat still reports the impact, so a client that lost the first response is not left guessing.</summary>
    [Fact]
    public async Task A_repeat_still_reports_what_references_the_asset()
    {
        var soda = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var (id, token) = await SeededAsync();
        await LinkRecipeAsync(_fixture.WorkspaceA, id, soda, RecipeAssetRole.Hero);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await DeleteAsync(client, id, token);

        var repeat = await DeleteAsync(client, id, await TokenAsync(id));
        var affected = repeat.GetProperty("affected");

        Assert.True(affected.GetProperty("any").GetBoolean());
        Assert.Equal("Soda bread", affected.GetProperty("recipes").EnumerateArray()
            .Single().GetProperty("title").GetString());
    }

    // ---- Confirmation, token and role ----

    /// <summary>
    /// A well-formed body is not a decision. Without <c>confirmed: true</c> the asset stays, and the refusal names
    /// the field rather than being a generic 400.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task A_delete_without_confirmation_is_refused_naming_the_field(bool? confirmed)
    {
        var (id, token) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendDeleteAsync(client, id, new { confirmed, expectedConcurrencyToken = token });
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MediaErrorCodes.AssetInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("confirmed", out _));
        Assert.Null((await ReloadAsync(id)).DeletedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public async Task A_malformed_token_is_a_bad_request_rather_than_a_conflict(string? token)
    {
        var (id, _) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendDeleteAsync(client, id, new { confirmed = true, expectedConcurrencyToken = token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(
            "expectedConcurrencyToken", out _));
        Assert.Null((await ReloadAsync(id)).DeletedAt);
    }

    /// <summary>
    /// A well-formed token that is not this asset's is a conflict, and the asset survives. This is testable over
    /// SQLite for the reason the patch tests record: eight zero bytes are not the seeded <c>randomblob(8)</c>.
    /// </summary>
    [Fact]
    public async Task A_token_that_is_not_this_assets_is_refused_as_a_conflict()
    {
        var (id, _) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendDeleteAsync(client, id, new
        {
            confirmed = true,
            expectedConcurrencyToken = Convert.ToBase64String(new byte[MediaConcurrencyToken.ByteLength]),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetStaleToken,
            (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Null((await ReloadAsync(id)).DeletedAt);
    }

    /// <summary>
    /// Both failures at once are both reported, so a client fixing its request is not corrected one round trip at a
    /// time.
    /// </summary>
    [Fact]
    public async Task A_request_wrong_in_two_ways_hears_about_both()
    {
        var (id, _) = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendDeleteAsync(client, id, new { confirmed = false, expectedConcurrencyToken = "nope" });
        var errors = (await ReadAsync(response)).GetProperty("errors");

        Assert.True(errors.TryGetProperty("confirmed", out _));
        Assert.True(errors.TryGetProperty("expectedConcurrencyToken", out _));
    }

    /// <summary>
    /// A contributor may add and edit an asset but not remove one: removing takes finished work out of every
    /// collaborator's library. Checked at the facade as well as the route, so the policy is not the only guard.
    /// </summary>
    [Fact]
    public async Task A_contributor_cannot_remove_an_asset()
    {
        var (id, token) = await SeededAsync();
        await SetMemberRoleAsync(_fixture.WorkspaceA, WorkspaceRole.Contributor);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await SendDeleteAsync(member, id, new { confirmed = true, expectedConcurrencyToken = token });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await ReloadAsync(id)).DeletedAt);

        // The same member can still patch it, so the refusal is the delete gate and not a broken membership.
        var patch = await member.PatchAsJsonAsync(
            AssetIn(_fixture.WorkspaceA, id),
            new { expectedConcurrencyToken = token, title = "Still editable" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }

    [Fact]
    public async Task An_editor_may_remove_an_asset()
    {
        var (id, token) = await SeededAsync();
        await SetMemberRoleAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await SendDeleteAsync(member, id, new { confirmed = true, expectedConcurrencyToken = token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await ReloadAsync(id)).DeletedAt);
    }

    // ---- Not found and isolation ----

    [Fact]
    public async Task An_unknown_asset_is_a_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await SendDeleteAsync(client, Guid.NewGuid(), new
        {
            confirmed = true,
            expectedConcurrencyToken = Convert.ToBase64String(new byte[MediaConcurrencyToken.ByteLength]),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A's asset deleted by B's owner: 404 through B's own route, and 404 naming A's slug. A's asset stays live.
    /// </summary>
    /// <remarks>
    /// The two cases fail for different reasons and both are worth having. Through <strong>B's slug</strong> the
    /// request is a member acting in their own workspace on an id that is not in it, so the query filter is what
    /// refuses — that is the isolation this feature owns. Naming <strong>A's slug</strong> is refused earlier, at
    /// membership resolution, before the route's action runs at all. A test that only did the second would prove
    /// the tenancy middleware works and nothing about this delete.
    /// </remarks>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_remove_the_others_asset()
    {
        var (inA, token) = await SeededAsync();

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var slug in (SeededWorkspace[])[_fixture.WorkspaceB, _fixture.WorkspaceA])
        {
            var response = await ownerB.SendAsync(
                HttpMethod.Delete,
                AssetIn(slug, inA),
                new { confirmed = true, expectedConcurrencyToken = token },
                null,
                Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Null((await ReloadAsync(inA)).DeletedAt);
    }

    /// <summary>
    /// A foreign asset answers 404 whatever else is wrong with the request, so the ordering cannot be used to learn
    /// that it exists.
    /// </summary>
    /// <remarks>
    /// The shape failures come first and are indistinguishable from a local asset's — a missing confirmation or a
    /// malformed token is a 400 either way, because neither check looks at the asset. Once the body is well formed,
    /// a foreign asset is 404 and never the 409 a stale token would earn on one the caller can see. Locks in the
    /// ordering rather than leaving it to the reading of the code.
    /// </remarks>
    [Fact]
    public async Task A_foreign_asset_never_answers_a_conflict_whatever_else_is_wrong()
    {
        var (inA, tokenA) = await SeededAsync();
        var wrongToken = Convert.ToBase64String(new byte[MediaConcurrencyToken.ByteLength]);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        // Well formed, so the asset is looked up: 404, not 409, for both a right and a wrong token.
        foreach (var token in (string[])[tokenA, wrongToken])
        {
            var found = await ownerB.SendAsync(
                HttpMethod.Delete,
                AssetIn(_fixture.WorkspaceB, inA),
                new { confirmed = true, expectedConcurrencyToken = token },
                null,
                Ct);

            Assert.Equal(HttpStatusCode.NotFound, found.StatusCode);
        }

        // Shape failures come first, so these say nothing about the asset at all.
        foreach (var body in (object[])
            [new { confirmed = false, expectedConcurrencyToken = tokenA },
             new { confirmed = true, expectedConcurrencyToken = "not-base64" }])
        {
            var refused = await ownerB.SendAsync(
                HttpMethod.Delete, AssetIn(_fixture.WorkspaceB, inA), body, null, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        Assert.Null((await ReloadAsync(inA)).DeletedAt);
    }

    /// <summary>
    /// B deleting its own asset leaves A's alone, and A's library is unaffected. The direction a one-sided isolation
    /// test misses.
    /// </summary>
    [Fact]
    public async Task Removing_one_workspaces_asset_leaves_the_others_untouched()
    {
        var (inA, _) = await SeededAsync();
        var (inB, tokenB) = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var response = await ownerB.SendAsync(
            HttpMethod.Delete,
            AssetIn(_fixture.WorkspaceB, inB),
            new { confirmed = true, expectedConcurrencyToken = tokenB },
            null,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await ReloadAsync(inB)).DeletedAt);
        Assert.Null((await ReloadAsync(inA)).DeletedAt);

        // A's library still lists A's asset.
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        var library = await ReadAsync(await ownerA.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets", Ct));

        Assert.Single(library.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// The impact report counts only this workspace's references. Both workspaces link a recipe to their own asset,
    /// and neither count describes the other.
    /// </summary>
    [Fact]
    public async Task The_impact_report_never_counts_the_other_workspaces_links()
    {
        var recipeInB = await SeedRecipeAsync(_fixture.WorkspaceB, "B's recipe");
        var (inB, _) = await SeededAsync(workspace: _fixture.WorkspaceB);
        await LinkRecipeAsync(_fixture.WorkspaceB, inB, recipeInB, RecipeAssetRole.Hero);
        await LinkBrandAsync(_fixture.WorkspaceB, inB);

        var (inA, tokenA) = await SeededAsync();

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        var affected = (await DeleteAsync(ownerA, inA, tokenA)).GetProperty("affected");

        Assert.False(affected.GetProperty("any").GetBoolean());
        Assert.Equal(0, affected.GetProperty("recipeCount").GetInt32());
        Assert.Equal(0, affected.GetProperty("brandProfileCount").GetInt32());

        // B's links are still there, uncounted and uncut.
        Assert.Equal(1, await RecipeLinkCountAsync(inB));
        Assert.Equal(1, await BrandLinkCountAsync(inB));
    }

    /// <summary>
    /// What the detail read says would be left pointing at a tombstone is what the removal then reports (12.10h).
    /// A client warns from the first and confirms from the second, so the two must count the same way — distinct
    /// brand profiles, and every test attachment — or a creator is told one number before and another after.
    /// </summary>
    [Fact]
    public async Task The_detail_read_warns_of_the_same_links_the_removal_then_reports()
    {
        var soda = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var (id, token) = await SeededAsync();
        await LinkRecipeAsync(_fixture.WorkspaceA, id, soda, RecipeAssetRole.Hero);
        await LinkBrandAsync(_fixture.WorkspaceA, id);
        await AttachAsync(_fixture.WorkspaceA, id, count: 2);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var before = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct));
        var affected = (await DeleteAsync(client, id, token)).GetProperty("affected");

        Assert.Equal(1, before.GetProperty("brandProfileCount").GetInt32());
        Assert.Equal(2, before.GetProperty("testAttachmentCount").GetInt32());
        Assert.Equal(
            affected.GetProperty("brandProfileCount").GetInt32(), before.GetProperty("brandProfileCount").GetInt32());
        Assert.Equal(
            affected.GetProperty("testAttachmentCount").GetInt32(),
            before.GetProperty("testAttachmentCount").GetInt32());
        Assert.Equal(affected.GetProperty("recipeCount").GetInt32(), before.GetProperty("recipeLinkCount").GetInt32());
    }

    /// <summary>
    /// The counts on a detail read are this workspace's own. Workspace B's links to its own asset do not raise
    /// what Workspace A is told about an unlinked one, and each sees its own figures.
    /// </summary>
    [Fact]
    public async Task The_detail_reads_link_counts_never_include_the_other_workspaces_links()
    {
        var (inB, _) = await SeededAsync(workspace: _fixture.WorkspaceB);
        await LinkBrandAsync(_fixture.WorkspaceB, inB);
        await AttachAsync(_fixture.WorkspaceB, inB, count: 2);

        var (inA, _) = await SeededAsync();

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var fromA = await ReadAsync(await ownerA.GetAsync(AssetIn(_fixture.WorkspaceA, inA), Ct));
        var fromB = await ReadAsync(await ownerB.GetAsync(AssetIn(_fixture.WorkspaceB, inB), Ct));

        Assert.Equal(0, fromA.GetProperty("brandProfileCount").GetInt32());
        Assert.Equal(0, fromA.GetProperty("testAttachmentCount").GetInt32());
        Assert.Equal(1, fromB.GetProperty("brandProfileCount").GetInt32());
        Assert.Equal(2, fromB.GetProperty("testAttachmentCount").GetInt32());
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

    private Task<HttpResponseMessage> SendDeleteAsync(GatewayClient client, Guid assetId, object body) =>
        client.SendAsync(HttpMethod.Delete, AssetIn(_fixture.WorkspaceA, assetId), body, null, Ct);

    /// <summary>Deletes and insists it succeeded, so a test about what changed cannot pass on a refusal.</summary>
    private async Task<JsonElement> DeleteAsync(GatewayClient client, Guid assetId, string token)
    {
        var response = await client.SendAsync(
            HttpMethod.Delete,
            AssetIn(_fixture.WorkspaceA, assetId),
            new { confirmed = true, expectedConcurrencyToken = token },
            null,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadAsync(response);
    }

    private async Task<(Guid Id, string Token)> SeededAsync(
        SeededWorkspace? workspace = null, string? description = null)
    {
        var target = workspace ?? _fixture.WorkspaceA;

        await using var scope = ScopeFor(target);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread hero",
            Description = description,
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
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

    private async Task<string> TokenAsync(Guid assetId) =>
        MediaConcurrencyToken.From((await ReloadAsync(assetId)).RowVersion);

    private async Task<MediaAssetVersion> ReloadVersionAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssetVersions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(version => version.MediaAssetId == assetId, Ct);
    }

    private async Task<List<AuditLog>> AuditAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AuditLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => entry.ResourceId == assetId.ToString("D"))
            .ToListAsync(Ct);
    }

    private async Task<Guid> SeedRecipeAsync(SeededWorkspace workspace, string title)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(Ct);

        return recipe.Id;
    }

    private async Task LinkRecipeAsync(
        SeededWorkspace workspace,
        Guid mediaAssetId,
        Guid recipeId,
        RecipeAssetRole role,
        int sortOrder = 0)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.RecipeAssetLinks.Add(new RecipeAssetLink
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            MediaAssetId = mediaAssetId,
            Role = role,
            SortOrder = sortOrder,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Links the asset into a brand profile's slots, creating the profile if the workspace has none.
    /// </summary>
    private async Task LinkBrandAsync(SeededWorkspace workspace, Guid mediaAssetId)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var profileId = await db.BrandProfiles
            .Select(profile => (Guid?)profile.Id)
            .FirstOrDefaultAsync(Ct);

        if (profileId is null)
        {
            var profile = new BrandProfile
            {
                Id = Guid.NewGuid(),

                // Required and check-constrained not to be blank, so the seed has to name it.
                BrandName = "A brand",
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            db.BrandProfiles.Add(profile);
            profileId = profile.Id;
        }

        db.BrandAssetLinks.Add(new BrandAssetLink
        {
            Id = Guid.NewGuid(),
            BrandProfileId = profileId.Value,
            MediaAssetId = mediaAssetId,

            // Check-constrained to be specified, so the enum's zero will not do.
            Role = BrandAssetRole.PrimaryLogo,
            SortOrder = 0,
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> RecipeLinkCountAsync(Guid mediaAssetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .RecipeAssetLinks
            .IgnoreQueryFilters()
            .CountAsync(link => link.MediaAssetId == mediaAssetId, Ct);
    }

    private async Task<int> BrandLinkCountAsync(Guid mediaAssetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandAssetLinks
            .IgnoreQueryFilters()
            .CountAsync(link => link.MediaAssetId == mediaAssetId, Ct);
    }

    /// <summary>
    /// Attaches the asset to a test run <paramref name="count"/> times, creating the recipe, version and run it
    /// needs.
    /// </summary>
    /// <remarks>
    /// A chain rather than a single row: <c>TestAttachmentLink</c> hangs off a <c>RecipeTestRun</c>, which pins a
    /// <c>RecipeVersion</c>, which belongs to a <c>Recipe</c>. <c>TestIssueId</c> is left null because an
    /// attachment does not need an issue and this test is about the count, not the shape of a test run.
    /// </remarks>
    private async Task AttachAsync(SeededWorkspace workspace, Guid mediaAssetId, int count)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Tested recipe",
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = RecipePolicy.FirstVersionNumber,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = actor,
            CreatedAt = Now,
            SnapshotSchemaVersion = 1,
        };

        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeVersionId = version.Id,
            TestedAt = Now,
            TestedByMembershipId = actor,
            Outcome = TestRunOutcome.Succeeded,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Recipes.Add(recipe);
        db.RecipeVersions.Add(version);
        db.RecipeTestRuns.Add(run);

        for (var index = 0; index < count; index++)
        {
            db.TestAttachmentLinks.Add(new TestAttachmentLink
            {
                Id = Guid.NewGuid(),
                RecipeTestRunId = run.Id,
                MediaAssetId = mediaAssetId,
                SortOrder = index,
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> TestAttachmentCountAsync(Guid mediaAssetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .TestAttachmentLinks
            .IgnoreQueryFilters()
            .CountAsync(link => link.MediaAssetId == mediaAssetId, Ct);
    }

    /// <summary>Audit entries for one asset, filed against <paramref name="workspace"/>.</summary>
    private async Task<int> AuditCountAsync(SeededWorkspace workspace, Guid assetId)
    {
        await using var scope = ScopeFor(workspace);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AuditLogs
            .IgnoreQueryFilters()
            .CountAsync(
                entry => entry.ResourceId == assetId.ToString("D") && entry.WorkspaceId == workspace.Id, Ct);
    }

    /// <summary>Puts the non-owner member at <paramref name="role"/>, for the role-gate tests.</summary>
    private async Task SetMemberRoleAsync(SeededWorkspace workspace, WorkspaceRole role)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var memberships = await db.WorkspaceMemberships
            .IgnoreQueryFilters()
            .Where(candidate => candidate.WorkspaceId == workspace.Id)
            .ToListAsync(Ct);

        foreach (var candidate in memberships.Where(candidate => candidate.Role != WorkspaceRole.Owner))
        {
            candidate.Role = role;
        }

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
