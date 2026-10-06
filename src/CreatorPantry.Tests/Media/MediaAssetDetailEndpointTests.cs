using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}</c> and its utilization history, through the
/// real Gateway — real cookie session, real gateway-signed internal token, real API.
/// </summary>
/// <remarks>
/// <para>
/// What is only answerable here: that lineage is actually resolved through two other modules' facades, that the
/// soft-delete policy is the one approved for this prompt, and that a member of one workspace learns nothing
/// about the other. The query shapes themselves are proved against SQL Server by
/// <see cref="MediaAssetDetailSqlServerTests"/>.
/// </para>
/// <para>
/// Assets are seeded straight into the database rather than created through the write route, which is a multipart
/// upload of real image bytes through a malware scanner and an object store — <c>MediaAssetCreateTests</c> covers
/// that seam, and depending on it here would make every test below it fail for unrelated reasons.
/// </para>
/// </remarks>
public sealed class MediaAssetDetailEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AssetIn(SeededWorkspace workspace, Guid assetId, string suffix = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}{suffix}";

    // ---- Found ----

    [Fact]
    public async Task An_asset_is_published_with_its_metadata_its_current_version_and_its_token()
    {
        var id = await SeedAssetAsync(
            _fixture.WorkspaceA,
            "Soda bread hero",
            description: "Overhead on linen.",
            altText: "A round loaf, slashed across the top.",
            channelKey: "instagram",
            day: DayOfWeek.Wednesday);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Soda bread hero", body.GetProperty("title").GetString());
        Assert.Equal("Overhead on linen.", body.GetProperty("description").GetString());
        Assert.Equal("A round loaf, slashed across the top.", body.GetProperty("altText").GetString());
        Assert.Equal("Original", body.GetProperty("kind").GetString());
        Assert.Equal("instagram", body.GetProperty("channelKey").GetString());
        Assert.Equal("Wednesday", body.GetProperty("day").GetString());

        var current = body.GetProperty("currentVersion");
        Assert.Equal(1, current.GetProperty("versionNumber").GetInt32());
        Assert.Equal("image/jpeg", current.GetProperty("mediaType").GetString());
        Assert.Equal(1600, current.GetProperty("width").GetInt32());

        // Opaque, and present because 12.9d's patch has to quote it.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("concurrencyToken").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("deletedAt").ValueKind);
    }

    /// <summary>
    /// The whole version history travels with the asset, newest first, because an asset has a handful of versions
    /// and 12.9h downloads one by number.
    /// </summary>
    [Fact]
    public async Task The_version_history_travels_with_the_asset_newest_first()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");
        await SeedVersionsAsync(_fixture.WorkspaceA, id, 2, 3);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct));

        Assert.Equal(
            [3, 2, 1],
            body.GetProperty("versions").EnumerateArray()
                .Select(version => version.GetProperty("versionNumber").GetInt32()));
        Assert.Equal(3, body.GetProperty("versionCount").GetInt32());

        // CurrentVersionNumber is still 1, so the current version is version 1 and not the newest row.
        Assert.Equal(1, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// Nothing a detail read publishes is an address, and nothing names who uploaded the asset. The object key and
    /// the version's own id stay on the server; the checksum is published because it is the download's entity tag.
    /// </summary>
    [Fact]
    public async Task A_detail_carries_no_address_no_bytes_and_no_authorship()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct));

        foreach (var absent in (string[])
            ["objectKey", "url", "workspaceId", "createdByMembershipId", "updatedByMembershipId", "rowVersion"])
        {
            Assert.False(body.TryGetProperty(absent, out _), $"a detail must not publish {absent}");
        }

        // deletedByMembershipId is the deliberate exception, and is asserted rather than left as a silent
        // asymmetry with the two above. Who uploaded an asset is not something a library card or a detail
        // publishes; who *deleted* one is the question DAM-005 exists to answer, and there is no other route that
        // could answer it. Null while the asset is live, so a live detail still names nobody.
        Assert.True(body.TryGetProperty("deletedByMembershipId", out var deletedBy));
        Assert.Equal(JsonValueKind.Null, deletedBy.ValueKind);

        var current = body.GetProperty("currentVersion");
        Assert.False(current.TryGetProperty("objectKey", out _));
        Assert.False(current.TryGetProperty("url", out _));

        // The one verification aid that is published, following the brand document precedent.
        Assert.StartsWith("sha256:", current.GetProperty("contentChecksum").GetString());
    }

    [Fact]
    public async Task A_library_detail_is_not_cached()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.True((await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct)).Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_read_a_detail()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);

        Assert.Equal(
            HttpStatusCode.OK,
            (await member.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct)).StatusCode);
    }

    // ---- Lineage ----

    /// <summary>
    /// The whole reason the detail reaches into two other modules: a panel that said "used in recipe 3f2a…" and
    /// "prompt 9b1…" would be useless. Titles come from <c>IRecipeFacade</c> and labels from
    /// <c>IPromptRecordFacade</c>.
    /// </summary>
    [Fact]
    public async Task Lineage_names_the_recipes_and_the_prompts_rather_than_listing_ids()
    {
        var recipeId = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        await LinkRecipeAsync(_fixture.WorkspaceA, id, recipeId, RecipeAssetRole.Hero, "The hero shot");
        await SeedPromptAsync(_fixture.WorkspaceA, id, "Soda bread hero prompt");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct));

        var link = body.GetProperty("recipeLinks").EnumerateArray().Single();
        Assert.Equal(recipeId, link.GetProperty("recipeId").GetGuid());
        Assert.Equal("Soda bread", link.GetProperty("title").GetString());
        Assert.Equal("Hero", link.GetProperty("role").GetString());
        Assert.Equal("The hero shot", link.GetProperty("caption").GetString());
        Assert.Equal(1, body.GetProperty("recipeLinkCount").GetInt32());

        var prompt = body.GetProperty("prompts").EnumerateArray().Single();
        Assert.Equal("Soda bread hero prompt", prompt.GetProperty("label").GetString());
        Assert.Equal("Hero", prompt.GetProperty("imageKind").GetString());
    }

    /// <summary>
    /// Prompt lineage names a prompt; it never quotes one. The text is the creator's craft and is published only
    /// by the prompt's own route (ai.md).
    /// </summary>
    [Fact]
    public async Task Prompt_lineage_never_publishes_the_prompt_text()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");
        await SeedPromptAsync(_fixture.WorkspaceA, id, "Soda bread hero prompt");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var prompt = (await ReadAsync(response)).GetProperty("prompts").EnumerateArray().Single();

        Assert.False(prompt.TryGetProperty("text", out _));
        Assert.False(prompt.TryGetProperty("generatedText", out _));

        // The seeded body, checked against the whole response rather than one field, so a prompt body leaking
        // through some other name is caught too.
        Assert.DoesNotContain("Overhead shot of soda bread on linen", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every link is named, and the count agrees with the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This started as a test that the two can diverge, and the schema disproved it.</strong>
    /// <c>RecipeAssetLink</c> carries <c>(WorkspaceId, RecipeId)</c> to <c>(WorkspaceId, Id)</c> on
    /// <c>Recipe</c>, so a link pointing at another workspace's recipe is refused by the database rather than
    /// merely unexpected — an attempt to insert one fails even on SQLite. The reference is
    /// <c>Cascade</c>, so deleting the recipe deletes the link rather than leaving an unnameable one behind.
    /// </para>
    /// <para>
    /// So the count and the list agree, always, and that is what is asserted. The business layer still drops a
    /// link it cannot name rather than inventing a placeholder title, which is a guard against a future shape
    /// this key does not cover, not a divergence anything can reach today.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_recipe_link_is_named_and_the_count_agrees_with_the_list()
    {
        var hero = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var gallery = await SeedRecipeAsync(_fixture.WorkspaceA, "Olive oil cake");
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Shared photograph");

        await LinkRecipeAsync(_fixture.WorkspaceA, id, hero, RecipeAssetRole.Hero);
        await LinkRecipeAsync(_fixture.WorkspaceA, id, gallery, RecipeAssetRole.Gallery, sortOrder: 1);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct));
        var links = body.GetProperty("recipeLinks").EnumerateArray().ToList();

        Assert.Equal(2, body.GetProperty("recipeLinkCount").GetInt32());
        Assert.Equal(2, links.Count);
        Assert.Equal(
            ["Olive oil cake", "Soda bread"],
            links.Select(link => link.GetProperty("title").GetString()).Order());
    }

    /// <summary>
    /// The database refuses a link to another workspace's recipe outright, which is why the count and the list
    /// above cannot diverge. Asserted rather than assumed, because the whole shape of the lineage model rests on
    /// it — and because the attempt is the one a cross-workspace leak would start with.
    /// </summary>
    [Fact]
    public async Task A_link_to_the_other_workspaces_recipe_is_refused_by_the_database()
    {
        var theirs = await SeedRecipeAsync(_fixture.WorkspaceB, "B's secret");
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        await Assert.ThrowsAnyAsync<Exception>(
            () => LinkForeignRecipeAsync(_fixture.WorkspaceA, id, theirs));
    }

    /// <summary>
    /// The two cross-module lineage reads, called directly with the other workspace's ids.
    /// </summary>
    /// <remarks>
    /// Through the detail route these are unreachable — the asset is resolved first, so the ids handed to them
    /// always belong to the caller's workspace. That is exactly why they are called directly here: these two
    /// methods are new public facade surface that anything in the application may call with any id, and their
    /// isolation should be proved rather than inferred from the one caller that exists today.
    /// </remarks>
    [Fact]
    public async Task The_lineage_facades_name_nothing_belonging_to_the_other_workspace()
    {
        var recipeInB = await SeedRecipeAsync(_fixture.WorkspaceB, "B's secret");
        var assetInB = await SeedAssetAsync(_fixture.WorkspaceB, "B's hero");
        await SeedPromptAsync(_fixture.WorkspaceB, assetInB, "B's prompt");

        await using (var fromA = ScopeFor(_fixture.WorkspaceA))
        {
            var recipes = fromA.ServiceProvider.GetRequiredService<IRecipeFacade>();
            var prompts = fromA.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

            // Absent rather than refused, so asking cannot distinguish a neighbour's row from one that was never
            // written (tenancy.md).
            Assert.Empty(await recipes.ListTitlesAsync([recipeInB], Ct));
            Assert.Empty(await prompts.ListForAssetAsync(assetInB, Ct));
        }

        // And B names its own, so the two empty results above are the filter working rather than seeds that never
        // landed — the positive control the same shape of test is easy to ship without.
        await using var fromB = ScopeFor(_fixture.WorkspaceB);

        Assert.Equal(
            "B's secret",
            Assert.Single(await fromB.ServiceProvider.GetRequiredService<IRecipeFacade>()
                .ListTitlesAsync([recipeInB], Ct)).Title);

        Assert.Equal(
            "B's prompt",
            Assert.Single(await fromB.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
                .ListForAssetAsync(assetInB, Ct)).Label);
    }

    /// <summary>
    /// A title read is a set, not a positional list: an id the workspace cannot see is dropped, so a caller must
    /// not index the result against the ids it passed. Asserted because the facade's own remarks promise it.
    /// </summary>
    [Fact]
    public async Task A_title_read_drops_what_it_cannot_see_rather_than_padding_the_result()
    {
        var mine = await SeedRecipeAsync(_fixture.WorkspaceA, "Soda bread");
        var theirs = await SeedRecipeAsync(_fixture.WorkspaceB, "B's secret");

        await using var fromA = ScopeFor(_fixture.WorkspaceA);
        var titles = await fromA.ServiceProvider.GetRequiredService<IRecipeFacade>()
            .ListTitlesAsync([theirs, mine, Guid.NewGuid()], Ct);

        Assert.Equal("Soda bread", Assert.Single(titles).Title);
    }

    // ---- Not found and soft delete ----

    [Fact]
    public async Task An_unknown_asset_is_a_not_found_with_a_stable_code()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(AssetIn(_fixture.WorkspaceA, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// The approved policy: an ordinary read of a tombstone is a 404, so 12.9e's "exclusion from ordinary reads"
    /// holds and a panel that forgot to check cannot render a deleted asset as live.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_a_not_found_by_default()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Deleted hero", deletedAt: Now);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Asked for explicitly, the tombstone comes back with who deleted it and when — which is the only way
    /// DAM-005 can report either, and the reason the columns are not behind a global query filter.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_returned_with_its_tombstone_when_asked_for()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Deleted hero", deletedAt: Now);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, "?includeDeleted=true"), Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Deleted hero", body.GetProperty("title").GetString());
        Assert.Equal(Now, body.GetProperty("deletedAt").GetDateTimeOffset());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("deletedByMembershipId").ValueKind);
    }

    /// <summary>The flag widens the read rather than switching it: a live asset is still found with it set.</summary>
    [Fact]
    public async Task Asking_for_deleted_assets_still_returns_a_live_one()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Live");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(
            await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, "?includeDeleted=true"), Ct));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("deletedAt").ValueKind);
    }

    /// <summary>
    /// The flag cannot be used to reach another workspace's asset. It widens which of <em>your own</em> assets are
    /// visible, and nothing else.
    /// </summary>
    [Fact]
    public async Task Asking_for_deleted_assets_cannot_reach_the_other_workspaces_asset()
    {
        var inB = await SeedAssetAsync(_fixture.WorkspaceB, "B's hero", deletedAt: Now);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ownerA.GetAsync(AssetIn(_fixture.WorkspaceA, inB, "?includeDeleted=true"), Ct)).StatusCode);
    }

    // ---- Isolation ----

    /// <summary>
    /// A's asset read from B's own route: 404, and the same 404 an unknown id gets, so the reply never confirms
    /// that the asset exists in A (tenancy.md).
    /// </summary>
    [Fact]
    public async Task An_asset_of_one_workspace_cannot_be_read_from_the_other()
    {
        var inA = await SeedAssetAsync(_fixture.WorkspaceA, "A's hero");

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var byId = await ownerB.GetAsync(AssetIn(_fixture.WorkspaceB, inA), Ct);
        var unknown = await ownerB.GetAsync(AssetIn(_fixture.WorkspaceB, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        Assert.Equal(
            (await ReadAsync(unknown)).GetProperty("code").GetString(),
            (await ReadAsync(byId)).GetProperty("code").GetString());
    }

    /// <summary>
    /// B's owner naming A's slug: 404 rather than 403, so the reply does not confirm that Workspace A exists.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_through_the_others_route()
    {
        var inA = await SeedAssetAsync(_fixture.WorkspaceA, "A's hero");

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ownerB.GetAsync(AssetIn(_fixture.WorkspaceA, inA), Ct)).StatusCode);
    }

    // ---- Utilization history ----

    [Fact]
    public async Task A_utilization_history_is_paged_newest_first_with_its_total()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        for (var index = 0; index < 5; index++)
        {
            await SeedUseAsync(_fixture.WorkspaceA, id, new DateOnly(2026, 1, 1).AddDays(index), $"p{index}");
        }

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seen = new List<Guid>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var suffix = cursor is null
                ? "/utilization?limit=2"
                : $"/utilization?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var body = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, suffix), Ct));

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()));
            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());

            cursor = body.GetProperty("nextCursor").ValueKind is JsonValueKind.Null
                ? null
                : body.GetProperty("nextCursor").GetString();

            if (cursor is null)
            {
                break;
            }
        }

        Assert.Null(cursor);
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    /// <summary>
    /// The detail counts the history and does not carry it, which is the shape approved for this prompt: a use is
    /// logged every time the asset goes out, so the list grows without limit where versions do not.
    /// </summary>
    [Fact]
    public async Task The_detail_counts_utilization_without_listing_it()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");
        await SeedUseAsync(_fixture.WorkspaceA, id, new DateOnly(2026, 4, 1), "instagram");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(await client.GetAsync(AssetIn(_fixture.WorkspaceA, id), Ct));

        Assert.Equal(1, body.GetProperty("utilizationCount").GetInt32());
        Assert.False(body.TryGetProperty("utilization", out _));
    }

    /// <summary>
    /// A use publishes the calendar day it was logged against and the weekday logged with it, not an instant and
    /// not a weekday re-derived on this server.
    /// </summary>
    [Fact]
    public async Task A_use_publishes_the_date_and_the_day_it_was_logged_with()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");
        await SeedUseAsync(
            _fixture.WorkspaceA, id, new DateOnly(2026, 4, 1), "instagram",
            day: DayOfWeek.Saturday, campaign: "Spring bakes", notes: "Carousel, slide one");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var item = (await ReadAsync(await client.GetAsync(
                AssetIn(_fixture.WorkspaceA, id, "/utilization"), Ct)))
            .GetProperty("items").EnumerateArray().Single();

        Assert.Equal("2026-04-01", item.GetProperty("utilizedOn").GetString());
        Assert.Equal("Saturday", item.GetProperty("utilizedDay").GetString());
        Assert.Equal("instagram", item.GetProperty("platformKey").GetString());
        Assert.Equal("Spring bakes", item.GetProperty("campaignName").GetString());
        Assert.Equal("Carousel, slide one", item.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task An_asset_never_used_has_an_empty_history_rather_than_a_not_found()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Never used");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, "/utilization"), Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// A history read of an asset that is not there is a 404, not an empty page. An empty page reads as "never
    /// used", which would let a caller tell a real asset from a fictional one by nothing at all.
    /// </summary>
    [Fact]
    public async Task A_history_of_an_unknown_or_inaccessible_asset_is_a_not_found()
    {
        var inB = await SeedAssetAsync(_fixture.WorkspaceB, "B's hero");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        foreach (var assetId in (Guid[])[Guid.NewGuid(), inB])
        {
            var response = await ownerA.GetAsync(AssetIn(_fixture.WorkspaceA, assetId, "/utilization"), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                MediaErrorCodes.AssetNotFound,
                (await ReadAsync(response)).GetProperty("code").GetString());
        }
    }

    /// <summary>A deleted asset's history is a 404: this route has no flag of its own, by design.</summary>
    [Fact]
    public async Task A_deleted_assets_history_is_a_not_found()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Deleted hero", deletedAt: Now);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, "/utilization"), Ct)).StatusCode);
    }

    /// <summary>
    /// A cursor is bound to the asset as well as the workspace, so moving one between two assets of the same
    /// workspace is refused rather than quietly paging the wrong history.
    /// </summary>
    [Fact]
    public async Task A_cursor_from_one_assets_history_is_refused_by_another()
    {
        var mine = await SeedAssetAsync(_fixture.WorkspaceA, "Mine");
        var other = await SeedAssetAsync(_fixture.WorkspaceA, "Other");

        await SeedUseAsync(_fixture.WorkspaceA, mine, new DateOnly(2026, 4, 1), "one");
        await SeedUseAsync(_fixture.WorkspaceA, mine, new DateOnly(2026, 4, 2), "two");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await ReadAsync(await client.GetAsync(
            AssetIn(_fixture.WorkspaceA, mine, "/utilization?limit=1"), Ct));
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var response = await client.GetAsync(
            AssetIn(_fixture.WorkspaceA, other, $"/utilization?cursor={Uri.EscapeDataString(cursor)}"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MediaErrorCodes.AssetCursorInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_history_cursor_this_server_did_not_issue_is_refused()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.GetAsync(
            AssetIn(_fixture.WorkspaceA, id, "/utilization?cursor=not-a-cursor"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("cursor", out _));
    }

    [Fact]
    public async Task An_oversized_history_page_is_clamped_rather_than_refused()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(AssetIn(_fixture.WorkspaceA, id, "/utilization?limit=10000"), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_history_total_can_be_declined()
    {
        var id = await SeedAssetAsync(_fixture.WorkspaceA, "Soda bread hero");
        await SeedUseAsync(_fixture.WorkspaceA, id, new DateOnly(2026, 4, 1), "instagram");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await ReadAsync(await client.GetAsync(
            AssetIn(_fixture.WorkspaceA, id, "/utilization?includeTotal=false"), Ct));

        Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalCount").ValueKind);
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

    private async Task<Guid> SeedAssetAsync(
        SeededWorkspace workspace,
        string title,
        string? description = null,
        string? altText = null,
        string? channelKey = null,
        DayOfWeek? day = null,
        DateTimeOffset? deletedAt = null)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            AltText = altText,
            Kind = MediaAssetKind.Original,
            ChannelKey = channelKey,
            Day = day,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        asset.Versions.Add(NewVersion(asset.Id, 1));

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    private static MediaAssetVersion NewVersion(Guid mediaAssetId, int versionNumber) => new()
    {
        Id = Guid.NewGuid(),
        MediaAssetId = mediaAssetId,
        VersionNumber = versionNumber,
        MediaType = "image/jpeg",
        SizeBytes = 204_800 + versionNumber,
        Width = 1600,
        Height = 1200,
        ContentChecksum = $"sha256:{Convert.ToHexString(Guid.NewGuid().ToByteArray())}",
        ObjectKey = $"assets/{mediaAssetId:D}/{versionNumber}.jpg",
        OriginalFileName = $"soda-bread-{versionNumber}.jpg",
        Source = MediaAssetVersionSource.Upload,
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now.AddMinutes(versionNumber),
    };

    private async Task SeedVersionsAsync(SeededWorkspace workspace, Guid mediaAssetId, int from, int to)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        for (var versionNumber = from; versionNumber <= to; versionNumber++)
        {
            db.MediaAssetVersions.Add(NewVersion(mediaAssetId, versionNumber));
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task SeedUseAsync(
        SeededWorkspace workspace,
        Guid mediaAssetId,
        DateOnly utilizedOn,
        string platformKey,
        DayOfWeek? day = null,
        string? campaign = null,
        string? notes = null)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MediaAssetUtilizations.Add(new MediaAssetUtilization
        {
            Id = Guid.NewGuid(),
            MediaAssetId = mediaAssetId,
            PlatformKey = platformKey,
            UtilizedOn = utilizedOn,
            UtilizedDay = day ?? utilizedOn.DayOfWeek,
            CampaignName = campaign,
            Notes = notes,
            LoggedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task SeedPromptAsync(SeededWorkspace workspace, Guid mediaAssetId, string label)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.PromptRecords.Add(new PromptRecord
        {
            Id = Guid.NewGuid(),
            ChannelKey = "instagram",
            ImageKind = PromptImageKind.Hero,

            // The body, so a test can assert the whole response does not contain it. A prompt is the creator's
            // craft and lineage names it rather than quoting it (ai.md).
            Text = "Overhead shot of soda bread on linen, soft window light.",
            Label = label,
            Source = PromptRecordSource.Manual,
            DamAssetId = mediaAssetId,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);
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
        string? caption = null,
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
            Caption = caption,
            SortOrder = sortOrder,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Attempts a link row pointing at another workspace's recipe, with raw SQL so nothing above the database
    /// can refuse it first.
    /// </summary>
    /// <remarks>
    /// It throws, and that is what the test using it asserts. The composite foreign key
    /// <c>(WorkspaceId, RecipeId)</c> to <c>(WorkspaceId, Id)</c> on <c>Recipe</c> makes the row unrepresentable,
    /// on SQLite as well as SQL Server — which is what guarantees every link an asset holds names a recipe the
    /// workspace can see.
    /// </remarks>
    private async Task LinkForeignRecipeAsync(SeededWorkspace workspace, Guid mediaAssetId, Guid recipeId)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO RecipeAssetLinks (Id, WorkspaceId, RecipeId, MediaAssetId, Role, Caption, SortOrder)
            VALUES ({0}, {1}, {2}, {3}, {4}, NULL, 1);
            """,
            [Guid.NewGuid(), workspace.Id, recipeId, mediaAssetId, (int)RecipeAssetRole.Gallery],
            Ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
