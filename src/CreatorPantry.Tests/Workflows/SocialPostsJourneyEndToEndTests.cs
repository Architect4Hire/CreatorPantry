using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Workflows;

#pragma warning disable MEAI001

/// <summary>
/// "Create social posts" end to end through the real Gateway (13A.20), in the order the guided screens send it: the
/// piece of work its first step files from a recipe, posts written for two channels and one edited past its limit,
/// one kept, a library picture chosen, one card put on the board however often it is asked for, the posts marked for
/// review when the recipe moves — and a neighbouring workspace that reaches none of it.
/// </summary>
/// <remarks>
/// <para>
/// The screens are the Social Studio's own sections and the shared posts run, hosted, so every request here is one the
/// studio already sends; the card is the board's ordinary create, naming the records it links. The workflow is only
/// ever asked whether a step is done. Nothing is scheduled.
/// </para>
/// <para>
/// Stubbed: the chat model, scripted with one known-good answer for the posts; the image provider, the object store
/// and the malware scanner, for the one library picture. The workers and the outbox are run by hand.
/// </para>
/// </remarks>
public sealed class SocialPostsJourneyEndToEndTests : IAsyncLifetime
{
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

        _fixture.Api.Factory.Services.GetRequiredService<AiTaskOptions>().Enabled.Add(AiTaskCatalog.ChannelPosts);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    private static string Key() => Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task Posts_are_written_kept_pictured_and_put_on_the_board_once_and_the_neighbour_reaches_none_of_it()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var recipe = await CreateRecipeAsync(owner, A);

        var started = await owner.PostAsJsonAsync(In(A, "workflow-runs"), new { definitionKey = "social.create" }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal("about", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 1. What the posts are about: filed on the first answer, kept on the run, named as what the step made ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "about")).StatusCode);

        var contextId = await ContextAsync(owner, A, "Plum galette week");
        await AddReferenceAsync(owner, A, contextId, new { kind = "Recipe", recipeId = recipe.Id });
        var kept = await owner.PutAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/about/draft"),
            new { kind = "CreativeContext", id = contextId, expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "about", [new { kind = "CreativeContext", id = contextId }])).StatusCode);
        Assert.Equal("write", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 2. Written and chosen: nothing kept is nothing done ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "write")).StatusCode);

        var asked = await owner.PostAsJsonAsync(
            In(A, "channel-post-requests"),
            new { creativeContextId = contextId, channelKeys = new[] { "instagram", "x" } },
            Key(),
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        await RunAiPassAsync();

        var posts = $"creative-contexts/{contextId}/posts";
        var package = await BodyOf(await owner.GetAsync(In(A, posts), Ct));
        var packageId = package.GetProperty("id").GetGuid();
        Assert.All(package.GetProperty("channels").EnumerateArray(), channel => Assert.Equal("Proposed", channel.GetProperty("status").GetString()));

        // The creator's own words past X's limit are kept as written and measured against it — never trimmed.
        var tooLong = new string('a', 300);
        var xLatest = Channel(package, "x").GetProperty("latest").GetProperty("id").GetGuid();
        var edited = await owner.PatchAsJsonAsync(In(A, $"{posts}/x"), new { body = tooLong, expectedLatestRevisionId = xLatest }, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var xNow = Channel(await BodyOf(edited), "x").GetProperty("latest");
        Assert.Equal(tooLong, xNow.GetProperty("body").GetString());
        Assert.Equal("Over", xNow.GetProperty("limitStatus").GetString());
        Assert.Equal(280, xNow.GetProperty("characterLimit").GetInt32());

        // One kept, on its own; the other channel is untouched by that decision.
        var instagram = Channel(package, "instagram").GetProperty("latest").GetProperty("id").GetGuid();
        var accepted = await owner.PostAsJsonAsync(In(A, $"{posts}/instagram/disposition"), new { decision = "accept", revisionId = instagram }, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var decided = await BodyOf(accepted);
        Assert.Equal("Accepted", Channel(decided, "instagram").GetProperty("status").GetString());
        Assert.Equal("Proposed", Channel(decided, "x").GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "write")).StatusCode);
        Assert.Equal("picture", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 3. A picture from the library goes with them ----
        var assetId = await LibraryPictureAsync(owner, A);
        await AddReferenceAsync(owner, A, contextId, new { kind = "DamAsset", mediaAssetId = assetId });
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "picture")).StatusCode);
        Assert.Equal("board", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 4. The board: one card, however often it is asked for, linking what the work names ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "board")).StatusCode);

        var card = new
        {
            title = "Plum galette week",
            column = "ToDo",
            creativeContextId = contextId,
            recipeId = recipe.Id,
            mediaAssetId = assetId,
            socialPackageId = packageId,
        };
        var made = await owner.PostAsJsonAsync(In(A, "content-cards"), card, "social-journey-card", Ct);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var cardBody = await BodyOf(made);
        var cardId = cardBody.GetProperty("card").GetProperty("id").GetGuid();
        Assert.Equal("ToDo", cardBody.GetProperty("card").GetProperty("column").GetString());
        Assert.Equal(JsonValueKind.Null, cardBody.GetProperty("card").GetProperty("plannedDay").ValueKind);

        var again = await owner.PostAsJsonAsync(In(A, "content-cards"), card, "social-journey-card", Ct);
        Assert.Equal(cardId, (await BodyOf(again)).GetProperty("card").GetProperty("id").GetGuid());

        // The same key for a different card is refused, not a second card.
        var reused = await owner.PostAsJsonAsync(In(A, "content-cards"), new { title = "Something else" }, "social-journey-card", Ct);
        Assert.NotEqual(HttpStatusCode.Created, reused.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, reused.StatusCode);
        var board = await BodyOf(await owner.GetAsync(In(A, "content-cards"), Ct));
        Assert.Single(board.GetProperty("items").EnumerateArray(), item => item.GetProperty("title").GetString() == "Plum galette week");

        // The card is kept on the run as soon as it is made, so coming back — on any device — shows it.
        var cardKept = await owner.PutAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/board/draft"),
            new { kind = "ContentCard", id = cardId, expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, cardKept.StatusCode);
        var boardStep = (await ReadRunAsync(owner, A, runId)).GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("key").GetString() == "board");
        Assert.Equal(cardId, boardStep.GetProperty("draftResourceId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "board")).StatusCode);
        var finished = await owner.PostAsJsonAsync(
            In(A, $"workflow-runs/{runId}/completion"), new { expectedConcurrencyToken = await TokenAsync(owner, A, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("Completed", (await BodyOf(finished)).GetProperty("status").GetString());

        // ---- Stale: the recipe moves, and the kept post is marked for review rather than rewritten ----
        var changed = await owner.PatchAsJsonAsync(
            In(A, $"recipes/{recipe.Id}"), new { expectedConcurrencyToken = recipe.Token, title = "Plum galette, revisited" }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await DrainOutboxAsync();

        var stale = Channel(await BodyOf(await owner.GetAsync(In(A, posts), Ct)), "instagram");
        Assert.Equal("NeedsReview", stale.GetProperty("status").GetString());
        Assert.Equal(instagram, stale.GetProperty("accepted").GetProperty("id").GetGuid());

        // ---- The neighbour: none of it is theirs to read, link or finish their own run with ----
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"workflow-runs/{runId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"creative-contexts/{contextId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, posts), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"content-cards/{cardId}"), Ct)).StatusCode);

        // Each record the board step links is a way in of its own, and none of ours goes on their board.
        foreach (var crossing in new object[]
        {
            new { title = "Theirs", creativeContextId = contextId },
            new { title = "Theirs", socialPackageId = packageId },
            new { title = "Theirs", recipeId = recipe.Id },
            new { title = "Theirs", mediaAssetId = assetId },
        })
        {
            var theirCard = await neighbour.PostAsJsonAsync(In(B, "content-cards"), crossing, Key(), Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, theirCard.StatusCode);
        }

        // Our card's request key, sent from their workspace, answers with nothing of ours.
        var replayed = await neighbour.PostAsJsonAsync(In(B, "content-cards"), card, "social-journey-card", Ct);
        Assert.NotEqual(HttpStatusCode.Created, replayed.StatusCode);
        Assert.DoesNotContain(cardId.ToString(), await replayed.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        // Nor can they decide on, rewrite or regenerate our posts.
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PostAsJsonAsync(
            In(B, $"{posts}/x/disposition"), new { decision = "accept", revisionId = xLatest }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PatchAsJsonAsync(
            In(B, $"{posts}/x"), new { body = "Theirs now.", expectedLatestRevisionId = xLatest }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PostAsJsonAsync(
            In(B, $"{posts}/x/disposition"), new { decision = "regenerate" }, Key(), Ct)).StatusCode);
        Assert.Empty((await BodyOf(await neighbour.GetAsync(In(B, "content-cards"), Ct))).GetProperty("items").EnumerateArray());

        // A Viewer is refused the board step's one write, which the step does not offer them either.
        using var viewer = await _fixture.SignInAsync(B.MemberEmail, cancellationToken: Ct);
        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);
        var viewed = await viewer.PostAsJsonAsync(In(B, "content-cards"), new { title = "A viewer's card" }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, viewed.StatusCode);
        Assert.Empty((await BodyOf(await viewer.GetAsync(In(B, "content-cards"), Ct))).GetProperty("items").EnumerateArray());

        var theirRunId = (await BodyOf(await neighbour.PostAsJsonAsync(
            In(B, "workflow-runs"), new { definitionKey = "social.create" }, Key(), Ct))).GetProperty("id").GetGuid();
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await CompleteAsync(neighbour, B, theirRunId, "about", [new { kind = "CreativeContext", id = contextId }])).StatusCode);

        // Nor can our card be kept on a run of theirs.
        var theirBoard = await neighbour.PutAsJsonAsync(
            In(B, $"workflow-runs/{theirRunId}/steps/about/draft"),
            new { kind = "ContentCard", id = cardId, expectedConcurrencyToken = await TokenAsync(neighbour, B, theirRunId) },
            Ct);
        Assert.NotEqual(HttpStatusCode.OK, theirBoard.StatusCode);
    }

    // ---- Helpers ----

    private sealed record SeededRecipe(Guid Id, string Token);

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private static JsonElement Channel(JsonElement package, string key) =>
        package.GetProperty("channels").EnumerateArray().Single(channel => channel.GetProperty("channelKey").GetString() == key);

    private static async Task<JsonElement> ReadRunAsync(GatewayClient client, SeededWorkspace workspace, Guid runId) =>
        await BodyOf(await client.GetAsync(In(workspace, $"workflow-runs/{runId}"), Ct));

    private static async Task<string> TokenAsync(GatewayClient client, SeededWorkspace workspace, Guid runId) =>
        (await ReadRunAsync(client, workspace, runId)).GetProperty("concurrencyToken").GetString()!;

    private static async Task<HttpResponseMessage> CompleteAsync(
        GatewayClient client, SeededWorkspace workspace, Guid runId, string stepKey, object[]? results = null) =>
        await client.PostAsJsonAsync(
            In(workspace, $"workflow-runs/{runId}/steps/{stepKey}/completion"),
            new { results, expectedConcurrencyToken = await TokenAsync(client, workspace, runId) },
            Ct);

    private static async Task<SeededRecipe> CreateRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var created = await client.PostAsJsonAsync(In(workspace, "recipes"), new { title = "Plum galette" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct));

        return new SeededRecipe(detail.GetProperty("id").GetGuid(), detail.GetProperty("concurrencyToken").GetString()!);
    }

    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace, string workingTitle)
    {
        var response = await client.PostAsJsonAsync(In(workspace, "creative-contexts"), new { workingTitle }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task AddReferenceAsync(GatewayClient client, SeededWorkspace workspace, Guid contextId, object reference)
    {
        var token = (await BodyOf(await client.GetAsync(In(workspace, $"creative-contexts/{contextId}"), Ct)))
            .GetProperty("concurrencyToken").GetString();
        var added = await client.PostAsJsonAsync(
            In(workspace, $"creative-contexts/{contextId}/references"), new { expectedConcurrencyToken = token, reference }, Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
    }

    /// <summary>One picture in this workspace's library, made and saved as the Image Studio saves one.</summary>
    private async Task<Guid> LibraryPictureAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var asked = await client.PostAsJsonAsync(
            In(workspace, "generated-images"), new { promptText = "A plum galette, sliced.", variantCount = 1 }, Key(), Ct);
        var operationId = (await BodyOf(asked)).GetProperty("id").GetGuid();

        await using (var scope = _fixture.Api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
        }

        var made = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));
        var staged = Assert.Single(made.GetProperty("images").EnumerateArray()).GetProperty("id").GetGuid();
        var saved = await client.PostAsJsonAsync(
            In(workspace, "dam-assets/from-generated-image"),
            new { generatedImageId = staged, metadata = new { title = "Galette, sliced" } },
            Key(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        return (await BodyOf(saved)).GetProperty("id").GetGuid();
    }

    private async Task RunAiPassAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(Ct);

        Assert.Equal(new AiOperationWorkerPassSummary(Claimed: 1, Proposed: 1, Failed: 0, Skipped: 0), summary);
    }

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

    /// <summary>The model, with one known-good answer: a post for each channel the request names.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private const string Posts = """
            {
              "schemaVersion": "content.channel-posts.v1",
              "posts": [
                { "channelKey": "instagram", "body": "Plum galette for the weekend, jammy and easy to love." },
                { "channelKey": "x", "body": "A plum galette that forgives you." }
              ]
            }
            """;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var instructions = string.Concat(messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
            if (!instructions.Contains("Write one post for each channel", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A task this journey has not scripted asked the model for an answer.");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Posts)) { ModelId = "test-model" });
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
