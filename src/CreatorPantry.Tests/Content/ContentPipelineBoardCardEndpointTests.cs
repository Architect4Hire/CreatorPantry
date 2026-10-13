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
/// <c>.../creative-contexts/{contextId}/board-card</c> through the real Gateway (13.7a, PIPE-UI-006): the
/// Content Pipeline's last step, at each point a run can stop, and replayed.
/// </summary>
/// <remarks>
/// The commit writes one card and nothing else. A run's pictures and prompts are saved by the library's keep
/// route before it, so every test here also holds that the commit — failed, repeated or refused — leaves the
/// number of assets, prompts and references exactly where it found them.
/// </remarks>
public sealed class ContentPipelineBoardCardEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SaveFault _fault = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            // The real audit writer, behind a switch that makes the one save a commit performs fail.
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

    private static string BoardCardOf(SeededWorkspace workspace, Guid contextId) =>
        $"/api/v1/workspaces/{workspace.Slug}/creative-contexts/{contextId}/board-card";

    private static string CardsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/content-cards";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static Task<HttpResponseMessage> CommitAsync(GatewayClient client, SeededWorkspace workspace, Guid contextId) =>
        client.PostAsync(BoardCardOf(workspace, contextId), new ByteArrayContent([]), cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static Guid IdOf(JsonElement detail) => detail.GetProperty("card").GetProperty("id").GetGuid();

    private static JsonElement? Linked(JsonElement detail, string kind) =>
        detail.GetProperty("lineage").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == kind)
            .Select(entry => (JsonElement?)entry)
            .SingleOrDefault();

    // ---- The commit ----

    [Fact]
    public async Task A_finished_run_goes_on_the_board_as_one_card_linking_what_the_work_holds()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var detail = await BodyOf(response);
        Assert.EndsWith($"/content-cards/{IdOf(detail)}", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var card = detail.GetProperty("card");
        Assert.Equal("Soda bread, autumn", card.GetProperty("title").GetString());
        Assert.Equal("ToDo", card.GetProperty("column").GetString());

        // Every link is to a record the work already held. The picture is the first one kept.
        Assert.Equal(run.ContextId, Linked(detail, "CreativeContext")!.Value.GetProperty("id").GetGuid());
        var recipe = Linked(detail, "Recipe")!.Value;
        Assert.Equal(run.RecipeId, recipe.GetProperty("id").GetGuid());
        Assert.Equal(run.VersionId, recipe.GetProperty("versionId").GetGuid());
        var picture = Linked(detail, "MediaAsset")!.Value;
        Assert.Equal(run.AssetIds[0], picture.GetProperty("id").GetGuid());
        Assert.Equal(1, picture.GetProperty("versionNumber").GetInt32());
        Assert.Equal(run.PromptId, Linked(detail, "PromptRecord")!.Value.GetProperty("id").GetGuid());
        Assert.Equal(run.SocialPackageId, Linked(detail, "SocialPackage")!.Value.GetProperty("id").GetGuid());

        // Its creation is its one history entry, and it is no recipe package.
        Assert.Equal(1, detail.GetProperty("history").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("package").ValueKind);

        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(Ct));
        Assert.Equal(ContentCardOrigin.ContentPipeline, stored.Origin);
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);
        Assert.Null(stored.PlannedDate);

        // Nothing but the card was written: no asset, no prompt, no reference.
        Assert.Equal(run.Sources, await SourceCountsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task The_audit_entry_says_a_run_was_put_on_the_board_and_none_of_the_creators_words()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = IdOf(await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId)));

        var entry = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.ResourceType == ContentAuditActions.ContentCardResourceType, Ct));

        Assert.Equal(ContentAuditActions.ContentCardCreated, entry.Action);
        Assert.Equal(id.ToString("D"), entry.ResourceId);
        Assert.Contains("Content Pipeline", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Soda bread", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Replay ----

    [Fact]
    public async Task Sending_it_again_answers_the_same_card_and_writes_nothing()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        var second = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        var third = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var id = IdOf(await BodyOf(first));
        var replayed = await BodyOf(second);
        Assert.Equal(id, IdOf(replayed));
        Assert.Equal(id, IdOf(await BodyOf(third)));

        // The replay is the card as it stands, in the same shape, not a thinner acknowledgement.
        Assert.Equal(run.AssetIds[0], Linked(replayed, "MediaAsset")!.Value.GetProperty("id").GetGuid());

        // One card, one history row, one audit entry — and the same sources as before any of it.
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
        Assert.Equal(run.Sources, await SourceCountsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_commit_whose_answer_never_arrived_is_found_by_the_read_and_not_repeated()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // Before: the work has no card, which is what a resumed run reads as "not on the board yet".
        var before = await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct);
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(ContentErrorCodes.CardNotFound, Code(await BodyOf(before)));

        // The commit lands and its response is thrown away, as a dropped connection would.
        (await CommitAsync(client, _fixture.WorkspaceA, run.ContextId)).Dispose();

        // The resumed run asks, and is told. A viewer may ask too.
        using var viewer = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var read = await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        var id = IdOf(await BodyOf(read));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct)).StatusCode);

        // And a client that retries without asking gets that card rather than a second one.
        var retried = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(id, IdOf(await BodyOf(retried)));
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_replay_still_answers_after_the_work_has_been_archived()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = IdOf(await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId)));

        // The work is archived afterwards. A fresh commit would be refused; a repeat of one that happened is not.
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.CreativeContexts.SingleAsync(item => item.Id == run.ContextId, Ct)).ArchivedAt = Now;
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var replayed = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(id, IdOf(await BodyOf(replayed)));
    }

    // ---- Failure points ----

    [Fact]
    public async Task A_run_that_stopped_before_any_picture_was_saved_is_refused_and_resumes_once_one_is()
    {
        // The creator kept a picture and never saved it: the work names the staged picture and no asset.
        var run = await SeedRunAsync(_fixture.WorkspaceA, savedPictures: 0, unsavedKeepers: 1);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var refused = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await BodyOf(refused);
        Assert.Equal(ContentErrorCodes.CardPipelineUnprocessable, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("keepers", out _));
        await AssertNoCardAsync(_fixture.WorkspaceA);

        // The save happens — the keep route's own work, stood in for here — and the same request now succeeds.
        var assetId = await SaveKeeperAsync(_fixture.WorkspaceA, run.ContextId);
        var resumed = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, resumed.StatusCode);
        Assert.Equal(assetId, Linked(await BodyOf(resumed), "MediaAsset")!.Value.GetProperty("id").GetGuid());
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_run_with_pictures_still_to_save_goes_on_the_board_and_saving_the_rest_makes_no_second_card()
    {
        // Two kept, one saved: the save of the second failed, or has not been asked for yet.
        var run = await SeedRunAsync(_fixture.WorkspaceA, savedPictures: 1, unsavedKeepers: 1);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = IdOf(await BodyOf(first));

        // The second picture is saved afterwards and the run is finished again.
        await SaveKeeperAsync(_fixture.WorkspaceA, run.ContextId);
        var again = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var detail = await BodyOf(again);
        Assert.Equal(id, IdOf(detail));

        // The card is the creator's from the moment it exists: a later commit does not re-point it.
        Assert.Equal(run.AssetIds[0], Linked(detail, "MediaAsset")!.Value.GetProperty("id").GetGuid());
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_write_that_fails_leaves_no_card_no_history_and_no_audit_entry_and_the_retry_makes_exactly_one()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // The audit entry is the last row staged, and it is made unstorable: the card and its creation row are
        // already in the same save, so if either could commit alone this is where it would show.
        _fault.Arm();
        var failed = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        _fault.Disarm();

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(ContentErrorCodes.CardNotSaved, Code(await BodyOf(failed)));
        await AssertNoCardAsync(_fixture.WorkspaceA);

        // What was saved before the failure is untouched by it: there is nothing to compensate.
        Assert.Equal(run.Sources, await SourceCountsAsync(_fixture.WorkspaceA));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct)).StatusCode);

        // The same request, sent again, makes the card — once, however often it is then repeated.
        var retried = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        var repeated = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(IdOf(await BodyOf(retried)), IdOf(await BodyOf(repeated)));
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
        Assert.Equal(run.Sources, await SourceCountsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_failure_on_a_repeat_cannot_harm_the_card_that_already_exists()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = IdOf(await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId)));

        // A repeat writes nothing, so a store that would refuse a write is never asked.
        _fault.Arm();
        var repeated = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        _fault.Disarm();

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(id, IdOf(await BodyOf(repeated)));
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
    }

    // ---- What the card links, and what it leaves off ----

    [Fact]
    public async Task A_picture_the_library_no_longer_holds_is_passed_over_for_the_next_one_kept()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA, savedPictures: 2);
        await RemoveAssetAsync(_fixture.WorkspaceA, run.AssetIds[0]);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(run.AssetIds[1], Linked(await BodyOf(response), "MediaAsset")!.Value.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_run_whose_every_saved_picture_has_been_removed_has_nothing_to_put_on_the_board()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        await RemoveAssetAsync(_fixture.WorkspaceA, run.AssetIds[0]);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CardPipelineUnprocessable, Code(await BodyOf(response)));
        await AssertNoCardAsync(_fixture.WorkspaceA);
    }

    [Fact]
    public async Task A_run_about_no_recipe_with_no_posts_still_goes_on_the_board_and_links_less()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA, recipe: false, posts: PostState.None, prompt: false);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await BodyOf(response);
        Assert.Equal(
            ["CreativeContext", "MediaAsset"],
            detail.GetProperty("lineage").EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()).Order());
    }

    [Fact]
    public async Task Posts_that_are_not_accepted_and_a_recipe_that_is_archived_are_left_off_rather_than_refusing_the_card()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA, posts: PostState.NeedsReview);
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.Recipes.SingleAsync(item => item.Id == run.RecipeId, Ct)).Status = RecipeStatus.Archived;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await BodyOf(response);
        Assert.Null(Linked(detail, "Recipe"));
        Assert.Null(Linked(detail, "SocialPackage"));
        Assert.NotNull(Linked(detail, "MediaAsset"));
    }

    [Theory]
    [InlineData(null, true, "Buttermilk Soda Bread")]
    [InlineData(null, false, "Content Pipeline run")]
    public async Task The_title_is_the_works_own_or_the_recipes_and_never_made_up(string? workingTitle, bool recipe, string expected)
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA, title: workingTitle, recipe: recipe);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var detail = await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId));

        Assert.Equal(expected, detail.GetProperty("card").GetProperty("title").GetString());
    }

    // ---- One per piece of work, and only the pipeline's ----

    [Fact]
    public async Task A_card_made_by_hand_for_the_same_work_is_not_the_pipelines_card()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var byHand = IdOf(await BodyOf(await client.PostAsJsonAsync(
            CardsIn(_fixture.WorkspaceA), new { title = "My own plan", creativeContextId = run.ContextId }, Ct)));

        // The work has a card, but not the pipeline's: the read says so, and the commit makes one.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct)).StatusCode);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = IdOf(await BodyOf(response));
        Assert.NotEqual(byHand, id);
        Assert.Equal(id, IdOf(await BodyOf(await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct))));

        // And a creator may go on adding their own afterwards.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            CardsIn(_fixture.WorkspaceA), new { title = "Another of mine", creativeContextId = run.ContextId }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_pipeline_card_pointed_at_other_work_stops_being_this_works_card()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        var other = await SeedRunAsync(_fixture.WorkspaceA, recipe: false, posts: PostState.None, prompt: false);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = (await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId))).GetProperty("card");

        var moved = await client.PatchAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{first.GetProperty("id").GetGuid()}",
            new Dictionary<string, object?>
            {
                ["creativeContextId"] = other.ContextId,
                ["socialPackageId"] = null,
                ["expectedConcurrencyToken"] = first.GetProperty("concurrencyToken").GetString(),
            },
            Ct);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        // It is an ordinary card now, about the other work — and it does not take that work's place either.
        Assert.Equal(
            ContentCardOrigin.Board,
            await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().Select(card => card.Origin).SingleAsync(Ct)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, run.ContextId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BoardCardOf(_fixture.WorkspaceA, other.ContextId), Ct)).StatusCode);

        var again = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(first.GetProperty("id").GetGuid(), IdOf(await BodyOf(again)));
    }

    [Fact]
    public async Task A_pipeline_card_unlinked_from_its_work_becomes_an_ordinary_card_and_frees_the_work()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = (await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId))).GetProperty("card");

        var unlinked = await client.PatchAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{first.GetProperty("id").GetGuid()}",
            new Dictionary<string, object?>
            {
                ["creativeContextId"] = null,
                ["expectedConcurrencyToken"] = first.GetProperty("concurrencyToken").GetString(),
            },
            Ct);

        // The store would refuse a pipeline card for no piece of work; the edit is accepted because the card
        // stops being one in the same write.
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        var stored = await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().SingleAsync(Ct));
        Assert.Equal((ContentCardOrigin.Board, (Guid?)null), (stored.Origin, stored.CreativeContextId));

        Assert.Equal(HttpStatusCode.Created, (await CommitAsync(client, _fixture.WorkspaceA, run.ContextId)).StatusCode);
    }

    [Fact]
    public async Task Editing_anything_else_about_a_pipeline_card_leaves_it_the_works_card()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = (await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId))).GetProperty("card");
        var id = first.GetProperty("id").GetGuid();

        // A new title, and the same piece of work named again: neither is a change of work.
        var edited = await client.PatchAsJsonAsync(
            $"{CardsIn(_fixture.WorkspaceA)}/{id}",
            new Dictionary<string, object?>
            {
                ["title"] = "Soda bread, for Friday",
                ["creativeContextId"] = run.ContextId,
                ["expectedConcurrencyToken"] = first.GetProperty("concurrencyToken").GetString(),
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(
            ContentCardOrigin.ContentPipeline,
            await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.AsNoTracking().Select(card => card.Origin).SingleAsync(Ct)));

        // So the commit still answers with it, under the creator's new title.
        var repeated = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var detail = await BodyOf(repeated);
        Assert.Equal(id, IdOf(detail));
        Assert.Equal("Soda bread, for Friday", detail.GetProperty("card").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Deleting_the_card_frees_the_work_to_be_put_on_the_board_again()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = IdOf(await BodyOf(await CommitAsync(client, _fixture.WorkspaceA, run.ContextId)));

        await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.Where(card => card.Id == first).ExecuteDeleteAsync(Ct));

        var again = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(first, IdOf(await BodyOf(again)));
        Assert.Equal(1, await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.CountAsync(Ct)));
    }

    // ---- Who may ----

    [Fact]
    public async Task A_viewer_may_not_commit_and_an_editor_may()
    {
        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        var theirs = await SeedRunAsync(_fixture.WorkspaceB);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var refused = await CommitAsync(viewer, _fixture.WorkspaceB, theirs.ContextId);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await AssertNoCardAsync(_fixture.WorkspaceB);

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);
        var ours = await SeedRunAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        Assert.Equal(HttpStatusCode.Created, (await CommitAsync(editor, _fixture.WorkspaceA, ours.ContextId)).StatusCode);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Another_workspaces_work_answers_exactly_as_work_that_does_not_exist()
    {
        var theirs = await SeedRunAsync(_fixture.WorkspaceB);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // A, on its own route, naming B's finished run; and naming work nobody has.
        var foreign = await CommitAsync(inA, _fixture.WorkspaceA, theirs.ContextId);
        var unknown = await CommitAsync(inA, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await AssertSameRefusalAsync(foreign, unknown, ContentErrorCodes.CreativeContextNotFound);

        // Nothing was made anywhere, and B's run is untouched.
        await AssertNoCardAsync(_fixture.WorkspaceA);
        await AssertNoCardAsync(_fixture.WorkspaceB);
        Assert.Equal(theirs.Sources, await SourceCountsAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task One_workspace_cannot_read_whether_anothers_work_is_on_the_board()
    {
        var theirs = await SeedRunAsync(_fixture.WorkspaceB);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        Assert.Equal(HttpStatusCode.Created, (await CommitAsync(inB, _fixture.WorkspaceB, theirs.ContextId)).StatusCode);

        // B's work is on B's board. From A it reads as it would if it were not there, or were nobody's.
        var foreign = await inA.GetAsync(BoardCardOf(_fixture.WorkspaceA, theirs.ContextId), Ct);
        var unknown = await inA.GetAsync(BoardCardOf(_fixture.WorkspaceA, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await AssertSameRefusalAsync(foreign, unknown, ContentErrorCodes.CardNotFound);

        // And a commit from A against it is refused without touching B's card.
        Assert.Equal(HttpStatusCode.NotFound, (await CommitAsync(inA, _fixture.WorkspaceA, theirs.ContextId)).StatusCode);
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceB));
        await AssertNoCardAsync(_fixture.WorkspaceA);
    }

    [Fact]
    public async Task Each_workspace_gets_its_own_card_for_its_own_run()
    {
        var ours = await SeedRunAsync(_fixture.WorkspaceA);
        var theirs = await SeedRunAsync(_fixture.WorkspaceB);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var a = await BodyOf(await CommitAsync(inA, _fixture.WorkspaceA, ours.ContextId));
        var b = await BodyOf(await CommitAsync(inB, _fixture.WorkspaceB, theirs.ContextId));

        Assert.NotEqual(IdOf(a), IdOf(b));
        Assert.Equal(ours.AssetIds[0], Linked(a, "MediaAsset")!.Value.GetProperty("id").GetGuid());
        Assert.Equal(theirs.AssetIds[0], Linked(b, "MediaAsset")!.Value.GetProperty("id").GetGuid());
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceA));
        Assert.Equal((1, 1, 1), await WrittenAsync(_fixture.WorkspaceB));

        // Neither board lists the other's card.
        var listed = await BodyOf(await inA.GetAsync(CardsIn(_fixture.WorkspaceA), Ct));
        Assert.Equal([IdOf(a)], listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Archived_work_cannot_be_put_on_the_board()
    {
        var run = await SeedRunAsync(_fixture.WorkspaceA);
        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            (await db.CreativeContexts.SingleAsync(item => item.Id == run.ContextId, Ct)).ArchivedAt = Now;
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await CommitAsync(client, _fixture.WorkspaceA, run.ContextId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextNotFound, Code(await BodyOf(response)));
        await AssertNoCardAsync(_fixture.WorkspaceA);
    }

    // ---- Helpers ----

    private enum PostState
    {
        None,
        Accepted,
        NeedsReview,
    }

    /// <summary>One run: its piece of work, what that names, and how many source rows the workspace then holds.</summary>
    private sealed record Run(
        Guid ContextId,
        Guid? RecipeId,
        Guid? VersionId,
        IReadOnlyList<Guid> AssetIds,
        Guid? PromptId,
        Guid? SocialPackageId,
        (int Assets, int AssetVersions, int Prompts, int References, int Recipes, int Posts) Sources);

    /// <summary>
    /// A run as the pipeline leaves it before its last step: a piece of work naming its recipe, the pictures
    /// it kept — some saved to the library, some not — the prompt kept with the first, and its posts.
    /// </summary>
    private async Task<Run> SeedRunAsync(
        SeededWorkspace workspace,
        int savedPictures = 1,
        int unsavedKeepers = 0,
        bool recipe = true,
        PostState posts = PostState.Accepted,
        bool prompt = true,
        string? title = "Soda bread, autumn")
    {
        var run = await InWorkspaceAsync(workspace, async db =>
        {
            var author = Guid.NewGuid();
            var context = CreativeContextSeeds.NewContext(Now, title);
            db.Add(context);
            await db.SaveChangesAsync(Ct);

            var order = 0;
            Guid? recipeId = null;
            Guid? versionId = null;
            if (recipe)
            {
                var row = new Recipe
                {
                    Id = Guid.NewGuid(), Title = "Buttermilk Soda Bread", Status = RecipeStatus.Approved,
                    CreatedByMembershipId = author, UpdatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now,
                };
                var version = NewVersion(row, author);
                db.AddRange(row, version);
                await db.SaveChangesAsync(Ct);

                var named = CreativeContextSeeds.Reference(context, CreativeContextReferenceKind.Recipe, order++, Now);
                named.RecipeId = row.Id;
                named.RecipeVersionId = version.Id;
                db.Add(named);
                (recipeId, versionId) = (row.Id, version.Id);
            }

            for (var index = 0; index < unsavedKeepers; index++)
            {
                var staged = CreativeContextSeeds.Reference(context, CreativeContextReferenceKind.GeneratedImage, order++, Now);
                staged.Purpose = CreativeContextReferencePurpose.Keeper;
                staged.GeneratedImageId = await CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct);
                db.Add(staged);
            }

            var assetIds = new List<Guid>();
            for (var index = 0; index < savedPictures; index++)
            {
                var assetId = await CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now, Ct);
                var kept = CreativeContextSeeds.Reference(context, CreativeContextReferenceKind.DamAsset, order++, Now);
                kept.Purpose = CreativeContextReferencePurpose.Keeper;
                kept.MediaAssetId = assetId;
                kept.MediaAssetVersionNumber = 1;
                db.Add(kept);
                assetIds.Add(assetId);
            }

            await db.SaveChangesAsync(Ct);

            Guid? promptId = null;
            if (prompt && assetIds.Count > 0)
            {
                var record = new PromptRecord
                {
                    Id = Guid.NewGuid(), ChannelKey = "instagram", ImageKind = PromptImageKind.Hero,
                    Text = "Overhead shot of soda bread on linen, soft window light.", Source = PromptRecordSource.Manual,
                    DamAssetId = assetIds[0], CreatedByMembershipId = author, CreatedAt = Now,
                };
                db.Add(record);
                promptId = record.Id;
            }

            Guid? socialPackageId = null;
            if (posts is not PostState.None)
            {
                var social = new SocialPackage { Id = Guid.NewGuid(), CreativeContextId = context.Id, CreatedByMembershipId = author, CreatedAt = Now, UpdatedAt = Now };
                var channel = new SocialPackageChannel
                {
                    Id = Guid.NewGuid(), SocialPackageId = social.Id, ChannelKey = "instagram", Status = ContentProposalStatus.Proposed, CreatedAt = Now, UpdatedAt = Now,
                };
                var post = new SocialRevision
                {
                    Id = Guid.NewGuid(), SocialPackageChannelId = channel.Id, RevisionNumber = 1, Source = ContentRevisionSource.CreatorEdit,
                    Body = "Post words.", CreatedByMembershipId = author, CreatedAt = Now,
                };
                db.AddRange(social, channel, post);
                await db.SaveChangesAsync(Ct);

                channel.Status = posts is PostState.Accepted ? ContentProposalStatus.Accepted : ContentProposalStatus.NeedsReview;
                channel.AcceptedRevisionId = post.Id;
                channel.AcceptedAt = Now;
                if (posts is PostState.NeedsReview)
                {
                    channel.StaleSince = Now;
                    channel.StaleReasons = ContentStaleReasons.BrandChanged;
                }

                socialPackageId = social.Id;
            }

            await db.SaveChangesAsync(Ct);

            return new Run(context.Id, recipeId, versionId, assetIds, promptId, socialPackageId, default);
        });

        return run with { Sources = await SourceCountsAsync(workspace) };
    }

    /// <summary>
    /// What the keep route does for one picture, without the bytes: a library asset, and the work's pointer to
    /// it as a keeper. Returns the asset.
    /// </summary>
    private Task<Guid> SaveKeeperAsync(SeededWorkspace workspace, Guid contextId) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var assetId = await CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now, Ct);
            var next = await db.CreativeContextReferences.Where(item => item.CreativeContextId == contextId).MaxAsync(item => item.SortOrder, Ct) + 1;

            db.Add(new CreativeContextReference
            {
                Id = Guid.NewGuid(), CreativeContextId = contextId, Kind = CreativeContextReferenceKind.DamAsset,
                Purpose = CreativeContextReferencePurpose.Keeper, SortOrder = next, MediaAssetId = assetId,
                MediaAssetVersionNumber = 1, AddedAt = Now,
            });
            await db.SaveChangesAsync(Ct);

            return assetId;
        });

    /// <summary>Removes a picture from the library the way the library does: a tombstone, not a deleted row.</summary>
    private Task RemoveAssetAsync(SeededWorkspace workspace, Guid assetId) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now;
            asset.DeletedByMembershipId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private static RecipeVersion NewVersion(Recipe recipe, Guid author)
    {
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(), RecipeId = recipe.Id, VersionNumber = 1, Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Ready, CreatedByMembershipId = author, CreatedAt = Now,
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

    /// <summary>Two refusals that must not be tellable apart: the same status, code, title and detail.</summary>
    private static async Task AssertSameRefusalAsync(HttpResponseMessage one, HttpResponseMessage other, string code)
    {
        var first = await BodyOf(one);
        var second = await BodyOf(other);

        Assert.Equal(code, Code(first));
        Assert.Equal(Code(first), Code(second));
        Assert.Equal(first.GetProperty("title").GetString(), second.GetProperty("title").GetString());
        Assert.Equal(first.GetProperty("status").GetInt32(), second.GetProperty("status").GetInt32());
        Assert.Equal(
            first.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            second.TryGetProperty("detail", out var otherDetail) ? otherDetail.GetString() : null);
    }

    /// <summary>The rows a commit must never add to: what the keep route saved, and what the work names.</summary>
    private Task<(int Assets, int AssetVersions, int Prompts, int References, int Recipes, int Posts)> SourceCountsAsync(
        SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db => (
            await db.MediaAssets.CountAsync(Ct),
            await db.MediaAssetVersions.CountAsync(Ct),
            await db.PromptRecords.CountAsync(Ct),
            await db.CreativeContextReferences.CountAsync(Ct),
            await db.Recipes.CountAsync(Ct),
            await db.SocialRevisions.CountAsync(Ct)));

    private async Task AssertNoCardAsync(SeededWorkspace workspace) =>
        Assert.Equal((0, 0, 0), await WrittenAsync(workspace));

    /// <summary>Cards, history rows and card audit entries in one workspace.</summary>
    private Task<(int Cards, int Moves, int AuditEntries)> WrittenAsync(SeededWorkspace workspace) =>
        InWorkspaceAsync(workspace, async db => (
            await db.ContentCards.CountAsync(Ct),
            await db.CardMoveHistory.CountAsync(Ct),
            await db.AuditLogs.CountAsync(log => log.ResourceType == ContentAuditActions.ContentCardResourceType, Ct)));

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>A switch a test arms to make the next commit's save fail.</summary>
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
