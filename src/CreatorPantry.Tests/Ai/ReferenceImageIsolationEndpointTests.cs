using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-004's request route through the real Gateway, across two workspaces, over real uploaded images.
/// </summary>
/// <remarks>
/// <para>
/// The reference is a brand source document, so this test uploads one into each workspace through the route
/// that exists for it — which means the id crossing the boundary below is a <em>real</em> document of a real
/// neighbour, not a GUID that exists nowhere. That is the case A's own ids cannot imitate, and the one worth
/// proving: it must answer exactly as an id that was never issued.
/// </para>
/// <para>
/// Nothing here reaches a provider. The test host runs no worker, so an accepted request stays queued, which
/// is all these assertions need; what the handler does with the bytes is
/// <c>ReferenceImageAnalysisAiTaskHandlerTests</c>'s subject.
/// </para>
/// </remarks>
public sealed class ReferenceImageIsolationEndpointTests : IAsyncLifetime
{
    private readonly InMemoryPrivateObjectStore _store = new();

    private readonly FakeMalwareScanGateway _scanner = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(_scanner);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A real document of the neighbour's is answered as one that never existed.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_image_is_answered_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var neighbours = await UploadImageAsync(theirs, _fixture.WorkspaceB);

        var crossing = await AskAsync(mine, _fixture.WorkspaceA, Body(neighbours));
        var missing = await AskAsync(mine, _fixture.WorkspaceA, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);

        // The same refusal in full, not merely the same code: wording that distinguished the two would say
        // which of a neighbour's documents exist (tenancy.md).
        Assert.Equal(
            WithoutTraceId(await BodyOf(missing)),
            WithoutTraceId(await BodyOf(crossing)));
    }

    /// <summary>And this workspace's own image is accepted, so the refusal above is the filter and not the field.</summary>
    [Fact]
    public async Task This_workspaces_own_image_is_accepted_and_queued()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var document = await UploadImageAsync(mine, _fixture.WorkspaceA);

        var response = await AskAsync(mine, _fixture.WorkspaceA, Body(document));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal("Requested", body.GetProperty("status").GetString());
        Assert.Equal(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/reference-image-requests/"
                + body.GetProperty("aiProposalRequestId").GetGuid(),
            response.Headers.Location!.ToString());
    }

    /// <summary>A GIF is as readable a reference as a PNG, which is why the inspector learned it.</summary>
    [Fact]
    public async Task An_animated_gif_is_an_acceptable_reference()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var document = await UploadImageAsync(
            mine, _fixture.WorkspaceA, BrandSourceSampleFiles.Gif(frames: 3), "loaf.gif");

        Assert.Equal(HttpStatusCode.Accepted, (await AskAsync(mine, _fixture.WorkspaceA, Body(document))).StatusCode);
    }

    /// <summary>
    /// A document that is not an image is refused in the same words as one that does not exist.
    /// </summary>
    /// <remarks>
    /// A creator who names their style guide gets told their workspace has no image with that id — the same
    /// answer as a stranger's id, because saying "that document is a PDF" would confirm the document exists.
    /// </remarks>
    [Fact]
    public async Task A_document_that_is_not_an_image_is_refused_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var pdf = await UploadImageAsync(
            mine, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf(), "house-style.pdf");

        var wrongKind = await AskAsync(mine, _fixture.WorkspaceA, Body(pdf));
        var missing = await AskAsync(mine, _fixture.WorkspaceA, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, wrongKind.StatusCode);
        Assert.Equal(
            WithoutTraceId(await BodyOf(missing)),
            WithoutTraceId(await BodyOf(wrongKind)));
    }

    /// <summary>
    /// A member of one workspace cannot ask in the other, and the answer is 404 rather than 403.
    /// </summary>
    /// <remarks>
    /// Not a weaker refusal: tenancy.md requires that an inaccessible workspace and an unknown one answer
    /// alike, so a non-member learns nothing about whether the workspace exists. 403 is what a member of
    /// <em>this</em> workspace gets when their role is too low, which is a different question.
    /// </remarks>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_ask_in_the_other()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var asking = await AskAsync(mine, _fixture.WorkspaceB, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, asking.StatusCode);
    }

    /// <summary>Without an idempotency key a retry would buy a second reading.</summary>
    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), Body(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The same key returns the first answer rather than reading the image twice.</summary>
    [Fact]
    public async Task The_same_key_replays_rather_than_reading_twice()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var document = await UploadImageAsync(mine, _fixture.WorkspaceA);
        var key = Guid.NewGuid().ToString("N");

        var first = await mine.PostAsJsonAsync(Route(_fixture.WorkspaceA), Body(document), key, Ct);
        var again = await mine.PostAsJsonAsync(Route(_fixture.WorkspaceA), Body(document), key, Ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(
            (await BodyOf(first)).GetProperty("aiProposalRequestId").GetGuid(),
            (await BodyOf(again)).GetProperty("aiProposalRequestId").GetGuid());
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
    }

    /// <summary>A malformed request is refused before any id is resolved, and names the code.</summary>
    [Fact]
    public async Task A_request_naming_no_image_is_refused_as_invalid()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await AskAsync(client, _fixture.WorkspaceA, new { note = "Just this feel." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            AiReferenceImageRequestErrors.RequestInvalid,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>Another workspace's request id is answered as one that does not exist, on the read too.</summary>
    [Fact]
    public async Task Another_workspaces_request_cannot_be_read()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var document = await UploadImageAsync(theirs, _fixture.WorkspaceB);
        var queued = await AskAsync(theirs, _fixture.WorkspaceB, Body(document));
        var requestId = (await BodyOf(queued)).GetProperty("aiProposalRequestId").GetGuid();

        var crossing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{requestId}", Ct);
        var missing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(
            WithoutTraceId(await BodyOf(missing)),
            WithoutTraceId(await BodyOf(crossing)));
    }

    /// <summary>
    /// An image larger than one request can carry is refused now, not queued to fail later.
    /// </summary>
    /// <remarks>
    /// The brand library accepts uploads far larger than the envelope's cap, so without a size check at
    /// request time a big photograph was accepted with 202, charged against the workspace's allowance, and
    /// then guaranteed to fail in the worker minutes afterwards. Refused in the same words as a document
    /// that is not an image, because both are "this workspace has no image you can read with that id".
    /// </remarks>
    [Fact]
    public async Task An_image_too_large_to_carry_is_refused_at_the_request()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var big = await UploadImageAsync(
            mine, _fixture.WorkspaceA, Padded(BrandSourceSampleFiles.Png(), 5 * 1024 * 1024), "huge.png");

        var response = await AskAsync(mine, _fixture.WorkspaceA, Body(big));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            AiReferenceImageRequestErrors.ReferenceNotFound,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A real image header followed by enough bytes to pass the upload cap and fail the envelope's.
    /// </summary>
    /// <remarks>
    /// The brand inspector reads a PNG's dimensions from its header and measures the whole file; neither
    /// cares what follows. 5MB sits between the two limits — under the 20MB a brand upload allows, over the
    /// 4MB one request can carry — which is exactly the gap the request-time check exists to close.
    /// </remarks>
    private static byte[] Padded(byte[] image, int totalLength)
    {
        var padded = new byte[totalLength];
        image.CopyTo(padded, 0);

        return padded;
    }

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/reference-image-requests";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static object Body(Guid referenceDocumentId) => new
    {
        referenceDocumentId,
        note = "I want the same quiet, overcast feel as this one.",
    };

    private static Task<HttpResponseMessage> AskAsync(
        GatewayClient client, SeededWorkspace workspace, object body) =>
        client.PostAsJsonAsync(Route(workspace), body, Guid.NewGuid().ToString("N"), Ct);

    /// <summary>One uploaded image of that workspace, through the route that inspects it.</summary>
    private static async Task<Guid> UploadImageAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        byte[]? bytes = null,
        string fileName = "reference.png")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? BrandSourceSampleFiles.Png());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        form.Add(new StringContent("Reference shot"), "title");
        form.Add(new StringContent("VisualReference"), "documentType");
        form.Add(new StringContent("VisualDirection"), "purpose");

        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents",
            form,
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <inheritdoc cref="ImagePromptIsolationEndpointTests"/>
    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }
}
