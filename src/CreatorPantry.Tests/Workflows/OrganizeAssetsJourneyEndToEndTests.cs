using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
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
/// "Organize your pictures" end to end through the real Gateway (13A.21), in the order the guided screens send it: a
/// picture uploaded and kept on the run, described — and an edit made against an older read refused with nothing
/// written — tagged from the workspace's own list only, a use logged once however often it is sent, a new version
/// added beside an original that still downloads byte for byte — and a neighbouring workspace that reaches none of it.
/// A second walk starts from a picture, and a third from a generated picture waiting to be saved.
/// </summary>
/// <remarks>
/// The screens are the library's own upload, picker, save, edit and log-a-use forms, inline, so every request here
/// is one the library already sends. The workflow is only ever asked whether a step is done. Stubbed: the image
/// provider, the object store and the malware scanner.
/// </remarks>
public sealed class OrganizeAssetsJourneyEndToEndTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly FakeImageGenerator _generator = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator>(_generator);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
            services.AddGeneratedImageWorker();
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    private static string Key() => Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_picture_is_uploaded_described_tagged_and_noted_and_the_neighbour_reaches_none_of_it()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var runId = await StartAsync(owner, A);
        Assert.Equal("add", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 1. Add: nothing chosen is nothing done; an upload is kept on the run straight away ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "add")).StatusCode);

        var original = BrandSourceSampleFiles.Png(width: 32, height: 24);
        var uploaded = await owner.PostAsync(In(A, "dam-assets"), Upload(original, "My loaf"), "organize-upload", Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var created = await BodyOf(uploaded);
        var assetId = created.GetProperty("id").GetGuid();
        Assert.Equal("Original", created.GetProperty("kind").GetString());

        var kept = await owner.PutAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/add/draft"),
            new { kind = "MediaAsset", id = assetId, expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var resumed = (await ReadRunAsync(owner, A, runId)).GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == "add");
        Assert.Equal("MediaAsset", resumed.GetProperty("draftKind").GetString());
        Assert.Equal(assetId, resumed.GetProperty("draftResourceId").GetGuid());

        // Continue names nothing: the step's draft is what it made.
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "add")).StatusCode);

        // A step whose actions keep no draft takes none.
        var noDraft = await owner.PutAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/describe/draft"),
            new { kind = "MediaAsset", id = assetId, expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noDraft.StatusCode);
        Assert.Equal("describe", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 2. Describe: only what changed is sent; an edit against an older read writes nothing ----
        var read = await AssetAsync(owner, A, assetId);
        var firstToken = read.GetProperty("concurrencyToken").GetString();
        var described = await owner.PatchAsJsonAsync(
            In(A, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = firstToken, altText = "A round loaf on linen." }, Ct);
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);
        Assert.Equal("My loaf", (await BodyOf(described)).GetProperty("title").GetString());

        // A token this picture never held stands for a read someone else's save has moved past. (SQLite does not
        // move a row version on update, so the spent token above cannot play that part here.)
        var staleToken = Convert.ToBase64String(new byte[MediaConcurrencyToken.ByteLength]);
        var stale = await owner.PatchAsJsonAsync(
            In(A, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = staleToken, altText = "Someone else's words." }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("A round loaf on linen.", (await AssetAsync(owner, A, assetId)).GetProperty("altText").GetString());

        // A title can't be emptied, and nothing is written for the attempt.
        var token = (await AssetAsync(owner, A, assetId)).GetProperty("concurrencyToken").GetString();
        var noTitle = await owner.PatchAsJsonAsync(In(A, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = token, title = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "describe")).StatusCode);

        // ---- 3. Tag: from this workspace's own list, never the neighbour's ----
        var ours = await SeedTagAsync(A, "Weeknight");
        var theirs = await SeedTagAsync(B, "Their tag");

        token = (await AssetAsync(owner, A, assetId)).GetProperty("concurrencyToken").GetString();
        var crossing = await owner.PatchAsJsonAsync(In(A, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = token, tags = new[] { theirs } }, Ct);
        Assert.NotEqual(HttpStatusCode.OK, crossing.StatusCode);
        Assert.Empty((await AssetAsync(owner, A, assetId)).GetProperty("tags").EnumerateArray());

        var tagged = await owner.PatchAsJsonAsync(In(A, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = token, tags = new[] { ours } }, Ct);
        Assert.Equal(HttpStatusCode.OK, tagged.StatusCode);
        Assert.Equal(ours, Assert.Single((await BodyOf(tagged)).GetProperty("tags").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "tag")).StatusCode);

        // ---- 4. Note a use: sent twice under one key, logged once ----
        var use = new { platformKey = "instagram", utilizedOn = "2026-10-14", campaignName = "Autumn" };
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(In(A, $"dam-assets/{assetId}/utilization"), use, "organize-use", Ct)).StatusCode);
        var again = await owner.PostAsJsonAsync(In(A, $"dam-assets/{assetId}/utilization"), use, "organize-use", Ct);
        Assert.True(again.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"answered {(int)again.StatusCode}");
        var uses = await BodyOf(await owner.GetAsync(In(A, $"dam-assets/{assetId}/utilization"), Ct));
        Assert.Single(uses.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "note-use")).StatusCode);
        var finished = await owner.PostAsJsonAsync(
            In(A, $"workflow-runs/{runId}/completion"), new { expectedConcurrencyToken = await TokenAsync(owner, A, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("Completed", (await BodyOf(finished)).GetProperty("status").GetString());

        // ---- A new version goes beside the original, which is never replaced ----
        var newer = BrandSourceSampleFiles.Png(width: 48, height: 36);
        var version = await owner.PostAsync(In(A, $"dam-assets/{assetId}/versions"), FileOnly(newer), "organize-version", Ct);
        Assert.Equal(HttpStatusCode.Created, version.StatusCode);
        var after = await AssetAsync(owner, A, assetId);
        Assert.Equal(2, after.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, after.GetProperty("versions").GetArrayLength());
        Assert.Equal("A round loaf on linen.", after.GetProperty("altText").GetString());

        var first = await owner.GetAsync(In(A, $"dam-assets/{assetId}/versions/1/download"), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(original, await first.Content.ReadAsByteArrayAsync(Ct));

        // ---- The neighbour: none of it is theirs to read, change, note or finish a run with ----
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"dam-assets/{assetId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"dam-assets/{assetId}/versions/1/download"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"workflow-runs/{runId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PatchAsJsonAsync(
            In(B, $"dam-assets/{assetId}"), new { expectedConcurrencyToken = token, title = "Theirs now" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PostAsJsonAsync(
            In(B, $"dam-assets/{assetId}/utilization"), use, Key(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PostAsync(
            In(B, $"dam-assets/{assetId}/versions"), FileOnly(newer), Key(), Ct)).StatusCode);
        Assert.Empty((await BodyOf(await neighbour.GetAsync(In(B, "dam-assets"), Ct))).GetProperty("items").EnumerateArray());

        var theirRunId = await StartAsync(neighbour, B);
        var theirDraft = await neighbour.PutAsJsonAsync(
            In(B, $"workflow-runs/{theirRunId}/steps/add/draft"),
            new { kind = "MediaAsset", id = assetId, expectedConcurrencyToken = await TokenAsync(neighbour, B, theirRunId) },
            Ct);
        Assert.NotEqual(HttpStatusCode.OK, theirDraft.StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await CompleteAsync(neighbour, B, theirRunId, "add", [new { kind = "MediaAsset", id = assetId }])).StatusCode);
    }

    [Fact]
    public async Task A_run_started_from_a_picture_is_done_adding_it_without_choosing_again()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var uploaded = await owner.PostAsync(In(A, "dam-assets"), Upload(BrandSourceSampleFiles.Png(), "Old loaf"), Key(), Ct);
        var assetId = (await BodyOf(uploaded)).GetProperty("id").GetGuid();

        var started = await owner.PostAsJsonAsync(
            In(A, "workflow-runs"), new { definitionKey = "assets.organize", startingResource = new { kind = "MediaAsset", id = assetId } }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "add")).StatusCode);
        Assert.Equal("describe", NextStep(await ReadRunAsync(owner, A, runId)));
    }

    [Fact]
    public async Task A_generated_picture_waiting_on_a_piece_of_work_is_saved_into_the_library_and_added()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var contextId = (await BodyOf(await owner.PostAsJsonAsync(In(A, "creative-contexts"), new { workingTitle = "Plum galette week" }, Ct)))
            .GetProperty("id").GetGuid();

        // Made and kept on the work, and not yet saved: what the first step finds on recent work.
        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = "A plum galette.", variantCount = 1, creativeContextId = contextId }, Key(), Ct);
        var operationId = (await BodyOf(asked)).GetProperty("id").GetGuid();
        await using (var scope = _fixture.Api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
        }

        var made = await BodyOf(await owner.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        var staged = Assert.Single(made.GetProperty("images").EnumerateArray()).GetProperty("id").GetGuid();
        await AddReferenceAsync(owner, contextId, new { kind = "GeneratedImage", generatedImageId = staged, purpose = "Keeper" });

        var recent = await BodyOf(await owner.GetAsync(In(A, "creative-contexts?limit=10"), Ct));
        Assert.Contains(recent.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == contextId);
        Assert.Contains(
            (await BodyOf(await owner.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct))).GetProperty("references").EnumerateArray(),
            reference => reference.GetProperty("kind").GetString() == "GeneratedImage" && reference.GetProperty("purpose").GetString() == "Keeper");

        // Saved on that work, twice by accident: one picture in the library.
        var body = new { generatedImageId = staged, metadata = new { title = "Plum galette week" }, creativeContextId = contextId };
        var saved = await owner.PostAsJsonAsync(In(A, "dam-assets/from-generated-image"), body, Key(), Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var assetId = (await BodyOf(saved)).GetProperty("id").GetGuid();
        var savedAgain = await owner.PostAsJsonAsync(In(A, "dam-assets/from-generated-image"), body, Key(), Ct);
        Assert.Equal(assetId, (await BodyOf(savedAgain)).GetProperty("id").GetGuid());

        var runId = await StartAsync(owner, A);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "add", [new { kind = "MediaAsset", id = assetId }])).StatusCode);
        Assert.Equal("AiGenerated", (await AssetAsync(owner, A, assetId)).GetProperty("kind").GetString());
    }

    // ---- Helpers ----

    private static MultipartFormDataContent Upload(byte[] bytes, string title)
    {
        var form = FileOnly(bytes);
        form.Add(new StringContent(title), "title");

        return form;
    }

    private static MultipartFormDataContent FileOnly(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "loaf.png");

        return form;
    }

    private static async Task<Guid> StartAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var started = await client.PostAsJsonAsync(In(workspace, "workflow-runs"), new { definitionKey = "assets.organize" }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);

        return (await BodyOf(started)).GetProperty("id").GetGuid();
    }

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

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

    private static async Task<JsonElement> AssetAsync(GatewayClient client, SeededWorkspace workspace, Guid assetId) =>
        await BodyOf(await client.GetAsync(In(workspace, $"dam-assets/{assetId}"), Ct));

    private async Task AddReferenceAsync(GatewayClient client, Guid contextId, object reference)
    {
        var token = (await BodyOf(await client.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct))).GetProperty("concurrencyToken").GetString();
        var added = await client.PostAsJsonAsync(
            In(A, $"creative-contexts/{contextId}/references"), new { expectedConcurrencyToken = token, reference }, Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
    }

    /// <summary>A tag in one workspace's own list, as tagging a recipe would have made it.</summary>
    private async Task<Guid> SeedTagAsync(SeededWorkspace workspace, string name)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var tag = new WorkspaceTag { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = Now };
        db.WorkspaceTags.Add(tag);
        await db.SaveChangesAsync(Ct);

        return tag.Id;
    }
}
