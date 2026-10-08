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
/// <c>GET</c>, <c>POST</c> and <c>DELETE .../recipes/{recipeId}/test-runs/{testRunId}/attachments</c> through the
/// real Gateway (RCPUB-005, the test-image role): a picture is evidence of one trial, so it hangs off the test
/// and not the recipe — no recipe version is written — and one workspace cannot reach another's.
/// </summary>
public sealed class RecipeTestAttachmentEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    private static string AttachmentsIn(SeededWorkspace workspace, Guid recipeId, Guid runId) =>
        $"{RecipeIn(workspace, recipeId)}/test-runs/{runId}/attachments";

    // ---- Attaching and listing ----

    [Fact]
    public async Task A_test_with_no_pictures_lists_none()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty((await BodyOf(response)).EnumerateArray());
    }

    [Fact]
    public async Task Attaching_answers_with_the_attachment_and_the_list_then_has_it()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA, versions: 2);

        var response = await client.PostAsJsonAsync(
            AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId),
            new { mediaAssetId = asset, versionNumber = 1, testIssueId = test.IssueId, caption = "  The dense crumb.  " },
            Ct);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(asset, body.GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(1, body.GetProperty("mediaAssetVersionNumber").GetInt32());
        Assert.Equal(test.IssueId, body.GetProperty("testIssueId").GetGuid());
        Assert.Equal("The dense crumb.", body.GetProperty("caption").GetString());
        Assert.Equal(0, body.GetProperty("sortOrder").GetInt32());

        // A reference and never an address: the bytes are the library's to serve.
        Assert.Equal(
            (string[])["id", "mediaAssetId", "mediaAssetVersionNumber", "testIssueId", "sortOrder", "caption"],
            body.EnumerateObject().Select(property => property.Name));

        var listed = await BodyOf(await client.GetAsync(AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId), Ct));
        Assert.Equal(body.GetProperty("id").GetGuid(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_unpinned_picture_of_the_whole_test_says_so_with_nulls_and_later_ones_follow_it()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var first = await SeedAssetAsync(_fixture.WorkspaceA);
        var second = await SeedAssetAsync(_fixture.WorkspaceA);

        var one = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, first));
        var two = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, second));

        Assert.Equal(JsonValueKind.Null, one.GetProperty("mediaAssetVersionNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, one.GetProperty("testIssueId").ValueKind);
        Assert.Equal(1, two.GetProperty("sortOrder").GetInt32());

        var listed = await BodyOf(await client.GetAsync(AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId), Ct));
        Assert.Equal(
            (Guid[])[first, second],
            listed.EnumerateArray().Select(item => item.GetProperty("mediaAssetId").GetGuid()));
    }

    /// <summary>
    /// The decision that keeps this off the recipe's link routes: a test image is evidence of a trial, not part
    /// of the recipe, so attaching one writes no recipe version and puts nothing among the recipe's own links.
    /// </summary>
    [Fact]
    public async Task Attaching_is_not_an_edit_of_the_recipe()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        var attached = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, asset));
        await client.SendAsync(
            HttpMethod.Delete,
            $"{AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId)}/{attached.GetProperty("id").GetGuid()}",
            body: null,
            headers: null,
            Ct);

        var recipe = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, test.RecipeId), Ct));

        Assert.Equal(1, recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Empty(recipe.GetProperty("assetLinks").EnumerateArray());
    }

    [Fact]
    public async Task A_contributor_may_attach_and_a_viewer_may_only_read()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var test = await SeedTestAsync(owner, _fixture.WorkspaceB);
        var asset = await SeedAssetAsync(_fixture.WorkspaceB);
        var attached = await BodyOf(await AttachAsync(owner, _fixture.WorkspaceB, test, asset));

        // Workspace B's seeded member is a Viewer.
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);
        var path = AttachmentsIn(_fixture.WorkspaceB, test.RecipeId, test.RunId);

        var read = await viewer.GetAsync(path, Ct);
        var attach = await viewer.PostAsJsonAsync(path, new { mediaAssetId = await SeedAssetAsync(_fixture.WorkspaceB) }, Ct);
        var detach = await viewer.SendAsync(
            HttpMethod.Delete, $"{path}/{attached.GetProperty("id").GetGuid()}", body: null, headers: null, Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, attach.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detach.StatusCode);
        Assert.Equal(1, await AttachmentCountAsync(_fixture.WorkspaceB, test.RunId));
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_removed_asset_is_refused_in_the_same_words_as_an_unknown_one()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var removed = await SeedAssetAsync(_fixture.WorkspaceA, deleted: true);

        var forRemoved = await AttachAsync(client, _fixture.WorkspaceA, test, removed);
        var forUnknown = await AttachAsync(client, _fixture.WorkspaceA, test, Guid.NewGuid());
        var refusal = await Refusal(forUnknown);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forUnknown.StatusCode);
        Assert.StartsWith($"422|{RecipeErrorCodes.TestAttachmentTargetUnprocessable}|", refusal);
        Assert.Contains("mediaAssetId=", refusal);
        Assert.Equal(refusal, await Refusal(forRemoved));
    }

    [Fact]
    public async Task A_version_the_asset_does_not_have_and_an_issue_of_another_test_are_each_named()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var other = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);

        var badVersion = await client.PostAsJsonAsync(path, new { mediaAssetId = asset, versionNumber = 9 }, Ct);
        var foreignIssue = await client.PostAsJsonAsync(path, new { mediaAssetId = asset, testIssueId = other.IssueId }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, badVersion.StatusCode);
        Assert.True((await BodyOf(badVersion)).GetProperty("errors").TryGetProperty("versionNumber", out _));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignIssue.StatusCode);
        Assert.True((await BodyOf(foreignIssue)).GetProperty("errors").TryGetProperty("testIssueId", out _));

        Assert.Equal(0, await AttachmentCountAsync(_fixture.WorkspaceA, test.RunId));
    }

    [Fact]
    public async Task The_same_picture_against_the_same_thing_twice_is_refused_but_against_an_issue_as_well_is_not()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);

        await AttachAsync(client, _fixture.WorkspaceA, test, asset);
        var again = await AttachAsync(client, _fixture.WorkspaceA, test, asset);
        var againstIssue = await client.PostAsJsonAsync(path, new { mediaAssetId = asset, testIssueId = test.IssueId }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TestAttachmentDuplicateConflict, (await BodyOf(again)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, againstIssue.StatusCode);
        Assert.Equal(2, await AttachmentCountAsync(_fixture.WorkspaceA, test.RunId));
    }

    [Fact]
    public async Task A_malformed_request_is_refused_before_anything_is_looked_up()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId),
            new { versionNumber = 0, caption = new string('x', RecipePolicy.CaptionMaxLength + 1) },
            Ct);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestAttachmentInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("mediaAssetId", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("versionNumber", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("caption", out _));
    }

    [Fact]
    public async Task A_test_of_another_recipe_and_an_unknown_test_are_the_same_not_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var other = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);

        // The other recipe's run, addressed under this recipe.
        var misfiled = await client.PostAsJsonAsync(
            AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, other.RunId), new { mediaAssetId = asset }, Ct);
        var unknown = await client.PostAsJsonAsync(
            AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, Guid.NewGuid()), new { mediaAssetId = asset }, Ct);
        var refusal = await Refusal(unknown);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.StartsWith($"404|{RecipeErrorCodes.TestRunNotFound}|", refusal);
        Assert.Equal(refusal, await Refusal(misfiled));
        Assert.Equal(0, await AttachmentCountAsync(_fixture.WorkspaceA, other.RunId));
    }

    [Fact]
    public async Task An_archived_recipes_tests_take_no_new_pictures_but_still_list_the_ones_they_have()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        await AttachAsync(client, _fixture.WorkspaceA, test, asset);

        var recipe = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, test.RecipeId), Ct));
        var archived = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, test.RecipeId)}/archive",
            new { expectedConcurrencyToken = recipe.GetProperty("concurrencyToken").GetString() },
            Ct);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var attach = await AttachAsync(client, _fixture.WorkspaceA, test, await SeedAssetAsync(_fixture.WorkspaceA));
        var read = await client.GetAsync(AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId), Ct);

        Assert.Equal(HttpStatusCode.Conflict, attach.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, (await BodyOf(attach)).GetProperty("code").GetString());
        Assert.Single((await BodyOf(read)).EnumerateArray());
    }

    // ---- Detaching ----

    /// <summary>The restriction, for this role: detaching removes one row and nothing about the asset.</summary>
    [Fact]
    public async Task Detaching_removes_the_attachment_and_nothing_about_the_asset()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA, versions: 2, uses: 3);
        var keep = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, await SeedAssetAsync(_fixture.WorkspaceA)));
        var attached = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, asset));
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);

        var response = await client.SendAsync(
            HttpMethod.Delete, $"{path}/{attached.GetProperty("id").GetGuid()}", body: null, headers: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(attached.GetProperty("id").GetGuid(), (await BodyOf(response)).GetProperty("id").GetGuid());

        var listed = await BodyOf(await client.GetAsync(path, Ct));
        Assert.Equal(keep.GetProperty("id").GetGuid(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());

        var after = await AssetFactsAsync(_fixture.WorkspaceA, asset);
        Assert.True(after.Exists);
        Assert.Null(after.DeletedAt);
        Assert.Equal(2, after.Versions);
        Assert.Equal(3, after.Uses);
    }

    [Fact]
    public async Task Detaching_twice_without_a_key_is_a_not_found_the_second_time()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var other = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var attached = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, asset));
        var onOther = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, other, asset));
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);
        var target = $"{path}/{attached.GetProperty("id").GetGuid()}";

        await client.SendAsync(HttpMethod.Delete, target, body: null, headers: null, Ct);
        var second = await client.SendAsync(HttpMethod.Delete, target, body: null, headers: null, Ct);
        var anotherTests = await client.SendAsync(
            HttpMethod.Delete, $"{path}/{onOther.GetProperty("id").GetGuid()}", body: null, headers: null, Ct);
        var refusal = await Refusal(second);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.StartsWith($"404|{RecipeErrorCodes.TestAttachmentNotFound}|", refusal);

        // An attachment of another test is not this test's to remove, and is answered as an unknown id is.
        Assert.Equal(refusal, await Refusal(anotherTests));
        Assert.Equal(1, await AttachmentCountAsync(_fixture.WorkspaceA, other.RunId));
    }

    // ---- Replay ----

    [Fact]
    public async Task Replaying_an_attach_with_its_key_returns_the_first_attachment_and_attaches_once()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);

        var first = await client.PostAsJsonAsync(path, new { mediaAssetId = asset }, "attach-key-1", Ct);
        var replay = await client.PostAsJsonAsync(path, new { mediaAssetId = asset }, "attach-key-1", Ct);

        // Without the key this repeat would have been the duplicate conflict.
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await AttachmentCountAsync(_fixture.WorkspaceA, test.RunId));
    }

    [Fact]
    public async Task One_key_cannot_be_reused_for_a_different_attachment()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);

        await client.PostAsJsonAsync(path, new { mediaAssetId = await SeedAssetAsync(_fixture.WorkspaceA) }, "attach-key-2", Ct);
        var reused = await client.PostAsJsonAsync(
            path, new { mediaAssetId = await SeedAssetAsync(_fixture.WorkspaceA) }, "attach-key-2", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(reused)).GetProperty("code").GetString());
        Assert.Equal(1, await AttachmentCountAsync(_fixture.WorkspaceA, test.RunId));
    }

    [Fact]
    public async Task Replaying_a_detach_with_its_key_returns_the_first_answer_rather_than_a_not_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(client, _fixture.WorkspaceA);
        var attached = await BodyOf(await AttachAsync(client, _fixture.WorkspaceA, test, await SeedAssetAsync(_fixture.WorkspaceA)));
        var target = $"{AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId)}/{attached.GetProperty("id").GetGuid()}";
        var key = new Dictionary<string, string> { [IdempotencyPolicy.KeyHeader] = "detach-key-1" };

        var first = await client.SendAsync(HttpMethod.Delete, target, body: null, key, Ct);
        var replay = await client.SendAsync(HttpMethod.Delete, target, body: null, key, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Another_workspaces_asset_cannot_be_attached_and_is_not_disclosed()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var test = await SeedTestAsync(ownerA, _fixture.WorkspaceA);
        var assetInB = await SeedAssetAsync(_fixture.WorkspaceB);

        var forNeighbours = await client_Attach(ownerA, test, assetInB, versionNumber: 1);
        var forUnknown = await client_Attach(ownerA, test, Guid.NewGuid(), versionNumber: 1);

        // Named as the asset, never as its version: a version is only judged for an asset this workspace holds.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forNeighbours.StatusCode);
        Assert.Equal(await Refusal(forUnknown), await Refusal(forNeighbours));
        Assert.Equal(0, await AttachmentCountAsync(_fixture.WorkspaceA, test.RunId));

        Task<HttpResponseMessage> client_Attach(GatewayClient client, SeededTest seeded, Guid assetId, int versionNumber) =>
            client.PostAsJsonAsync(
                AttachmentsIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
                new { mediaAssetId = assetId, versionNumber },
                Ct);
    }

    [Fact]
    public async Task Another_workspaces_issue_cannot_be_illustrated_and_is_not_disclosed()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var test = await SeedTestAsync(ownerA, _fixture.WorkspaceA);
        var testInB = await SeedTestAsync(ownerB, _fixture.WorkspaceB);
        var asset = await SeedAssetAsync(_fixture.WorkspaceA);
        var path = AttachmentsIn(_fixture.WorkspaceA, test.RecipeId, test.RunId);

        var forNeighbours = await ownerA.PostAsJsonAsync(path, new { mediaAssetId = asset, testIssueId = testInB.IssueId }, Ct);
        var forUnknown = await ownerA.PostAsJsonAsync(path, new { mediaAssetId = asset, testIssueId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forNeighbours.StatusCode);
        Assert.Equal(await Refusal(forUnknown), await Refusal(forNeighbours));
        Assert.Equal(0, await AttachmentCountAsync(_fixture.WorkspaceA, test.RunId));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_attach_to_or_detach_from_the_others_test()
    {
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var testInB = await SeedTestAsync(ownerB, _fixture.WorkspaceB);
        var attached = await BodyOf(await AttachAsync(ownerB, _fixture.WorkspaceB, testInB, await SeedAssetAsync(_fixture.WorkspaceB)));
        var attachmentId = attached.GetProperty("id").GetGuid();

        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var assetInA = await SeedAssetAsync(_fixture.WorkspaceA);

        foreach (var workspace in (SeededWorkspace[])[_fixture.WorkspaceB, _fixture.WorkspaceA])
        {
            // Through B's slug A's owner is not a member; through A's slug the recipe and test are not A's.
            var path = AttachmentsIn(workspace, testInB.RecipeId, testInB.RunId);

            var read = await ownerA.GetAsync(path, Ct);
            var attach = await ownerA.PostAsJsonAsync(path, new { mediaAssetId = assetInA }, Ct);
            var detach = await ownerA.SendAsync(HttpMethod.Delete, $"{path}/{attachmentId}", body: null, headers: null, Ct);

            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, detach.StatusCode);
        }

        // B's test is exactly as B left it.
        var listed = await BodyOf(await ownerB.GetAsync(AttachmentsIn(_fixture.WorkspaceB, testInB.RecipeId, testInB.RunId), Ct));
        Assert.Equal(attachmentId, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
    }

    // ---- Helpers ----

    private sealed record SeededTest(Guid RecipeId, Guid RunId, Guid IssueId);

    private Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    /// <summary>A recipe and a test of its version 1 that raised one issue.</summary>
    private static async Task<SeededTest> SeedTestAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var recipe = await BodyOf(await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes", new { title = "Olive oil cake" }, Ct));
        var recipeId = recipe.GetProperty("recipeId").GetGuid();

        var created = await client.PostAsJsonAsync(
            $"{RecipeIn(workspace, recipeId)}/test-runs",
            new
            {
                sourceVersionNumber = 1,
                testedAt = "2026-09-20T18:00:00Z",
                outcome = "SucceededWithIssues",
                issues = new object[] { new { severity = "Major", title = "Crumb too dense" } },
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var run = await BodyOf(created);

        return new SeededTest(
            recipeId,
            run.GetProperty("testRunId").GetGuid(),
            run.GetProperty("issueIds").EnumerateArray().Single().GetGuid());
    }

    private static Task<HttpResponseMessage> AttachAsync(
        GatewayClient client, SeededWorkspace workspace, SeededTest test, Guid assetId) =>
        client.PostAsJsonAsync(AttachmentsIn(workspace, test.RecipeId, test.RunId), new { mediaAssetId = assetId }, Ct);

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
            Title = "Crumb close-up",
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
            asset.Versions.Add(new MediaAssetVersion
            {
                Id = Guid.NewGuid(),
                MediaAssetId = asset.Id,
                VersionNumber = number,
                MediaType = "image/jpeg",
                SizeBytes = 204_800,
                Width = 1600,
                Height = 1200,
                ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                ObjectKey = $"assets/{asset.Id:D}/{number}.jpg",
                Source = MediaAssetVersionSource.Upload,
                CreatedByMembershipId = actor,
                CreatedAt = Now,
            });
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

    private async Task<int> AttachmentCountAsync(SeededWorkspace workspace, Guid runId)
    {
        await using var scope = ScopeFor(workspace);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .TestAttachmentLinks.CountAsync(link => link.RecipeTestRunId == runId, Ct);
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
