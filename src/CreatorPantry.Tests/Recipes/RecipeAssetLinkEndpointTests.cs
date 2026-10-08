using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST</c> and <c>DELETE .../recipes/{recipeId}/asset-links</c> through the real Gateway (RCPUB-005): what
/// a link is, that it is an edit of the recipe, every refusal, replay, and what one workspace can do to
/// another's recipes and assets — which is nothing.
/// </summary>
public sealed class RecipeAssetLinkEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A well-formed token that was never any recipe's.</summary>
    private const string NeverThisRecipes = "CAcGBQQDAgE=";

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    private static string LinksIn(SeededWorkspace workspace, Guid recipeId) => $"{RecipeIn(workspace, recipeId)}/asset-links";

    // ---- Linking ----

    [Fact]
    public async Task Linking_puts_the_asset_on_the_recipe_and_answers_with_the_recipe()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Hero", caption = "  Fresh from the oven.  ", expectedConcurrencyToken = recipe.Token },
            Ct);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(recipe.Id, body.GetProperty("id").GetGuid());

        var link = Assert.Single(body.GetProperty("assetLinks").EnumerateArray());
        Assert.Equal(asset, link.GetProperty("mediaAssetId").GetGuid());
        Assert.Equal("Hero", link.GetProperty("role").GetString());
        Assert.Equal("Fresh from the oven.", link.GetProperty("caption").GetString());
        Assert.Equal(0, link.GetProperty("sortOrder").GetInt32());

        // Unpinned and belonging to no step, said as nulls rather than by leaving the fields out.
        Assert.Equal(JsonValueKind.Null, link.GetProperty("mediaAssetVersionNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, link.GetProperty("instructionStepId").ValueKind);

        // And it is really there, not only in the answer.
        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipe.Id), Ct));
        Assert.Equal(link.GetProperty("id").GetGuid(), reread.GetProperty("assetLinks")[0].GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A link is part of what a version records, so linking is an edit: one new version, a new token, and the
    /// old token spent. Had it written no version, history and the live recipe would disagree about which
    /// pictures the recipe has.
    /// </summary>
    [Fact]
    public async Task Linking_is_an_edit_of_the_recipe_and_writes_one_new_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var body = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Gallery"));

        Assert.Equal(2, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        // And like any other edit it is refused from a token that is not the recipe's current one. That a link
        // spends the token it was composed against is RecipeAssetLinkSqlServerTests' to show: this host is
        // SQLite, which never advances a row version.
        var stale = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, NeverThisRecipes, asset, "Process");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, (await BodyOf(stale)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("Hero")]
    [InlineData("Gallery")]
    [InlineData("Process")]
    [InlineData("Social")]
    public async Task Every_recipe_level_role_can_be_linked(string role)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, role);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(role, (await BodyOf(response)).GetProperty("assetLinks")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_step_image_names_a_step_of_this_recipe()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Step", instructionStepId = recipe.StepIds[1], expectedConcurrencyToken = recipe.Token },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var link = (await BodyOf(response)).GetProperty("assetLinks")[0];
        Assert.Equal("Step", link.GetProperty("role").GetString());
        Assert.Equal(recipe.StepIds[1], link.GetProperty("instructionStepId").GetGuid());
    }

    [Fact]
    public async Task A_link_can_pin_one_version_and_the_pin_stays_when_the_asset_gains_another()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA, versions: 2);

        var linked = await BodyOf(await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Gallery", versionNumber = 1, expectedConcurrencyToken = recipe.Token },
            Ct));

        Assert.Equal(1, linked.GetProperty("assetLinks")[0].GetProperty("mediaAssetVersionNumber").GetInt32());

        await AddVersionAsync(_fixture.WorkspaceA, asset, 3);

        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipe.Id), Ct));
        Assert.Equal(1, reread.GetProperty("assetLinks")[0].GetProperty("mediaAssetVersionNumber").GetInt32());
    }

    [Fact]
    public async Task New_links_go_to_the_end_and_the_same_asset_may_be_linked_in_two_roles()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var first = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Hero"));
        var second = await BodyOf(await LinkAsync(
            client, _fixture.WorkspaceA, recipe.Id, first.GetProperty("concurrencyToken").GetString()!, asset, "Social"));

        Assert.Equal(
            (string[])["Hero", "Social"],
            second.GetProperty("assetLinks").EnumerateArray().Select(link => link.GetProperty("role").GetString()));
        Assert.Equal(
            (int[])[0, 1],
            second.GetProperty("assetLinks").EnumerateArray().Select(link => link.GetProperty("sortOrder").GetInt32()));
    }

    // ---- Refusals ----

    /// <summary>
    /// A second lead image is refused rather than replacing the first: replacing would unlink a picture the
    /// creator chose as a side effect of linking another.
    /// </summary>
    [Fact]
    public async Task A_second_hero_is_refused_and_the_first_stays()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var first = await SeedAssetAsync(_fixture.WorkspaceA);
        var second = await SeedAssetAsync(_fixture.WorkspaceA);

        var linked = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, first, "Hero"));
        var token = linked.GetProperty("concurrencyToken").GetString()!;

        var response = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, token, second, "Hero");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.AssetLinkHeroConflict, (await BodyOf(response)).GetProperty("code").GetString());

        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipe.Id), Ct));
        Assert.Equal(first, Assert.Single(reread.GetProperty("assetLinks").EnumerateArray()).GetProperty("mediaAssetId").GetGuid());

        // Nothing was written, so the token is still good.
        Assert.Equal(token, reread.GetProperty("concurrencyToken").GetString());
    }

    [Fact]
    public async Task The_same_asset_in_the_same_role_twice_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var linked = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Gallery"));
        var response = await LinkAsync(
            client, _fixture.WorkspaceA, recipe.Id, linked.GetProperty("concurrencyToken").GetString()!, asset, "Gallery");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.AssetLinkDuplicateConflict, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_asset_this_workspace_does_not_have_is_refused_naming_the_field()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, Guid.NewGuid(), "Gallery");
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.AssetLinkTargetUnprocessable, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("mediaAssetId", out _));
    }

    /// <summary>
    /// An asset removed from the library cannot be linked, and gets exactly the answer an unknown one does —
    /// the removal is not disclosed to someone who only holds its id.
    /// </summary>
    [Fact]
    public async Task A_removed_asset_is_refused_in_the_same_words_as_an_unknown_one()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var removed = await SeedAssetAsync(_fixture.WorkspaceA, deleted: true);

        var forRemoved = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, removed, "Gallery");
        var forUnknown = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, Guid.NewGuid(), "Gallery");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forRemoved.StatusCode);
        Assert.Equal(await Refusal(forUnknown), await Refusal(forRemoved));
    }

    [Fact]
    public async Task A_pin_naming_a_version_the_asset_does_not_have_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Gallery", versionNumber = 7, expectedConcurrencyToken = recipe.Token },
            Ct);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.AssetLinkTargetUnprocessable, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    [Fact]
    public async Task A_step_image_naming_a_step_of_another_recipe_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var other = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Step", instructionStepId = other.StepIds[0], expectedConcurrencyToken = recipe.Token },
            Ct);
        var body = await BodyOf(response);

        // Same workspace, so the foreign key would accept it. This is the rule that does not.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(body.GetProperty("errors").TryGetProperty("instructionStepId", out _));
    }

    [Fact]
    public async Task A_malformed_request_hears_about_every_field_at_once()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { versionNumber = 0, caption = new string('x', RecipePolicy.CaptionMaxLength + 1) },
            Ct);
        var errors = (await BodyOf(response)).GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(errors.TryGetProperty("mediaAssetId", out _));
        Assert.True(errors.TryGetProperty("role", out _));
        Assert.True(errors.TryGetProperty("versionNumber", out _));
        Assert.True(errors.TryGetProperty("caption", out _));
        Assert.True(errors.TryGetProperty("expectedConcurrencyToken", out _));
    }

    [Fact]
    public async Task A_step_without_its_step_and_a_step_on_another_role_are_both_malformed()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var stepWithoutStep = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Step", expectedConcurrencyToken = recipe.Token },
            Ct);
        var heroWithStep = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Hero", instructionStepId = recipe.StepIds[0], expectedConcurrencyToken = recipe.Token },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, stepWithoutStep.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, heroWithStep.StatusCode);
        Assert.True((await BodyOf(heroWithStep)).GetProperty("errors").TryGetProperty("instructionStepId", out _));
    }

    [Fact]
    public async Task An_archived_recipe_takes_no_links()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var archived = await BodyOf(await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipe.Id)}/archive", new { expectedConcurrencyToken = recipe.Token }, Ct));

        var response = await LinkAsync(
            client, _fixture.WorkspaceA, recipe.Id, archived.GetProperty("concurrencyToken").GetString()!, asset, "Gallery");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_viewer_can_neither_link_nor_unlink()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var recipe = await SeedRecipeAsync(owner, _fixture.WorkspaceB);
        var asset = await SeedAssetAsync(_fixture.WorkspaceB);
        var linked = await BodyOf(await LinkAsync(owner, _fixture.WorkspaceB, recipe.Id, recipe.Token, asset, "Gallery"));
        var token = linked.GetProperty("concurrencyToken").GetString()!;
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();

        // Workspace B's seeded member is a Viewer.
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);

        var link = await LinkAsync(viewer, _fixture.WorkspaceB, recipe.Id, token, asset, "Social");
        var unlink = await UnlinkAsync(viewer, _fixture.WorkspaceB, recipe.Id, linkId, token);

        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlink.StatusCode);
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceB, recipe.Id));
    }

    // ---- Unlinking ----

    /// <summary>
    /// The restriction, on the rows themselves: unlinking removes one link and leaves the asset, its versions
    /// and its usage history exactly as they were.
    /// </summary>
    [Fact]
    public async Task Unlinking_removes_the_link_and_nothing_about_the_asset()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA, versions: 2, uses: 3);

        var linked = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Hero"));
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();

        var response = await UnlinkAsync(
            client, _fixture.WorkspaceA, recipe.Id, linkId, linked.GetProperty("concurrencyToken").GetString()!);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("assetLinks").EnumerateArray());

        // An edit, like linking: one more version.
        Assert.Equal(3, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        var after = await AssetFactsAsync(_fixture.WorkspaceA, asset);
        Assert.True(after.Exists);
        Assert.Null(after.DeletedAt);
        Assert.Equal(2, after.Versions);
        Assert.Equal(3, after.Uses);

        // Still readable in the library, by its own route.
        var read = await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets/{asset}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(3, (await BodyOf(read)).GetProperty("utilizationCount").GetInt32());
    }

    [Fact]
    public async Task Unlinking_one_link_leaves_the_others_and_their_positions()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var token = recipe.Token;
        JsonElement body = default;
        foreach (var role in (string[])["Hero", "Gallery", "Social"])
        {
            body = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, token, asset, role));
            token = body.GetProperty("concurrencyToken").GetString()!;
        }

        var middle = body.GetProperty("assetLinks")[1].GetProperty("id").GetGuid();
        var after = await BodyOf(await UnlinkAsync(client, _fixture.WorkspaceA, recipe.Id, middle, token));

        Assert.Equal(
            (string[])["Hero", "Social"],
            after.GetProperty("assetLinks").EnumerateArray().Select(link => link.GetProperty("role").GetString()));
        Assert.Equal(
            (int[])[0, 2],
            after.GetProperty("assetLinks").EnumerateArray().Select(link => link.GetProperty("sortOrder").GetInt32()));

        // And the gap does not trip the next link: it lands after the largest position in use.
        var relinked = await BodyOf(await LinkAsync(
            client, _fixture.WorkspaceA, recipe.Id, after.GetProperty("concurrencyToken").GetString()!, asset, "Process"));
        Assert.Equal(3, relinked.GetProperty("assetLinks")[2].GetProperty("sortOrder").GetInt32());
    }

    [Fact]
    public async Task Unlinking_a_link_this_recipe_does_not_have_is_a_not_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var other = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var onOther = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, other.Id, other.Token, asset, "Gallery"));
        var foreignLink = onOther.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();

        var unknown = await UnlinkAsync(client, _fixture.WorkspaceA, recipe.Id, Guid.NewGuid(), recipe.Token);
        var anotherRecipes = await UnlinkAsync(client, _fixture.WorkspaceA, recipe.Id, foreignLink, recipe.Token);

        var forUnknown = await Refusal(unknown);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.StartsWith($"404|{RecipeErrorCodes.AssetLinkNotFound}|", forUnknown);

        // A link of another recipe is not this recipe's to remove, and is answered exactly as an unknown id is.
        Assert.Equal(forUnknown, await Refusal(anotherRecipes));
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceA, other.Id));
    }

    [Fact]
    public async Task Unlinking_needs_the_recipes_current_token()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var linked = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Gallery"));
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();

        var missing = await client.SendAsync(
            HttpMethod.Delete, $"{LinksIn(_fixture.WorkspaceA, recipe.Id)}/{linkId}", new { }, headers: null, Ct);
        var stale = await UnlinkAsync(client, _fixture.WorkspaceA, recipe.Id, linkId, NeverThisRecipes);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceA, recipe.Id));
    }

    // ---- Replay ----

    [Fact]
    public async Task Replaying_a_link_with_its_key_returns_the_first_answer_and_links_once()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var request = new { mediaAssetId = asset, role = "Gallery", expectedConcurrencyToken = recipe.Token };

        var first = await client.PostAsJsonAsync(LinksIn(_fixture.WorkspaceA, recipe.Id), request, "link-key-1", Ct);
        var replay = await client.PostAsJsonAsync(LinksIn(_fixture.WorkspaceA, recipe.Id), request, "link-key-1", Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));

        // Byte for byte the first answer — the same link id, the same token — and one link, one version.
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceA, recipe.Id));

        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipe.Id), Ct));
        Assert.Equal(2, reread.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// Without a key a repeat is not replayed — but it still cannot link twice. The first call moved the
    /// recipe's token, so on SQL Server the second is refused as stale; and where that guard is absent, as on
    /// this SQLite host, the duplicate rule refuses it instead. Either way: a conflict, and one link.
    /// </summary>
    [Fact]
    public async Task Repeating_a_link_without_a_key_is_refused_and_links_once()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var first = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Gallery");
        var repeat = await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Gallery");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceA, recipe.Id));
    }

    [Fact]
    public async Task One_key_cannot_be_reused_for_a_different_link()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Gallery", expectedConcurrencyToken = recipe.Token },
            "link-key-2",
            Ct);
        var reused = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Social", expectedConcurrencyToken = recipe.Token },
            "link-key-2",
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(reused)).GetProperty("code").GetString());
        Assert.Equal(1, await LinkCountAsync(_fixture.WorkspaceA, recipe.Id));
    }

    [Fact]
    public async Task Replaying_an_unlink_with_its_key_returns_the_first_answer()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var linked = await BodyOf(await LinkAsync(client, _fixture.WorkspaceA, recipe.Id, recipe.Token, asset, "Gallery"));
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();
        var token = linked.GetProperty("concurrencyToken").GetString()!;
        var key = new Dictionary<string, string> { [IdempotencyPolicy.KeyHeader] = "unlink-key-1" };
        var path = $"{LinksIn(_fixture.WorkspaceA, recipe.Id)}/{linkId}";

        var first = await client.SendAsync(HttpMethod.Delete, path, new { expectedConcurrencyToken = token }, key, Ct);
        var replay = await client.SendAsync(HttpMethod.Delete, path, new { expectedConcurrencyToken = token }, key, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));

        // Without the key the same request would have been a conflict; and the version was written once.
        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipe.Id), Ct));
        Assert.Equal(3, reread.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    // ---- History, restore, duplicate, and a removed step ----

    [Fact]
    public async Task Restoring_an_earlier_version_puts_back_the_links_it_had()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA, versions: 2);

        // Version 2 has a pinned step image; version 3 has none.
        var linked = await BodyOf(await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Step", instructionStepId = recipe.StepIds[0], versionNumber = 2, expectedConcurrencyToken = recipe.Token },
            Ct));
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();
        var unlinked = await BodyOf(await UnlinkAsync(
            client, _fixture.WorkspaceA, recipe.Id, linkId, linked.GetProperty("concurrencyToken").GetString()!));

        var restored = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipe.Id)}/versions/2/restore",
            new { expectedConcurrencyToken = unlinked.GetProperty("concurrencyToken").GetString() },
            Ct);
        var body = await BodyOf(restored);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        // The whole link as archived: the same id, the role, the step it belonged to, and the pin.
        var link = Assert.Single(body.GetProperty("assetLinks").EnumerateArray());
        Assert.Equal(linkId, link.GetProperty("id").GetGuid());
        Assert.Equal("Step", link.GetProperty("role").GetString());
        Assert.Equal(recipe.StepIds[0], link.GetProperty("instructionStepId").GetGuid());
        Assert.Equal(2, link.GetProperty("mediaAssetVersionNumber").GetInt32());
    }

    /// <summary>
    /// Restoring a version from before a step existed removes the step and its picture's link together. The
    /// step key is <c>Restrict</c>, so this is the restore that would fail if the link outlived its step.
    /// </summary>
    [Fact]
    public async Task Restoring_a_version_from_before_a_step_existed_removes_the_step_and_its_image_link()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var detail = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipe.Id), Ct));
        var group = detail.GetProperty("instructionGroups")[0];

        // Version 2 adds a third step; version 3 gives it a picture.
        var edited = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipe.Id),
            new
            {
                expectedConcurrencyToken = recipe.Token,
                instructions = new[]
                {
                    new
                    {
                        id = group.GetProperty("id").GetGuid(),
                        title = group.GetProperty("title").GetString(),
                        steps = new object[]
                        {
                            new { id = recipe.StepIds[0], text = "Cream the butter and sugar." },
                            new { id = recipe.StepIds[1], text = "Fold in the flour." },
                            new { text = "Bake until golden." },
                        },
                    },
                },
            },
            Ct));
        var thirdStep = edited.GetProperty("instructionGroups")[0].GetProperty("steps")[2].GetProperty("id").GetGuid();

        var linked = await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new
            {
                mediaAssetId = asset,
                role = "Step",
                instructionStepId = thirdStep,
                expectedConcurrencyToken = edited.GetProperty("concurrencyToken").GetString(),
            },
            Ct);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        var restored = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipe.Id)}/versions/1/restore",
            new { expectedConcurrencyToken = (await BodyOf(linked)).GetProperty("concurrencyToken").GetString() },
            Ct);
        var body = await BodyOf(restored);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(2, body.GetProperty("instructionGroups")[0].GetProperty("steps").GetArrayLength());
        Assert.Empty(body.GetProperty("assetLinks").EnumerateArray());
    }

    [Fact]
    public async Task A_copy_of_the_recipe_carries_its_links_with_step_images_following_the_copys_own_steps()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Step", instructionStepId = recipe.StepIds[1], versionNumber = 1, expectedConcurrencyToken = recipe.Token },
            Ct);

        var duplicated = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipe.Id)}/duplicate", new { title = "Olive oil cake, again" }, Ct);
        Assert.Equal(HttpStatusCode.Created, duplicated.StatusCode);

        var copy = await BodyOf(await client.GetAsync(duplicated.Headers.Location!.ToString(), Ct));
        var copySteps = copy.GetProperty("instructionGroups")[0].GetProperty("steps").EnumerateArray()
            .Select(step => step.GetProperty("id").GetGuid()).ToList();
        var link = Assert.Single(copy.GetProperty("assetLinks").EnumerateArray());

        // The same asset and pin, on the copy's own second step — not on the source recipe's.
        Assert.Equal(asset, link.GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(1, link.GetProperty("mediaAssetVersionNumber").GetInt32());
        Assert.Equal(copySteps[1], link.GetProperty("instructionStepId").GetGuid());
        Assert.DoesNotContain(link.GetProperty("instructionStepId").GetGuid(), recipe.StepIds);
    }

    /// <summary>
    /// Removing a step never removes its picture. The link is kept and demoted to a recipe-level in-progress
    /// image — and the step, which the key would otherwise refuse to delete, goes.
    /// </summary>
    [Fact]
    public async Task Removing_a_step_keeps_its_image_as_an_in_progress_picture()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var linked = await BodyOf(await client.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = asset, role = "Step", instructionStepId = recipe.StepIds[1], caption = "Folding in.", expectedConcurrencyToken = recipe.Token },
            Ct));
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();
        var group = linked.GetProperty("instructionGroups")[0];

        // Resubmit the method with only its first step.
        var edited = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipe.Id),
            new
            {
                expectedConcurrencyToken = linked.GetProperty("concurrencyToken").GetString(),
                instructions = new[]
                {
                    new
                    {
                        id = group.GetProperty("id").GetGuid(),
                        title = group.GetProperty("title").GetString(),
                        steps = new[] { new { id = recipe.StepIds[0], text = "Cream the butter and sugar." } },
                    },
                },
            },
            Ct);
        var body = await BodyOf(edited);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Single(body.GetProperty("instructionGroups")[0].GetProperty("steps").EnumerateArray());

        var link = Assert.Single(body.GetProperty("assetLinks").EnumerateArray());
        Assert.Equal(linkId, link.GetProperty("id").GetGuid());
        Assert.Equal("Process", link.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, link.GetProperty("instructionStepId").ValueKind);
        Assert.Equal("Folding in.", link.GetProperty("caption").GetString());
    }

    // ---- Isolation ----

    /// <summary>
    /// The restriction: a member of A cannot link B's asset to A's recipe. B's asset is answered exactly as an
    /// asset that does not exist is, so the attempt does not even confirm that B has it.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_asset_cannot_be_linked_and_is_not_disclosed()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var recipe = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);
        var assetInB = await SeedAssetAsync(_fixture.WorkspaceB);

        var forNeighbours = await LinkAsync(ownerA, _fixture.WorkspaceA, recipe.Id, recipe.Token, assetInB, "Gallery");
        var forUnknown = await LinkAsync(ownerA, _fixture.WorkspaceA, recipe.Id, recipe.Token, Guid.NewGuid(), "Gallery");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forNeighbours.StatusCode);
        Assert.Equal(await Refusal(forUnknown), await Refusal(forNeighbours));
        Assert.Equal(0, await LinkCountAsync(_fixture.WorkspaceA, recipe.Id));

        // Nor does the pin leak it: a version number is only ever judged for an asset this workspace holds.
        var pinned = await ownerA.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipe.Id),
            new { mediaAssetId = assetInB, role = "Gallery", versionNumber = 1, expectedConcurrencyToken = recipe.Token },
            Ct);
        var pinnedErrors = (await BodyOf(pinned)).GetProperty("errors");
        Assert.True(pinnedErrors.TryGetProperty("mediaAssetId", out _));
        Assert.False(pinnedErrors.TryGetProperty("versionNumber", out _));
    }

    /// <summary>
    /// The key behind a step image pins the workspace and not the recipe, so a neighbour's step id has to be
    /// answered exactly as a step of another recipe in this workspace is, and as an id that names nothing.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_step_cannot_carry_a_step_image_and_is_not_disclosed()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var recipe = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);
        var recipeInB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        Task<HttpResponseMessage> StepImageOn(Guid stepId) =>
            ownerA.PostAsJsonAsync(
                LinksIn(_fixture.WorkspaceA, recipe.Id),
                new { mediaAssetId = asset, role = "Step", instructionStepId = stepId, expectedConcurrencyToken = recipe.Token },
                Ct);

        var forNeighbours = await StepImageOn(recipeInB.StepIds[0]);
        var forUnknown = await StepImageOn(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forNeighbours.StatusCode);
        Assert.Equal(await Refusal(forUnknown), await Refusal(forNeighbours));
        Assert.Equal(0, await LinkCountAsync(_fixture.WorkspaceA, recipe.Id));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_link_to_or_unlink_from_the_others_recipe()
    {
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var recipeInB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);
        var assetInB = await SeedAssetAsync(_fixture.WorkspaceB);
        var linked = await BodyOf(await LinkAsync(ownerB, _fixture.WorkspaceB, recipeInB.Id, recipeInB.Token, assetInB, "Hero"));
        var token = linked.GetProperty("concurrencyToken").GetString()!;
        var linkId = linked.GetProperty("assetLinks")[0].GetProperty("id").GetGuid();

        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var assetInA = await SeedAssetAsync(_fixture.WorkspaceA);

        // Through B's own slug: A's owner is not a member, so the workspace is not there at all.
        var linkViaB = await LinkAsync(ownerA, _fixture.WorkspaceB, recipeInB.Id, token, assetInA, "Gallery");
        var unlinkViaB = await UnlinkAsync(ownerA, _fixture.WorkspaceB, recipeInB.Id, linkId, token);

        // Through A's slug with B's ids: the recipe is not one of A's.
        var linkViaA = await LinkAsync(ownerA, _fixture.WorkspaceA, recipeInB.Id, token, assetInA, "Gallery");
        var unlinkViaA = await UnlinkAsync(ownerA, _fixture.WorkspaceA, recipeInB.Id, linkId, token);

        Assert.Equal(HttpStatusCode.NotFound, linkViaB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlinkViaB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, linkViaA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlinkViaA.StatusCode);

        // B's recipe is exactly as B left it.
        var reread = await BodyOf(await ownerB.GetAsync(RecipeIn(_fixture.WorkspaceB, recipeInB.Id), Ct));
        Assert.Equal(linkId, Assert.Single(reread.GetProperty("assetLinks").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(token, reread.GetProperty("concurrencyToken").GetString());
    }

    [Fact]
    public async Task One_workspaces_idempotency_key_replays_nothing_in_the_other()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var recipeA = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);
        var recipeB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);
        var assetA = await SeedAssetAsync(_fixture.WorkspaceA);
        var assetB = await SeedAssetAsync(_fixture.WorkspaceB);

        var inA = await ownerA.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceA, recipeA.Id),
            new { mediaAssetId = assetA, role = "Gallery", expectedConcurrencyToken = recipeA.Token },
            "shared-key",
            Ct);
        var inB = await ownerB.PostAsJsonAsync(
            LinksIn(_fixture.WorkspaceB, recipeB.Id),
            new { mediaAssetId = assetB, role = "Gallery", expectedConcurrencyToken = recipeB.Token },
            "shared-key",
            Ct);

        Assert.Equal(HttpStatusCode.OK, inA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inB.StatusCode);
        Assert.False(inB.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(assetB, (await BodyOf(inB)).GetProperty("assetLinks")[0].GetProperty("mediaAssetId").GetGuid());
    }

    // ---- Helpers ----

    private sealed record SeededRecipe(Guid Id, string Token, IReadOnlyList<Guid> StepIds);

    private Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    /// <summary>A recipe with one method of two steps, read back so a test starts from real ids and a real token.</summary>
    private static async Task<SeededRecipe> SeedRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new
            {
                title = "Olive oil cake",
                instructions = new[]
                {
                    new
                    {
                        title = "Batter",
                        steps = new[] { new { text = "Cream the butter and sugar." }, new { text = "Fold in the flour." } },
                    },
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct));

        return new SeededRecipe(
            detail.GetProperty("id").GetGuid(),
            detail.GetProperty("concurrencyToken").GetString()!,
            [.. detail.GetProperty("instructionGroups")[0].GetProperty("steps").EnumerateArray()
                .Select(step => step.GetProperty("id").GetGuid())]);
    }

    private static Task<HttpResponseMessage> LinkAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string token, Guid assetId, string role) =>
        client.PostAsJsonAsync(
            LinksIn(workspace, recipeId),
            new { mediaAssetId = assetId, role, expectedConcurrencyToken = token },
            Ct);

    private static Task<HttpResponseMessage> UnlinkAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, Guid linkId, string token) =>
        client.SendAsync(
            HttpMethod.Delete,
            $"{LinksIn(workspace, recipeId)}/{linkId}",
            new { expectedConcurrencyToken = token },
            headers: null,
            Ct);

    /// <summary>
    /// Seeds an asset straight into the database, with the workspace context resolved first so the ownership
    /// interceptor stamps <c>WorkspaceId</c> exactly as a request would.
    /// </summary>
    private async Task<Guid> SeedAssetAsync(SeededWorkspace workspace, int versions = 1, int uses = 0, bool deleted = false)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread hero",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = versions,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            DeletedAt = deleted ? Now : null,
            DeletedByMembershipId = deleted ? actor : null,
        };

        for (var number = 1; number <= versions; number++)
        {
            asset.Versions.Add(Version(asset.Id, number, actor));
        }

        db.MediaAssets.Add(asset);

        for (var index = 0; index < uses; index++)
        {
            db.MediaAssetUtilizations.Add(new MediaAssetUtilization
            {
                Id = Guid.NewGuid(),
                MediaAssetId = asset.Id,
                PlatformKey = "instagram",
                UtilizedOn = DateOnly.FromDateTime(Now.UtcDateTime),
                UtilizedDay = Now.DayOfWeek,
                LoggedByMembershipId = actor,
                CreatedAt = Now,
            });
        }

        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    private async Task AddVersionAsync(SeededWorkspace workspace, Guid assetId, int versionNumber)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.MediaAssetVersions.Add(Version(assetId, versionNumber, Guid.NewGuid()));
        await db.SaveChangesAsync(Ct);
    }

    private static MediaAssetVersion Version(Guid assetId, int number, Guid actor) =>
        new()
        {
            Id = Guid.NewGuid(),
            MediaAssetId = assetId,
            VersionNumber = number,
            MediaType = "image/jpeg",
            SizeBytes = 204_800,
            Width = 1600,
            Height = 1200,
            ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            ObjectKey = $"assets/{assetId:D}/{number}.jpg",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = Now,
        };

    private async Task<(bool Exists, DateTimeOffset? DeletedAt, int Versions, int Uses)> AssetFactsAsync(
        SeededWorkspace workspace, Guid assetId)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == assetId, Ct);

        return (
            asset is not null,
            asset?.DeletedAt,
            await db.MediaAssetVersions.CountAsync(version => version.MediaAssetId == assetId, Ct),
            await db.MediaAssetUtilizations.CountAsync(use => use.MediaAssetId == assetId, Ct));
    }

    private async Task<int> LinkCountAsync(SeededWorkspace workspace, Guid recipeId)
    {
        await using var scope = ScopeFor(workspace);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .RecipeAssetLinks.CountAsync(link => link.RecipeId == recipeId, Ct);
    }

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    /// <summary>What a refusal says, without the parts that differ per request: its status, code, title and field names.</summary>
    private static async Task<string> Refusal(HttpResponseMessage response)
    {
        var body = await BodyOf(response);
        var fields = body.TryGetProperty("errors", out var errors)
            ? string.Join(",", errors.EnumerateObject().Select(field => $"{field.Name}={field.Value}"))
            : string.Empty;

        return $"{(int)response.StatusCode}|{body.GetProperty("code").GetString()}|{body.GetProperty("title").GetString()}|{fields}";
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
