using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Content;

#pragma warning disable MEAI001

/// <summary>
/// The connected journey walked end to end over HTTP (AF.8.1): a creator asks for recipe concepts, turns the
/// one they chose into a recipe, opens a piece of work from that recipe on a themed day, plans and composes a
/// picture for it, generates it, keeps it in the library, has the posts written, accepts them channel by
/// channel — and the day remembers all of it. A neighbouring workspace tries every step against the same ids
/// and reaches none of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <see cref="CreativePipelineEndToEndTests"/> proves the picture half of
/// this in a row. Nothing before this ran a model-backed step and the step that consumes its answer through
/// their routes, so an id one capability hands the next in the wrong shape — a concept id, a proposal id, a
/// revision id — would have passed every per-feature test.
/// </para>
/// <para>
/// <strong>What is real and what is not.</strong> Real: the Gateway, the API, the database, every facade,
/// every task handler and its output validator, the rendition job, and both workers' own passes. Stubbed: the
/// chat model (scripted, one known-good answer per task), the image provider, the object store (in memory)
/// and the malware scanner.
/// </para>
/// <para>
/// <strong>What has no route, and is run by hand.</strong> The two queue drivers and the outbox dispatcher
/// belong to the Worker host; the API host has none, by design, so a request can never generate inside
/// itself. Each is resolved from the host and run once where the journey needs it. The five tasks are
/// switched on through the host's own options object, as a deployment's configuration would.
/// </para>
/// </remarks>
public sealed class CreatorJourneyEndToEndTests : IAsyncLifetime
{
    private const string ThemeKey = "meat-free-monday";

    /// <summary>What the neighbour is making for themselves, in words that appear nowhere in our journey.</summary>
    private const string TheirDish = "barley broth";

    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly FakeImageGenerator _generator = new();
    private readonly ScriptedChatClient _chat = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator>(_generator);
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(_chat);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());

            // The queue drivers the Worker host adds and the API host deliberately does not.
            services.AddAiOperationWorker();
            services.AddGeneratedImageWorker();
        });

        var tasks = _fixture.Api.Factory.Services.GetRequiredService<AiTaskOptions>();
        foreach (var task in (string[])
        [
            AiTaskCatalog.RecipeConcepts,
            AiTaskCatalog.RecipeFirstDraft,
            AiTaskCatalog.PhotographyConcept,
            AiTaskCatalog.ImagePrompt,
            AiTaskCatalog.ChannelPosts,
        ])
        {
            tasks.Enabled.Add(task);
        }

        // A picture with detail in it, so its renditions are honestly smaller than it is. The provider
        // double's default is a few pixels across, which the rendition job rightly declines to shrink.
        _generator.Returns(Picture(width: 640, height: 480));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    [Fact]
    public async Task A_concept_becomes_a_recipe_a_picture_and_accepted_posts_and_the_neighbour_reaches_none_of_it()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);

        // 1. A themed day, with the notes every later generation is meant to follow.
        var themed = await ours.SendAsync(
            HttpMethod.Put,
            In(A, "weekly-themes"),
            new
            {
                themes = new[]
                {
                    new { day = "Monday", key = ThemeKey, displayName = "Meat-Free Monday", standingNotes = "Never tofu." },
                },
            },
            headers: null,
            Ct);
        Assert.Equal(HttpStatusCode.OK, themed.StatusCode);

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"weekly-themes/{ThemeKey}/history"), Ct), "the day's history");

        // 2. Ask for concepts. Accepted and queued; the worker's pass is what calls the model.
        var conceptRequestId = await RequestAsync(ours, A, "recipe-concept-requests", new { cuisine = "Sichuan", course = "dinner" });
        Assert.Equal(0, _chat.Calls);
        await RunAiPassAsync();

        var concepts = await ProposalAsync(ours, A, $"recipe-concept-requests/{conceptRequestId}");
        var conceptId = Added(concepts, "RecipeConcept").First().GetProperty("targetId").GetGuid();

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"recipe-concept-requests/{conceptRequestId}"), Ct), "the concept request");

        // 3. Draft the chosen concept, named by the two ids the concept request published.
        var chosen = new { sourceConceptRequestId = conceptRequestId, sourceConceptId = conceptId };

        var stolenConcept = await theirs.PostAsJsonAsync(In(B, "recipe-draft-requests"), chosen, Key(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, stolenConcept.StatusCode);
        Assert.Equal(AiFirstDraftRequestErrors.ConceptNotFound, await CodeOf(stolenConcept));

        var draftRequestId = await RequestAsync(ours, A, "recipe-draft-requests", chosen);
        await RunAiPassAsync();

        var draft = await ProposalAsync(ours, A, $"recipe-draft-requests/{draftRequestId}");
        var acceptance = new
        {
            decision = "AcceptAll",
            acceptedChangeIds = draft.GetProperty("changes").EnumerateArray()
                .Select(change => change.GetProperty("changeId").GetGuid())
                .ToArray(),
        };

        // 4. The neighbour tries to accept the draft first, while it is still undecided. Nothing is created.
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"recipe-draft-requests/{draftRequestId}"), Ct), "the draft request");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"recipe-draft-requests/{draftRequestId}/acceptance"), acceptance, Ct),
            "accepting the draft");
        Assert.Empty(await ItemsAsync(theirs, B, "recipes"));

        var accepted = await ours.PostAsJsonAsync(In(A, $"recipe-draft-requests/{draftRequestId}/acceptance"), acceptance, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await BodyOf(accepted);
        var recipeId = acceptedBody.GetProperty("recipeId").GetGuid();
        Assert.Equal(1, acceptedBody.GetProperty("recipeVersionNumber").GetInt32());

        var recipe = await BodyOf(await ours.GetAsync(In(A, $"recipes/{recipeId}"), Ct));
        Assert.Equal("Weeknight Mapo Tofu", recipe.GetProperty("title").GetString());

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"recipes/{recipeId}"), Ct), "the recipe");

        // 5. A piece of work from that recipe, on the themed day, for two channels.
        var opened = await ours.PostAsJsonAsync(
            In(A, "creative-contexts"),
            new
            {
                workingTitle = "Mapo tofu, Monday",
                channelKeys = new[] { "instagram", "pinterest" },
                day = "Monday",
                weeklyThemeKey = ThemeKey,
                from = new { kind = "Recipe", recipeId },
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var context = await BodyOf(opened);
        var contextId = context.GetProperty("id").GetGuid();
        Assert.Equal(recipeId, Assert.Single(context.GetProperty("references").EnumerateArray()).GetProperty("recipeId").GetGuid());

        var fromTheirRecipe = await theirs.PostAsJsonAsync(
            In(B, "creative-contexts"), new { workingTitle = "Borrowed", from = new { kind = "Recipe", recipeId } }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fromTheirRecipe.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, await CodeOf(fromTheirRecipe));

        var onTheirDay = await theirs.PostAsJsonAsync(
            In(B, "creative-contexts"), new { workingTitle = "Borrowed", weeklyThemeKey = ThemeKey }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onTheirDay.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextThemeUnprocessable, await CodeOf(onTheirDay));

        var token = context.GetProperty("concurrencyToken").GetString();
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"creative-contexts/{contextId}"), Ct), "the piece of work");
        await AssertNotFoundAsync(
            theirs.PatchAsJsonAsync(
                In(B, $"creative-contexts/{contextId}"), new { expectedConcurrencyToken = token, workingTitle = "Theirs now" }, Ct),
            "editing the piece of work");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(
                In(B, $"creative-contexts/{contextId}/references"),
                new { expectedConcurrencyToken = token, reference = new { kind = "Recipe", recipeId } },
                Ct),
            "adding to the piece of work");

        // 6. Plan the shoot for that recipe and that piece of work.
        foreach (var crossing in (object[])[new { recipeId }, new { channelKey = "instagram", creativeContextId = contextId }])
        {
            await AssertNotFoundAsync(
                theirs.PostAsJsonAsync(In(B, "photography-concept-requests"), crossing, Key(), Ct), "planning a shoot");
        }

        var shootRequestId = await RequestAsync(
            ours, A, "photography-concept-requests", new { channelKey = "instagram", recipeId, creativeContextId = contextId });

        // The neighbour has work of their own in the queue for this pass. One worker serves both, and each is
        // answered from its own workspace's material.
        var theirShootRequestId = await RequestAsync(theirs, B, "photography-concept-requests", new { dishName = TheirDish });
        await RunAiPassAsync(operations: 2);

        Assert.Equal("Proposed", (await BodyOf(await theirs.GetAsync(In(B, $"photography-concept-requests/{theirShootRequestId}"), Ct)))
            .GetProperty("status").GetString());
        await AssertNotFoundAsync(ours.GetAsync(In(A, $"photography-concept-requests/{theirShootRequestId}"), Ct), "their shoot plan, to us");

        var shoot = await ProposalAsync(ours, A, $"photography-concept-requests/{shootRequestId}");
        var shootConceptId = Added(shoot, "PhotographyConcept").First().GetProperty("targetId").GetGuid();

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"photography-concept-requests/{shootRequestId}"), Ct), "the shoot plan");

        // 7. Compose the prompt for that concept's hero shot, with the same recipe and piece of work.
        var composing = new
        {
            conceptRequestId = shootRequestId,
            conceptId = shootConceptId,
            shotKind = "Hero",
            channelKey = "instagram",
            recipeId,
            creativeContextId = contextId,
        };

        var stolenShoot = await theirs.PostAsJsonAsync(
            In(B, "image-prompt-requests"),
            new { conceptRequestId = shootRequestId, conceptId = shootConceptId, shotKind = "Hero" },
            Key(),
            Ct);
        Assert.Equal(HttpStatusCode.NotFound, stolenShoot.StatusCode);
        Assert.Equal(AiImagePromptRequestErrors.ConceptNotFound, await CodeOf(stolenShoot));

        var promptRequestId = await RequestAsync(ours, A, "image-prompt-requests", composing);
        await RunAiPassAsync();

        var composed = await ProposalAsync(ours, A, $"image-prompt-requests/{promptRequestId}");
        var promptProposalId = composed.GetProperty("proposalId").GetGuid();
        var promptText = Added(composed, "ImagePrompt").Single().GetProperty("afterValue").GetString()!;

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"image-prompt-requests/{promptRequestId}"), Ct), "the prompt request");

        // 8. Generate from that prompt, naming the proposal it came from. Queued, not generated.
        var generate = new { promptText, aiProposalId = promptProposalId, variantCount = 2, recipeId, creativeContextId = contextId };

        var stolenPrompt = await theirs.PostAsJsonAsync(In(B, "generated-images"), generate, Key(), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stolenPrompt.StatusCode);
        Assert.Equal(MediaErrorCodes.GenerationProposalUnprocessable, await CodeOf(stolenPrompt));

        // Each thing the run says it is for is refused on its own, too: a prompt of the neighbour's own cannot
        // be generated for our recipe or our piece of work.
        var forOurRecipe = await theirs.PostAsJsonAsync(
            In(B, "generated-images"), new { promptText = "Theirs.", variantCount = 1, recipeId }, Key(), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forOurRecipe.StatusCode);
        Assert.Equal(MediaErrorCodes.GenerationRecipeUnprocessable, await CodeOf(forOurRecipe));

        var forOurWork = await theirs.PostAsJsonAsync(
            In(B, "generated-images"), new { promptText = "Theirs.", variantCount = 1, creativeContextId = contextId }, Key(), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forOurWork.StatusCode);
        Assert.Equal(MediaErrorCodes.GenerationContextUnprocessable, await CodeOf(forOurWork));

        var requested = await ours.PostAsJsonAsync(In(A, "generated-images"), generate, Key(), Ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = (await BodyOf(requested)).GetProperty("id").GetGuid();
        Assert.Equal(0, _generator.Calls);

        // 9. The image worker's pass, with a run of the neighbour's queued beside ours. Each run's pictures
        // are made from its own prompt and staged in its own workspace.
        var theirRun = await theirs.PostAsJsonAsync(
            In(B, "generated-images"), new { promptText = $"A bowl of {TheirDish} on slate.", variantCount = 1 }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Accepted, theirRun.StatusCode);
        var theirOperationId = (await BodyOf(theirRun)).GetProperty("id").GetGuid();

        var pass = await RunImagePassAsync();
        Assert.Equal(2, pass.Claimed);
        Assert.Equal(2, pass.Generated);
        Assert.Equal(3, _generator.Calls);
        Assert.Equal(2, _generator.Prompts.Count(sent => sent.Contains(promptText, StringComparison.Ordinal)));
        Assert.DoesNotContain(
            _generator.Prompts,
            sent => sent.Contains(promptText, StringComparison.Ordinal) && sent.Contains(TheirDish, StringComparison.Ordinal));

        var theirStaged = (await BodyOf(await theirs.GetAsync(In(B, $"generated-images/operations/{theirOperationId}"), Ct)))
            .GetProperty("images").EnumerateArray().Select(image => image.GetProperty("id").GetGuid()).ToList();
        Assert.Single(theirStaged);
        await AssertNotFoundAsync(ours.GetAsync(In(A, $"generated-images/operations/{theirOperationId}"), Ct), "their run, to us");
        await AssertNotFoundAsync(ours.GetAsync(In(A, $"generated-images/{theirStaged[0]}/preview"), Ct), "their picture, to us");

        var run = await BodyOf(await ours.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        var staged = run.GetProperty("images").EnumerateArray().Select(image => image.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, staged.Count);

        // The run says what it was for, so it can be traced to the recipe and the piece of work without the prompt.
        Assert.Equal(recipeId, run.GetProperty("recipeId").GetGuid());
        Assert.Equal(contextId, run.GetProperty("creativeContextId").GetGuid());
        Assert.DoesNotContain(theirStaged[0], staged);

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"generated-images/operations/{operationId}"), Ct), "the generation run");
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"generated-images/{staged[0]}/preview"), Ct), "a staged picture");
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"generated-images/{staged[0]}/content"), Ct), "a staged picture's bytes");
        await AssertNotFoundAsync(
            theirs.SendAsync(HttpMethod.Delete, In(B, $"generated-images/{staged[0]}"), body: null, headers: null, Ct),
            "declining a staged picture");

        // 10. Renditions. The request for them was written with each staged picture; the dispatcher makes them.
        await DrainOutboxAsync();

        var rendered = await BodyOf(await ours.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        Assert.All(rendered.GetProperty("images").EnumerateArray(), image =>
        {
            Assert.Equal(JsonValueKind.Number, image.GetProperty("webSizeBytes").ValueKind);
            Assert.Equal(JsonValueKind.Number, image.GetProperty("thumbnailSizeBytes").ValueKind);
            Assert.True(image.GetProperty("webSizeBytes").GetInt64() < image.GetProperty("sizeBytes").GetInt64());
        });

        var preview = await ours.GetAsync(In(A, $"generated-images/{staged[0]}/preview"), Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("web", Assert.Single(preview.Headers.GetValues(MediaRenditionSelector.HeaderName)));
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);

        await AssertNotFoundAsync(
            theirs.GetAsync(In(B, $"generated-images/{staged[0]}/preview?rendition=thumbnail"), Ct), "a staged picture's thumbnail");

        // 11. Keep the first picture for this piece of work, with the prompt that made it. One request: the
        // picture is in the library and the work names it, together.
        var keep = new
        {
            generatedImageId = staged[0],
            creativeContextId = contextId,
            metadata = new { title = "Mapo tofu hero", altText = "A bowl of mapo tofu on a pale board." },
            prompt = new
            {
                channelKey = "instagram",
                imageKind = "Hero",
                text = promptText,
                generatedText = promptText,
                source = "ImagePromptComposition",
                aiProposalId = promptProposalId,
                recipeId,
                promptTemplateId = composed.GetProperty("promptTemplateId").GetString(),
                promptTemplateVersion = composed.GetProperty("promptTemplateVersion").GetString(),
                promptTemplateBodyChecksum = composed.GetProperty("promptTemplateBodyChecksum").GetString(),
            },
        };

        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(
                In(B, "dam-assets/from-generated-image"),
                new { generatedImageId = staged[0], metadata = new { title = "Theirs now" } },
                Ct),
            "keeping a staged picture");
        Assert.Empty(await ItemsAsync(theirs, B, "dam-assets"));

        var kept = await ours.PostAsJsonAsync(In(A, "dam-assets/from-generated-image"), keep, Key(), Ct);
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        var asset = await BodyOf(kept);
        var assetId = asset.GetProperty("id").GetGuid();
        var promptRecordId = asset.GetProperty("promptRecordId").GetGuid();
        Assert.Equal("AiGenerated", asset.GetProperty("kind").GetString());

        // The library's copy arrived with the renditions the staged picture already had.
        var library = await ours.GetAsync(In(A, $"dam-assets/{assetId}/content"), Ct);
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        Assert.Equal("web", Assert.Single(library.Headers.GetValues(MediaRenditionSelector.HeaderName)));

        var keeper = new { kind = "DamAsset", purpose = "Keeper", mediaAssetId = assetId };
        var named = (await BodyOf(await ours.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct)))
            .GetProperty("references").EnumerateArray()
            .Single(reference => reference.GetProperty("kind").GetString() == "DamAsset");
        Assert.Equal("Keeper", named.GetProperty("purpose").GetString());
        Assert.Equal(assetId, named.GetProperty("mediaAssetId").GetGuid());
        var keeperReferenceId = named.GetProperty("id").GetGuid();

        // The neighbour keeps a picture of their own and names our piece of work for it. Refused, and theirs
        // is still only staged: a work in another workspace is no work at all.
        var onOurWork = await theirs.PostAsJsonAsync(
            In(B, "dam-assets/from-generated-image"),
            new { generatedImageId = theirStaged[0], metadata = new { title = "Theirs" }, creativeContextId = contextId },
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, onOurWork.StatusCode);
        Assert.Equal(MediaErrorCodes.AssetInvalidRequest, await CodeOf(onOurWork));

        // The day's history is cached. Read now, before the posts exist, so the read at the end proves a
        // later write retires the cached answer rather than waiting out its lifetime.
        var soFar = Assert.Single(await ItemsAsync(ours, A, $"weekly-themes/{ThemeKey}/history"));
        Assert.Equal(
            ["AcceptedRecipeDraft", "KeptPicture"],
            soFar.GetProperty("outcomes").EnumerateArray().Select(outcome => outcome.GetProperty("kind").GetString()).Order());

        foreach (var path in (string[])[$"dam-assets/{assetId}", $"dam-assets/{assetId}/content", $"prompts/{promptRecordId}"])
        {
            await AssertNotFoundAsync(theirs.GetAsync(In(B, path), Ct), path);
        }

        var theirOwn = await theirs.PostAsJsonAsync(In(B, "creative-contexts"), new { workingTitle = "Their own work" }, Ct);
        Assert.Equal(HttpStatusCode.Created, theirOwn.StatusCode);
        var theirContext = await BodyOf(theirOwn);
        var borrowed = await theirs.PostAsJsonAsync(
            In(B, $"creative-contexts/{theirContext.GetProperty("id").GetGuid()}/references"),
            new { expectedConcurrencyToken = theirContext.GetProperty("concurrencyToken").GetString(), reference = keeper },
            Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowed.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, await CodeOf(borrowed));

        // 12. Have the posts written for both channels.
        var asking = new { creativeContextId = contextId, channelKeys = new[] { "instagram", "pinterest" } };

        await AssertNotFoundAsync(theirs.PostAsJsonAsync(In(B, "channel-post-requests"), asking, Key(), Ct), "asking for posts");

        var postsRequestId = await RequestAsync(ours, A, "channel-post-requests", asking);
        await RunAiPassAsync();

        var written = await BodyOf(await ours.GetAsync(In(A, $"channel-post-requests/{postsRequestId}"), Ct));
        Assert.Equal("Proposed", written.GetProperty("request").GetProperty("status").GetString());
        var package = written.GetProperty("package");
        Assert.Equal(
            new[] { "instagram", "pinterest" },
            package.GetProperty("channels").EnumerateArray().Select(channel => channel.GetProperty("channelKey").GetString()).Order());

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"channel-post-requests/{postsRequestId}"), Ct), "the posts request");

        // 13. The neighbour tries to read, rewrite and accept them first. Ours are exactly as they were written.
        var posts = $"creative-contexts/{contextId}/posts";
        var instagram = Channel(package, "instagram").GetProperty("latest").GetProperty("id").GetGuid();

        await AssertNotFoundAsync(theirs.GetAsync(In(B, posts), Ct), "the posts");
        await AssertNotFoundAsync(
            theirs.PatchAsJsonAsync(In(B, $"{posts}/instagram"), new { body = "Theirs now.", expectedLatestRevisionId = instagram }, Ct),
            "rewriting a post");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{posts}/instagram/disposition"), new { decision = "accept", revisionId = instagram }, Ct),
            "accepting a post");

        var untouched = Channel(await BodyOf(await ours.GetAsync(In(A, posts), Ct)), "instagram");
        Assert.Equal("Proposed", untouched.GetProperty("status").GetString());
        Assert.Equal(instagram, untouched.GetProperty("latest").GetProperty("id").GetGuid());

        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{posts}/instagram/disposition"), new { decision = "regenerate" }, Key(), Ct),
            "regenerating a post");

        // Each channel is its own decision.
        foreach (var channelKey in (string[])["instagram", "pinterest"])
        {
            var revisionId = Channel(package, channelKey).GetProperty("latest").GetProperty("id").GetGuid();
            var decided = await ours.PostAsJsonAsync(
                In(A, $"{posts}/{channelKey}/disposition"), new { decision = "accept", revisionId }, Ct);
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

            var channel = Channel(await BodyOf(decided), channelKey);
            Assert.Equal("Accepted", channel.GetProperty("status").GetString());
            Assert.Equal(revisionId, channel.GetProperty("accepted").GetProperty("id").GetGuid());
        }

        // 14. The day remembers the piece of work, and everything that came of it.
        var history = await ItemsAsync(ours, A, $"weekly-themes/{ThemeKey}/history");
        var remembered = Assert.Single(history);
        Assert.Equal(contextId, remembered.GetProperty("contextId").GetGuid());

        var outcomes = remembered.GetProperty("outcomes").EnumerateArray()
            .ToDictionary(outcome => outcome.GetProperty("kind").GetString()!);
        Assert.Equal(["AcceptedPosts", "AcceptedRecipeDraft", "KeptPicture"], outcomes.Keys.Order());
        Assert.Equal(recipeId, outcomes["AcceptedRecipeDraft"].GetProperty("recipeId").GetGuid());
        Assert.Equal(assetId, outcomes["KeptPicture"].GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(
            new[] { "instagram", "pinterest" },
            outcomes["AcceptedPosts"].GetProperty("channelKeys").EnumerateArray().Select(key => key.GetString()));

        // A day of the same name in the other workspace is a different day, with nothing in it.
        var theirDay = await theirs.SendAsync(
            HttpMethod.Put,
            In(B, "weekly-themes"),
            new { themes = new[] { new { day = "Monday", key = ThemeKey, displayName = "Meat-Free Monday" } } },
            headers: null,
            Ct);
        Assert.Equal(HttpStatusCode.OK, theirDay.StatusCode);
        Assert.Empty(await ItemsAsync(theirs, B, $"weekly-themes/{ThemeKey}/history"));

        // The day's standing notes travelled with the piece of work into every generation made for it. The
        // concepts were asked for before there was a piece of work, so they alone were written without them.
        Assert.DoesNotContain("Never tofu.", Assert.Single(_chat.PromptsFor("Propose recipe concepts")), StringComparison.Ordinal);
        Assert.Contains("Never tofu.", Assert.Single(_chat.PromptsFor("Write one image prompt")), StringComparison.Ordinal);
        Assert.Contains("Never tofu.", Assert.Single(_chat.PromptsFor("Write one post for each channel")), StringComparison.Ordinal);

        // Two shoots were planned in one pass. Ours carried our day and our recipe; theirs carried their dish
        // and nothing of ours.
        var shoots = _chat.PromptsFor("Plan photography");
        var ourShoot = Assert.Single(shoots, prompt => prompt.Contains("Never tofu.", StringComparison.Ordinal));
        var theirShoot = Assert.Single(shoots, prompt => prompt.Contains(TheirDish, StringComparison.Ordinal));
        Assert.Contains("Weeknight Mapo Tofu", ourShoot, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirDish, ourShoot, StringComparison.Ordinal);
        Assert.DoesNotContain("Mapo", theirShoot, StringComparison.OrdinalIgnoreCase);

        // The neighbour cannot take any of it away either, and our own slug is no way in for their session.
        var recipeToken = (await BodyOf(await ours.GetAsync(In(A, $"recipes/{recipeId}"), Ct))).GetProperty("concurrencyToken").GetString();
        var assetToken = (await BodyOf(await ours.GetAsync(In(A, $"dam-assets/{assetId}"), Ct))).GetProperty("concurrencyToken").GetString();

        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"recipes/{recipeId}/archive"), new { expectedConcurrencyToken = recipeToken }, Ct),
            "archiving the recipe");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(
                In(B, $"recipes/{recipeId}/versions/1/restore"), new { expectedConcurrencyToken = recipeToken }, Key(), Ct),
            "restoring a version of the recipe");
        await AssertNotFoundAsync(
            theirs.PatchAsJsonAsync(
                In(B, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = assetToken, title = "Theirs now" }, Ct),
            "retitling the kept picture");
        await AssertNotFoundAsync(
            theirs.SendAsync(
                HttpMethod.Delete, In(B, $"dam-assets/{assetId}"), new { confirmed = true, expectedConcurrencyToken = assetToken }, headers: null, Ct),
            "deleting the kept picture");
        await AssertNotFoundAsync(
            theirs.SendAsync(
                HttpMethod.Delete,
                In(B, $"creative-contexts/{contextId}/references/{keeperReferenceId}?expectedConcurrencyToken={Uri.EscapeDataString(token!)}"),
                body: null,
                headers: null,
                Ct),
            "removing the kept picture from the piece of work");

        foreach (var path in (string[])
        [
            $"recipes/{recipeId}", $"creative-contexts/{contextId}", posts, $"dam-assets/{assetId}", $"prompts/{promptRecordId}",
            $"weekly-themes/{ThemeKey}/history",
        ])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await theirs.GetAsync(In(A, path), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ours.GetAsync(In(A, path), Ct)).StatusCode);
        }

        var intact = await BodyOf(await ours.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct));
        Assert.Equal(2, intact.GetProperty("references").GetArrayLength());
        Assert.Equal(recipeToken, (await BodyOf(await ours.GetAsync(In(A, $"recipes/{recipeId}"), Ct))).GetProperty("concurrencyToken").GetString());
        Assert.Equal("Mapo tofu hero", (await BodyOf(await ours.GetAsync(In(A, $"dam-assets/{assetId}"), Ct))).GetProperty("title").GetString());

        // And none of the neighbour's attempts against our ids reached a model: our five answers and two
        // pictures, and the one of each the neighbour asked for about their own dish.
        Assert.Equal(6, _chat.Calls);
        Assert.Equal(3, _generator.Calls);
        Assert.Empty(await ItemsAsync(theirs, B, "recipes"));
        Assert.Empty(await ItemsAsync(theirs, B, "dam-assets"));
        Assert.Single(await ItemsAsync(ours, A, "dam-assets"));
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>Queues one AI request and returns the id to poll.</summary>
    private static async Task<Guid> RequestAsync(GatewayClient client, SeededWorkspace workspace, string route, object body)
    {
        var response = await client.PostAsJsonAsync(In(workspace, route), body, Key(), Ct);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"{route} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

        return (await BodyOf(response)).GetProperty("aiProposalRequestId").GetGuid();
    }

    /// <summary>The proposal a finished request holds.</summary>
    private static async Task<JsonElement> ProposalAsync(GatewayClient client, SeededWorkspace workspace, string path)
    {
        var status = await BodyOf(await client.GetAsync(In(workspace, path), Ct));
        Assert.Equal("Proposed", status.GetProperty("status").GetString());

        return status.GetProperty("proposal");
    }

    /// <summary>The rows that introduce a thing of one kind, in the order the model proposed them.</summary>
    private static IEnumerable<JsonElement> Added(JsonElement proposal, string targetKind) =>
        proposal.GetProperty("changes").EnumerateArray().Where(change =>
            change.GetProperty("changeKind").GetString() == "Add"
            && change.GetProperty("targetKind").GetString() == targetKind);

    private static JsonElement Channel(JsonElement package, string key) =>
        package.GetProperty("channels").EnumerateArray()
            .Single(channel => channel.GetProperty("channelKey").GetString() == key);

    private static async Task<List<JsonElement>> ItemsAsync(GatewayClient client, SeededWorkspace workspace, string path)
    {
        var response = await client.GetAsync(In(workspace, path), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await BodyOf(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task AssertNotFoundAsync(Task<HttpResponseMessage> attempt, string what)
    {
        var response = await attempt;

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"{what} answered {(int)response.StatusCode} to another workspace");

        // A coded refusal, so this is the route declining the id and not an address nothing answers at.
        var code = await CodeOf(response);
        Assert.True(code?.EndsWith("not_found", StringComparison.Ordinal), $"{what} was refused as '{code}'");
    }

    /// <summary>One pass of the AI queue. Everything queued for it must be claimed and must land.</summary>
    private async Task RunAiPassAsync(int operations = 1)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(Ct);

        Assert.Equal(new AiOperationWorkerPassSummary(Claimed: operations, Proposed: operations, Failed: 0, Skipped: 0), summary);
    }

    private async Task<GeneratedImagePassSummary> RunImagePassAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
    }

    /// <summary>Delivers every due outbox message, which is what makes the renditions.</summary>
    private async Task DrainOutboxAsync()
    {
        while (true)
        {
            await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
            var summary = await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);

            if (summary.Claimed == 0)
            {
                return;
            }
        }
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();

    private static byte[] Picture(int width, int height)
    {
        var samples = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var detail = (12 * Math.Sin((x + (3 * y)) / 2.0)) + (((x * 7) + (y * 13)) % 17) - 8;
                var at = ((y * width) + x) * 4;
                samples[at] = (byte)(128 + (70 * Math.Sin(x / 7.0)) + detail);
                samples[at + 1] = (byte)(128 + (70 * Math.Cos(y / 5.0)) + detail);
                samples[at + 2] = (byte)(128 + (70 * Math.Sin((x + y) / 9.0)) + detail);
                samples[at + 3] = byte.MaxValue;
            }
        }

        return new PngSampleFile { Width = (uint)width, Height = (uint)height, Samples = samples }.Build();
    }

    /// <summary>
    /// One model for the whole journey: it reads which task is asking from the task instructions and gives
    /// that task the answer its own handler tests use. Anything else asking is a step this journey has not
    /// scripted, and fails loudly rather than being answered with a guess.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private static readonly (string Task, string Answer)[] Answers =
        [
            ("Propose recipe concepts for a food content creator", """
                {
                  "schemaVersion": "recipe.concepts.v1",
                  "concepts": [
                    { "title": "Mapo Tofu Reimagined", "summary": "A spicier take.", "distinctnessRationale": "Uses fermented broad bean paste." },
                    { "title": "Dan Dan Noodle Bowl", "summary": "A quicker noodle dish.", "distinctnessRationale": "Cold-tossed rather than braised." }
                  ]
                }
                """),
            ("Propose one complete structured recipe draft", """
                {
                  "schemaVersion": "recipe.first-draft.v1",
                  "title": "Weeknight Mapo Tofu",
                  "ingredientGroups": [ { "ingredients": [ { "displayText": "1 block silken tofu, cubed" } ] } ],
                  "instructions": [ { "steps": [ { "text": "Simmer until heated through." } ] } ]
                }
                """),
            ("Plan photography for the subject described", """
                {
                  "schemaVersion": "image.photography-concept.v1",
                  "concepts": [
                    {
                      "label": "Morning window light",
                      "mood": "Unhurried and domestic",
                      "palette": "Warm neutrals and wheat",
                      "rationale": "Overhead suits a wide shallow bowl",
                      "channelFit": "A square crop still reads small",
                      "shots": [
                        {
                          "kind": "Hero",
                          "framing": "Overhead, square crop",
                          "lighting": "Soft daylight from the left",
                          "surface": "Pale oak over undyed linen",
                          "styling": "A spoon resting on the rim",
                          "props": ["Linen napkin", "Ceramic bowl"]
                        }
                      ]
                    }
                  ],
                  "warnings": []
                }
                """),
            ("Write one image prompt for the shot named in the SOURCE segment", """
                {
                  "schemaVersion": "image.prompt.v1",
                  "prompt": "Overhead square-crop photograph of a wide bowl of mapo tofu on a pale oak board over undyed linen, soft daylight from the left, a spoon resting on the rim, warm neutral palette.",
                  "avoid": ["harsh direct flash", "plastic props"],
                  "warnings": []
                }
                """),
            ("Write one post for each channel listed below", """
                {
                  "schemaVersion": "content.channel-posts.v1",
                  "posts": [
                    { "channelKey": "instagram", "body": "Mapo tofu for Monday night, bright with chilli and easy to love." },
                    { "channelKey": "pinterest", "body": "A simple weeknight mapo tofu." }
                  ]
                }
                """),
        ];

        private readonly List<string> _prompts = [];

        public int Calls { get; private set; }

        /// <summary>Everything each request for this task sent, instructions and source material together.</summary>
        public IReadOnlyList<string> PromptsFor(string task) =>
            [.. _prompts.Where(prompt => prompt.Contains(task, StringComparison.Ordinal))];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            _prompts.Add(string.Concat(messages.Select(message => message.Text)));

            // The task instructions travel in the system message; creator and source text never do, so text
            // an earlier step produced cannot be mistaken for the task that is asking now.
            var instructions = string.Concat(messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
            var answer = Answers.FirstOrDefault(each => instructions.Contains(each.Task, StringComparison.Ordinal)).Answer
                ?? throw new InvalidOperationException("A task this journey has not scripted asked the model for an answer.");

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)) { ModelId = "test-model" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
