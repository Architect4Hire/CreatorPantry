using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>POST .../brand-source-documents/text</c> through the real Gateway: pasted text is stored as a plain-text
/// file and handled exactly as an upload of one — the same limit, scan, replay and policy — and one workspace's
/// paste never reaches the other.
/// </summary>
public sealed class BrandSourcePasteEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "paste-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private readonly InMemoryPrivateObjectStore _store = new();

    private readonly FakeMalwareScanGateway _scanner = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(_scanner);
        });

        var userId = await _fixture.Api.CreateUserAsync(ContributorEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string PasteIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents/text";

    private static string Key() => Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static object Paste(
        string? text = "Okay, this lasagna is going to be your new favorite. Trust me.",
        string? title = "Sunday lasagna post",
        string documentType = "WritingSample",
        string purpose = "Voice") =>
        new { title, text, documentType, purpose };

    private Task<HttpResponseMessage> PasteAsync(GatewayClient client, SeededWorkspace workspace, object body, string? key = null) =>
        client.PostAsJsonAsync(PasteIn(workspace), body, key ?? Key(), TestContext.Current.CancellationToken);

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider);
    }

    private Task<int> DocumentCountAsync(SeededWorkspace workspace) => InScopeAsync(workspace, services =>
        services.GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Pasting_stores_the_text_as_a_plain_text_file_named_from_the_title()
    {
        var cancellation = TestContext.Current.CancellationToken;
        const string text = "Okay, this lasagna is going to be your new favorite. Trust me.\n";
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await PasteAsync(client, _fixture.WorkspaceA, Paste(text, title: "  Sunday / lasagna  ", purpose: "NotMyVoice"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.EndsWith($"{PasteIn(_fixture.WorkspaceA)[..^"/text".Length]}/{id}", response.Headers.Location!.ToString());
        Assert.Equal("Sunday / lasagna", body.GetProperty("title").GetString());
        Assert.Equal("NotMyVoice", body.GetProperty("purpose").GetString());

        var version = body.GetProperty("currentVersion");
        Assert.Equal("text/plain", version.GetProperty("mediaType").GetString());
        Assert.Equal("Sunday - lasagna.txt", version.GetProperty("originalFileName").GetString());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), version.GetProperty("sizeBytes").GetInt64());

        // The bytes in storage are exactly the pasted characters, with no byte-order mark.
        var key = Assert.Single(_store.Keys);
        Assert.StartsWith($"workspaces/{_fixture.WorkspaceA.Id:N}/brand-sources/{id:N}/", key);
        var stored = await InScopeAsync(_fixture.WorkspaceA, async services =>
        {
            await using var content = await services.GetRequiredService<IBrandSourceObjectGateway>().OpenReadAsync(key, cancellation);
            using var buffer = new MemoryStream();
            await content!.Content.CopyToAsync(buffer, cancellation);
            return buffer.ToArray();
        });
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(text), stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task Text_that_is_missing_or_blank_is_refused_on_the_text_field(string? text)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await PasteAsync(client, _fixture.WorkspaceA, Paste(text));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await BodyOf(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("text", out _));
        Assert.Empty(_store.Keys);
        Assert.Equal(0, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Text_over_the_text_limit_is_refused_and_stores_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var tooLong = new string('a', (int)BrandPolicy.SourceTextUploadMaxBytes + 1);

        var response = await PasteAsync(client, _fixture.WorkspaceA, Paste(tooLong));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("text", out _));
        Assert.Equal(0, _scanner.Scans);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task Every_problem_with_a_paste_is_reported_in_one_answer()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await PasteAsync(client, _fixture.WorkspaceA, Paste(text: " ", title: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await BodyOf(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("text", out _));
        Assert.True(errors.TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Repeating_a_paste_with_its_key_replays_it_and_stores_nothing_new()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var key = Key();

        var first = await PasteAsync(client, _fixture.WorkspaceA, Paste(), key);
        var second = await PasteAsync(client, _fixture.WorkspaceA, Paste(), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal((await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(second)).GetProperty("id").GetGuid());
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task A_key_reused_for_different_text_is_refused_and_stores_nothing_new()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var key = Key();

        await PasteAsync(client, _fixture.WorkspaceA, Paste("One set of words."), key);
        var second = await PasteAsync(client, _fixture.WorkspaceA, Paste("A different set of words."), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(second)).GetProperty("code").GetString());
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_paste_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(PasteIn(_fixture.WorkspaceA), Paste(), TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Pasted_text_the_scanner_refuses_is_rejected_and_leaves_nothing_behind()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await PasteAsync(client, _fixture.WorkspaceA, Paste($"hello {BrandSourceSampleFiles.MalwareMarker}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BrandErrorCodes.SourceFileRejected, JsonDocument.Parse(raw).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(BrandSourceSampleFiles.MalwareMarker, raw);
        Assert.Empty(_store.Keys);
        Assert.Equal(0, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_contributor_cannot_paste_and_nothing_is_read()
    {
        using var contributor = await _fixture.SignInAsync(ContributorEmail, Password, TestContext.Current.CancellationToken);

        var response = await PasteAsync(contributor, _fixture.WorkspaceA, Paste());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _scanner.Scans);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task One_workspaces_paste_is_invisible_and_unreachable_from_the_other()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var key = Key();

        var inA = await BodyOf(await PasteAsync(ownerA, _fixture.WorkspaceA, Paste(), key));

        // B's owner cannot paste into A, and it answers as a workspace that was never created does.
        var intoA = await PasteAsync(ownerB, _fixture.WorkspaceA, Paste());
        var intoNowhere = await ownerB.PostAsJsonAsync(
            "/api/v1/workspaces/no-such-kitchen/brand-source-documents/text", Paste(), Key(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);

        // The same key and the same words in B are B's own document, not a replay of A's.
        var inB = await PasteAsync(ownerB, _fixture.WorkspaceB, Paste(), key);
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);
        Assert.False(inB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.NotEqual(inA.GetProperty("id").GetGuid(), (await BodyOf(inB)).GetProperty("id").GetGuid());

        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceB));
        Assert.Contains(_store.Keys, candidate => candidate.StartsWith($"workspaces/{_fixture.WorkspaceB.Id:N}/", StringComparison.Ordinal));
    }

    [Fact]
    public void An_example_marked_as_not_my_voice_is_never_offered_as_grounding_for_any_task()
    {
        foreach (var task in Enum.GetValues<AiTaskType>())
        {
            Assert.DoesNotContain(BrandSourcePurpose.NotMyVoice, BrandContextSelection.PurposesFor(task));
        }
    }
}
