using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The <c>.../prompts</c> routes through the real Gateway — the list, one prompt in full, and the save: the
/// contract, the role bars, replay, and the boundary between two workspaces.
/// </summary>
public sealed class PromptsEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PromptsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/prompts";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static object Manual(string text = "Overhead shot of soda bread on linen, soft window light.") => new
    {
        channelKey = "instagram",
        imageKind = "Hero",
        text,
        label = "Soda bread hero",
        source = "Manual",
    };

    private static Task<HttpResponseMessage> SaveAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? idempotencyKey = null) =>
        idempotencyKey is null
            ? client.PostAsJsonAsync(PromptsIn(workspace), body, Ct)
            : client.PostAsJsonAsync(PromptsIn(workspace), body, idempotencyKey, Ct);

    /// <summary>A recipe and its first version, created through the real route so the pins are real.</summary>
    private async Task<(Guid RecipeId, Guid VersionId)> CreateRecipeAsync(SeededWorkspace workspace, string email)
    {
        using var client = await SignInAsync(email);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Buttermilk Soda Bread" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("recipeId").GetGuid(), body.GetProperty("versionId").GetGuid());
    }

    private async Task<int> CountAsync(SeededWorkspace workspace)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().PromptRecords.CountAsync(Ct);
    }

    private static Task<HttpResponseMessage> ListAsync(
        GatewayClient client, SeededWorkspace workspace, string query = "") =>
        client.GetAsync($"{PromptsIn(workspace)}{query}", Ct);

    private static JsonElement[] Items(JsonElement body) => [.. body.GetProperty("items").EnumerateArray()];

    private static Task<HttpResponseMessage> DetailAsync(
        GatewayClient client, SeededWorkspace workspace, Guid promptRecordId) =>
        client.GetAsync($"{PromptsIn(workspace)}/{promptRecordId}", Ct);

    private static Task<HttpResponseMessage> TextAsync(
        GatewayClient client, SeededWorkspace workspace, Guid promptRecordId, string query = "") =>
        client.GetAsync($"{PromptsIn(workspace)}/{promptRecordId}/text{query}", Ct);

    private static Task<HttpResponseMessage> RecordAsync(
        GatewayClient client, SeededWorkspace workspace, Guid promptRecordId, string query = "") =>
        client.GetAsync($"{PromptsIn(workspace)}/{promptRecordId}/record{query}", Ct);

    /// <summary>The header exactly as sent, before the client's parser normalises it. Exactly one is sent.</summary>
    private static string RawDisposition(HttpResponseMessage response) =>
        response.Content.Headers.NonValidated["Content-Disposition"].Single();

    /// <summary>Saves one prompt through the real route and returns its id.</summary>
    private async Task<Guid> SavedIdAsync(GatewayClient client, SeededWorkspace workspace, object? body = null) =>
        (await BodyOf(await SaveAsync(client, workspace, body ?? Manual())))
            .GetProperty("promptRecordId").GetGuid();

    /// <summary>
    /// A problem body with its per-request field dropped, so two refusals can be compared as the answers they
    /// are. <c>traceId</c> differs by design and is the only thing allowed to.
    /// </summary>
    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }

    // ---- the list ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_with_no_prompts_answers_an_empty_page()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await ListAsync(client, _fixture.WorkspaceA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var body = await BodyOf(response);
        Assert.Empty(Items(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_saved_prompt_appears_in_the_list_as_a_preview_rather_than_a_prompt()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var text = new string('a', 1000);
        await SaveAsync(client, _fixture.WorkspaceA, Manual(text));

        var body = await BodyOf(await ListAsync(client, _fixture.WorkspaceA));
        var row = Assert.Single(Items(body));

        Assert.Equal(200, row.GetProperty("textPreview").GetString()!.Length);
        Assert.Equal(1000, row.GetProperty("textLength").GetInt32());
        Assert.Equal("instagram", row.GetProperty("channelKey").GetString());
        Assert.Equal("Hero", row.GetProperty("imageKind").GetString());
        Assert.Equal("Manual", row.GetProperty("source").GetString());

        // The prompt itself, the model's draft and the provenance belong to the detail route.
        Assert.False(row.TryGetProperty("text", out _));
        Assert.False(row.TryGetProperty("generatedText", out _));
        Assert.False(row.TryGetProperty("createdByMembershipId", out _));
    }

    /// <summary>A Viewer may read the library; it is the workspace's own content.</summary>
    [Fact]
    public async Task A_viewer_may_list_prompts_even_though_they_may_not_save_one()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await SaveAsync(owner, _fixture.WorkspaceB, Manual());

        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await ListAsync(viewer, _fixture.WorkspaceB);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(Items(await BodyOf(response)));
    }

    [Fact]
    public async Task The_list_filters_by_channel_and_searches_the_text()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await SaveAsync(client, _fixture.WorkspaceA, Manual("Overhead soda bread on linen."));
        await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "blog",
            imageKind = "ProcessStep",
            text = "The dough before it goes in.",
            source = "Manual",
        });

        Assert.Single(Items(await BodyOf(await ListAsync(client, _fixture.WorkspaceA, "?channel=blog"))));
        Assert.Single(Items(await BodyOf(await ListAsync(client, _fixture.WorkspaceA, "?search=soda"))));
        Assert.Empty(Items(await BodyOf(await ListAsync(client, _fixture.WorkspaceA, "?search=risotto"))));
        Assert.Empty(Items(await BodyOf(await ListAsync(client, _fixture.WorkspaceA, "?channel=mastodon"))));
    }

    [Fact]
    public async Task The_list_pages_with_a_cursor()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        for (var index = 0; index < 3; index++)
        {
            await SaveAsync(client, _fixture.WorkspaceA, Manual($"Prompt {index}."));
        }

        var first = await BodyOf(await ListAsync(client, _fixture.WorkspaceA, "?limit=2"));
        Assert.Equal(2, Items(first).Length);
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());

        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await BodyOf(await ListAsync(
            client, _fixture.WorkspaceA, $"?cursor={Uri.EscapeDataString(cursor)}"));

        Assert.Single(Items(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task A_malformed_cursor_answers_400_with_the_cursor_code()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await ListAsync(client, _fixture.WorkspaceA, "?cursor=not-a-cursor%21");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ContentErrorCodes.PromptCursorInvalid, Code(await BodyOf(response)));
    }

    /// <summary>
    /// A cursor minted in one workspace is refused in the other, rather than paging the wrong library.
    /// </summary>
    [Fact]
    public async Task A_cursor_does_not_cross_the_workspace_boundary()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        for (var index = 0; index < 3; index++)
        {
            await SaveAsync(ownerA, _fixture.WorkspaceA, Manual($"Mine {index}."));
            await SaveAsync(ownerB, _fixture.WorkspaceB, Manual($"Theirs {index}."));
        }

        var mine = await BodyOf(await ListAsync(ownerA, _fixture.WorkspaceA, "?limit=2"));
        var cursor = mine.GetProperty("nextCursor").GetString()!;

        var replayed = await ListAsync(ownerB, _fixture.WorkspaceB, $"?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(ContentErrorCodes.PromptCursorInvalid, Code(await BodyOf(replayed)));
    }

    [Fact]
    public async Task One_workspaces_list_never_shows_the_others_prompts()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        await SaveAsync(ownerA, _fixture.WorkspaceA, Manual("Mine."));
        await SaveAsync(ownerB, _fixture.WorkspaceB, Manual("Theirs, about risotto."));

        var mine = await BodyOf(await ListAsync(ownerA, _fixture.WorkspaceA));
        Assert.Single(Items(mine));
        Assert.Equal(1, mine.GetProperty("totalCount").GetInt32());

        // And a term only the other workspace's prompt contains matches nothing here.
        var searched = await BodyOf(await ListAsync(ownerA, _fixture.WorkspaceA, "?search=risotto"));
        Assert.Empty(Items(searched));
        Assert.Equal(0, searched.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_list_the_other()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await ListAsync(client, _fixture.WorkspaceB);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- the detail --------------------------------------------------------------------------------------

    /// <summary>
    /// The create's <c>Location</c> header is followed rather than assumed, so the two route strings cannot
    /// drift apart unnoticed — it was a forward reference until this route shipped.
    /// </summary>
    [Fact]
    public async Task A_created_prompt_is_readable_at_its_location_header()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var text = new string('a', 1000);
        var created = await SaveAsync(client, _fixture.WorkspaceA, Manual(text));
        var location = created.Headers.Location!.ToString();

        var response = await client.GetAsync(location, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var body = await BodyOf(response);

        // The whole prompt, where the list carried 200 characters of it and a length.
        Assert.Equal(text, body.GetProperty("text").GetString());
        Assert.Equal(
            (await BodyOf(created)).GetProperty("promptRecordId").GetGuid(),
            body.GetProperty("promptRecordId").GetGuid());

        // Detail has no textPreview and no textLength: a client holding the text can measure it.
        Assert.False(body.TryGetProperty("textPreview", out _));
        Assert.False(body.TryGetProperty("textLength", out _));

        // And the two columns that never leave the server still do not.
        Assert.False(body.TryGetProperty("workspaceId", out _));
        Assert.False(body.TryGetProperty("createdByMembershipId", out _));
    }

    /// <summary>A Viewer may read a prompt in full; it is the workspace's own content.</summary>
    [Fact]
    public async Task A_viewer_may_read_a_prompt_in_full()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var id = (await BodyOf(await SaveAsync(owner, _fixture.WorkspaceB, Manual())))
            .GetProperty("promptRecordId").GetGuid();

        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await DetailAsync(viewer, _fixture.WorkspaceB, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await BodyOf(response)).GetProperty("text").GetString()));
    }

    [Fact]
    public async Task An_unknown_prompt_answers_404_with_the_modules_code()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await DetailAsync(client, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(ContentErrorCodes.PromptNotFound, Code(await BodyOf(response)));
    }

    /// <summary>
    /// An id that is not a Guid never reaches the action: routing answers the same 404, with the edge's generic
    /// code rather than this module's.
    /// </summary>
    /// <remarks>
    /// Recorded as a test because a client branching on <c>code</c> sees two codes for what is one condition to
    /// it. Nothing is disclosed either way, which is why this is documented rather than fixed.
    /// </remarks>
    [Fact]
    public async Task An_id_that_is_not_a_guid_is_refused_by_routing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync($"{PromptsIn(_fixture.WorkspaceA)}/not-a-guid", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Over HTTP, and not merely at the facade: another workspace's prompt and an id that does not exist answer
    /// with the same status, the same code, the same title and the same body.
    /// </summary>
    /// <remarks>
    /// The status alone would not be the coverage worth having — a 404 whose body said "that prompt belongs to
    /// another workspace" would disclose exactly what the matching status hides. Everything but
    /// <c>traceId</c>, which is per-request by design, is compared.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_prompt_answers_exactly_as_an_unknown_one_does()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var theirs = (await BodyOf(await SaveAsync(ownerB, _fixture.WorkspaceB, Manual("Theirs, about risotto."))))
            .GetProperty("promptRecordId").GetGuid();

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var borrowed = await DetailAsync(ownerA, _fixture.WorkspaceA, theirs);
        var unknown = await DetailAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, borrowed.StatusCode);
        Assert.Equal(unknown.StatusCode, borrowed.StatusCode);

        var borrowedBody = await BodyOf(borrowed);
        var unknownBody = await BodyOf(unknown);

        Assert.Equal(ContentErrorCodes.PromptNotFound, Code(borrowedBody));
        Assert.Equal(Code(unknownBody), Code(borrowedBody));
        Assert.Equal(WithoutTraceId(unknownBody), WithoutTraceId(borrowedBody));

        // And the one thing an equality check cannot see: that the shared wording discloses nothing.
        Assert.DoesNotContain(
            "workspace",
            borrowedBody.GetProperty("title").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A non-member is refused the route before the prompt id is ever looked at, and an inaccessible workspace
    /// answers as an unknown one does.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="A_member_of_one_workspace_cannot_list_the_other"/> for the detail route:
    /// the <c>WorkspaceViewer</c> policy is what makes it so, and a route added without it would still pass
    /// every test above, because A's own id is not in B's library either. The prompt id here is B's real one,
    /// so the refusal cannot be the prompt being absent.
    /// </remarks>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_the_others_prompt_by_route()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var theirs = (await BodyOf(await SaveAsync(ownerB, _fixture.WorkspaceB, Manual())))
            .GetProperty("promptRecordId").GetGuid();

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var refused = await DetailAsync(ownerA, _fixture.WorkspaceB, theirs);
        var unknownWorkspace = await ownerA.GetAsync(
            $"/api/v1/workspaces/no-such-workspace/prompts/{theirs}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(unknownWorkspace.StatusCode, refused.StatusCode);
    }

    /// <summary>
    /// A prompt body is private creator content, so the detail response must not be cacheable by anything
    /// between the creator and the API — this is the route that carries the whole prompt.
    /// </summary>
    [Fact]
    public async Task The_detail_response_carries_no_shared_cache_permission()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = (await BodyOf(await SaveAsync(client, _fixture.WorkspaceA, Manual())))
            .GetProperty("promptRecordId").GetGuid();

        var response = await DetailAsync(client, _fixture.WorkspaceA, id);

        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.False(response.Headers.CacheControl!.Public);
    }

    // ---- the text download -------------------------------------------------------------------------------

    /// <summary>
    /// PRM-004 over HTTP: the file a creator actually receives, and every header that decides what a browser
    /// does with it.
    /// </summary>
    [Fact]
    public async Task A_prompt_downloads_as_a_plain_text_file()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        const string text = "Overhead shot of soda bread on linen, soft window light.";
        var id = await SavedIdAsync(client, _fixture.WorkspaceA, Manual(text));

        var response = await TextAsync(client, _fixture.WorkspaceA, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The charset is part of the contract: a prompt is creator text and may be anything but ASCII.
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());

        // Offered as a file, under the label slugged with the moment it was saved. The raw header is read so
        // the quoting is seen as it was sent rather than as a parser tidied it.
        // It needs no quotes, and that is the guarantee rather than an omission: the whole name is a header
        // token, so there is nothing in it a parser could end the value early on.
        var disposition = RawDisposition(response);
        Assert.Matches(@"^attachment; filename=soda-bread-hero-\d{8}-\d{6}\.txt$", disposition);

        Assert.Equal("nosniff", response.Headers.NonValidated["X-Content-Type-Options"].Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        // No validator is offered: a file save has no revalidating client, and no-store with an ETag would be
        // telling a client to keep nothing and then how to ask whether it is still fresh.
        Assert.Null(response.Headers.ETag);

        // The body is the prompt and nothing else, and the bytes carry no byte-order mark in front of it.
        Assert.Equal(text, await response.Content.ReadAsStringAsync(Ct));
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal((byte)'O', bytes[0]);
    }

    /// <summary>
    /// The download and the detail route are one read differently represented, so the text must be identical.
    /// </summary>
    [Fact]
    public async Task The_file_is_byte_for_byte_the_text_the_detail_route_publishes()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // Interior line breaks as a creator would paste them, which neither route may normalise, plus
        // characters that have to survive the slug being folded to ASCII without touching the body.
        const string text = "Crème brûlée, overhead.\nSoft window light — linen cloth.";
        var id = await SavedIdAsync(client, _fixture.WorkspaceA, Manual(text));

        var detail = await BodyOf(await DetailAsync(client, _fixture.WorkspaceA, id));
        var file = await (await TextAsync(client, _fixture.WorkspaceA, id)).Content.ReadAsStringAsync(Ct);

        Assert.Equal(text, file);
        Assert.Equal(detail.GetProperty("text").GetString(), file);
    }

    /// <summary>
    /// The one thing a <c>.txt</c> file must not pick up on its way out: the provenance the detail route
    /// publishes and this one deliberately does not.
    /// </summary>
    /// <remarks>
    /// A download is the representation most likely to be "improved" with a header comment naming the channel,
    /// the template or the proposal it came from — which would put workspace-private provenance into a file
    /// that then travels anywhere, and would make PRM-005's JSON record redundant for the wrong reason.
    /// </remarks>
    [Fact]
    public async Task The_file_carries_no_label_channel_or_provenance()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // Text that shares no word with the prompt's own label, channel or source, so the absences below are
        // the response's doing rather than the fixture's.
        const string text = "Three lemons on a marble slab, hard noon light.";
        var id = await SavedIdAsync(client, _fixture.WorkspaceA, Manual(text));

        var body = await (await TextAsync(client, _fixture.WorkspaceA, id)).Content.ReadAsStringAsync(Ct);

        // The whole body, which is what actually forbids a header line — the named absences below cannot be
        // the assertion that carries this test, because the body is the prompt and a prompt may say anything.
        Assert.Equal(text, body);

        // Named anyway, because this is the list of what a well-meaning "add some context to the file" change
        // would reach for, and the equality above is easy to relax by accident while leaving a test passing.
        foreach (var absent in new[] { "instagram", "Hero", "Soda bread hero", "Manual", id.ToString() })
        {
            Assert.DoesNotContain(absent, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_viewer_may_download_a_prompt()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var id = await SavedIdAsync(owner, _fixture.WorkspaceB);

        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await TextAsync(viewer, _fixture.WorkspaceB, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(await response.Content.ReadAsStringAsync(Ct)));
    }

    /// <summary>
    /// A refusal is a problem document, not a file: nothing about it may look downloadable.
    /// </summary>
    [Fact]
    public async Task An_unknown_prompt_download_answers_404_and_offers_no_file()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await TextAsync(client, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(ContentErrorCodes.PromptNotFound, Code(await BodyOf(response)));

        // No attachment, and no name for a browser to save a problem document under.
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    /// <summary>
    /// The download route's version of the detail route's isolation case, which is not implied by it: a second
    /// read of one row is a second place the boundary has to hold.
    /// </summary>
    /// <remarks>
    /// Everything but <c>traceId</c> is compared, because a 404 whose body said "that prompt belongs to another
    /// workspace" would disclose exactly what the matching status hides.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_prompt_cannot_be_downloaded_and_refuses_identically()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var theirs = await SavedIdAsync(ownerB, _fixture.WorkspaceB, Manual("Theirs, about risotto."));

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var borrowed = await TextAsync(ownerA, _fixture.WorkspaceA, theirs);
        var unknown = await TextAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, borrowed.StatusCode);
        Assert.Equal(unknown.StatusCode, borrowed.StatusCode);

        var borrowedBody = await BodyOf(borrowed);
        Assert.Equal(ContentErrorCodes.PromptNotFound, Code(borrowedBody));
        Assert.Equal(WithoutTraceId(await BodyOf(unknown)), WithoutTraceId(borrowedBody));

        // And no part of the neighbour's prompt reached the response, header or body.
        Assert.DoesNotContain("risotto", await borrowed.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.False(borrowed.Content.Headers.Contains("Content-Disposition"));
    }

    /// <summary>
    /// A non-member is refused the download route itself, before the prompt id is looked at.
    /// </summary>
    /// <remarks>
    /// The <c>WorkspaceViewer</c> policy is what makes that so, and a download route added without one would
    /// pass every other test here — A's own prompt ids are not in B's library either, so only B's real id
    /// asked on B's route can tell the difference.
    /// </remarks>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_download_the_others_prompt_by_route()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var theirs = await SavedIdAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var refused = await TextAsync(ownerA, _fixture.WorkspaceB, theirs);
        var unknownWorkspace = await ownerA.GetAsync(
            $"/api/v1/workspaces/no-such-workspace/prompts/{theirs}/text", Ct);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(unknownWorkspace.StatusCode, refused.StatusCode);
        Assert.False(refused.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_cannot_be_downloaded_either()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync($"{PromptsIn(_fixture.WorkspaceA)}/not-a-guid/text", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Nothing a caller sends can change the file, and in particular nothing can name it.
    /// </summary>
    /// <remarks>
    /// The name is composed from the row, so there is no parameter to bind — which is the point: a download
    /// route that accepted a filename would let a caller choose the extension a browser opens the body with.
    /// </remarks>
    [Theory]
    [InlineData("?filename=../../etc/passwd")]
    [InlineData("?filename=owned.html")]
    [InlineData("?workspaceId=00000000-0000-0000-0000-000000000002")]
    [InlineData("?contentType=text/html")]
    public async Task No_query_parameter_can_change_the_file_or_its_name(string query)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = await SavedIdAsync(client, _fixture.WorkspaceA);

        var plain = await TextAsync(client, _fixture.WorkspaceA, id);
        var tampered = await TextAsync(client, _fixture.WorkspaceA, id, query);

        Assert.Equal(HttpStatusCode.OK, tampered.StatusCode);
        Assert.Equal(RawDisposition(plain), RawDisposition(tampered));
        Assert.Equal(plain.Content.Headers.ContentType!.ToString(), tampered.Content.Headers.ContentType!.ToString());
        Assert.Equal(
            await plain.Content.ReadAsStringAsync(Ct),
            await tampered.Content.ReadAsStringAsync(Ct));
    }

    // ---- the record download -----------------------------------------------------------------------------

    /// <summary>
    /// PRM-005 over HTTP: the file a creator receives, and every header that decides what happens to it.
    /// </summary>
    [Fact]
    public async Task A_prompt_downloads_as_a_json_document()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = await SavedIdAsync(client, _fixture.WorkspaceA);

        var response = await RecordAsync(client, _fixture.WorkspaceA, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());

        // The same name the text download offers, with a different extension. Unquoted because the whole name
        // is a header token by construction.
        Assert.Matches(@"^attachment; filename=soda-bread-hero-\d{8}-\d{6}\.json$", RawDisposition(response));

        Assert.Equal("nosniff", response.Headers.NonValidated["X-Content-Type-Options"].Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Null(response.Headers.ETag);

        var body = await response.Content.ReadAsStringAsync(Ct);

        // Served exactly as the domain wrote it: declared version first, indented, newline-only endings, and
        // no byte-order mark. Content rather than Ok is what keeps MVC from re-encoding all three.
        Assert.StartsWith("{\n  \"schemaVersion\": \"prompt.record.v1\",", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exported document and the detail response describe one prompt, so they must agree field for field.
    /// </summary>
    /// <remarks>
    /// Proved over HTTP rather than only at the writer, because this is where the two shapes are actually
    /// published and where a serializer option on one side could diverge from the other. The timestamps are
    /// compared as instants: the export normalizes to UTC at full precision and the response carries an
    /// offset, so the strings differ by design while the moment may not.
    /// </remarks>
    [Fact]
    public async Task The_exported_document_agrees_with_the_detail_response_field_for_field()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = await SavedIdAsync(client, _fixture.WorkspaceA);

        var detail = JsonNode.Parse(
            await (await DetailAsync(client, _fixture.WorkspaceA, id)).Content.ReadAsStringAsync(Ct))!.AsObject();
        var exported = JsonNode.Parse(
            await (await RecordAsync(client, _fixture.WorkspaceA, id)).Content.ReadAsStringAsync(Ct))!
            .AsObject()["prompt"]!.AsObject();

        Assert.Equal(
            detail.Select(pair => pair.Key).ToList(),
            exported.Select(pair => pair.Key).ToList());

        foreach (var (key, value) in detail.Where(pair => pair.Key != "createdAt"))
        {
            Assert.Equal(value?.ToJsonString(), exported[key]?.ToJsonString());
        }

        Assert.Equal(
            DateTimeOffset.Parse(detail["createdAt"]!.GetValue<string>()).ToUniversalTime(),
            DateTimeOffset.Parse(exported["createdAt"]!.GetValue<string>()).ToUniversalTime());
    }

    [Fact]
    public async Task A_viewer_may_export_a_prompt()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var id = await SavedIdAsync(owner, _fixture.WorkspaceB);

        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await RecordAsync(viewer, _fixture.WorkspaceB, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_prompt_export_answers_404_and_offers_no_file()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await RecordAsync(client, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(ContentErrorCodes.PromptNotFound, Code(await BodyOf(response)));

        // A problem document is not a download, and `application/json` on this route must not be mistaken for
        // an export: the refusal declares problem+json and offers no name to save it under.
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    /// <summary>
    /// The export route's own isolation case: a third read of one row is a third place the boundary must hold.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_prompt_cannot_be_exported_and_refuses_identically()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var theirs = await SavedIdAsync(ownerB, _fixture.WorkspaceB, Manual("Theirs, about risotto."));

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var borrowed = await RecordAsync(ownerA, _fixture.WorkspaceA, theirs);
        var unknown = await RecordAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, borrowed.StatusCode);
        Assert.Equal(unknown.StatusCode, borrowed.StatusCode);

        var borrowedBody = await BodyOf(borrowed);
        Assert.Equal(ContentErrorCodes.PromptNotFound, Code(borrowedBody));
        Assert.Equal(WithoutTraceId(await BodyOf(unknown)), WithoutTraceId(borrowedBody));

        Assert.DoesNotContain(
            "risotto", await borrowed.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.False(borrowed.Content.Headers.Contains("Content-Disposition"));
    }

    /// <summary>
    /// A non-member is refused the export route itself, before the prompt id is looked at.
    /// </summary>
    /// <remarks>
    /// B's real id on B's route, because that is the only request A's own ids cannot imitate — the
    /// <c>WorkspaceViewer</c> policy is what refuses it, and a route shipped without one would pass every
    /// other test in this section.
    /// </remarks>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_export_the_others_prompt_by_route()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var theirs = await SavedIdAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var refused = await RecordAsync(ownerA, _fixture.WorkspaceB, theirs);
        var unknownWorkspace = await ownerA.GetAsync(
            $"/api/v1/workspaces/no-such-workspace/prompts/{theirs}/record", Ct);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(unknownWorkspace.StatusCode, refused.StatusCode);
        Assert.False(refused.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_cannot_be_exported_either()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync($"{PromptsIn(_fixture.WorkspaceA)}/not-a-guid/record", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Nothing a caller sends can change the document, its name, or the shape it is written in.
    /// </summary>
    /// <remarks>
    /// `schemaVersion` is the server's to declare, so there is deliberately no parameter to ask for an older
    /// or a wider shape — a download that honoured one would be two contracts behind one route.
    /// </remarks>
    [Theory]
    [InlineData("?filename=../../etc/passwd")]
    [InlineData("?schemaVersion=prompt.record.v0")]
    [InlineData("?workspaceId=00000000-0000-0000-0000-000000000002")]
    [InlineData("?include=workspaceId,createdByMembershipId")]
    public async Task No_query_parameter_can_change_the_document_or_its_name(string query)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var id = await SavedIdAsync(client, _fixture.WorkspaceA);

        var plain = await RecordAsync(client, _fixture.WorkspaceA, id);
        var tampered = await RecordAsync(client, _fixture.WorkspaceA, id, query);

        Assert.Equal(HttpStatusCode.OK, tampered.StatusCode);
        Assert.Equal(RawDisposition(plain), RawDisposition(tampered));
        Assert.Equal(
            await plain.Content.ReadAsStringAsync(Ct),
            await tampered.Content.ReadAsStringAsync(Ct));
    }

    // ---- the contract ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_prompt_answers_201_with_its_location_and_what_was_stored()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, Manual());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        var id = body.GetProperty("promptRecordId").GetGuid();

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal($"{PromptsIn(_fixture.WorkspaceA)}/{id}", response.Headers.Location!.ToString());
        Assert.Equal("instagram", body.GetProperty("channelKey").GetString());
        Assert.Equal("Hero", body.GetProperty("imageKind").GetString());
        Assert.Equal("Manual", body.GetProperty("source").GetString());
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// A prompt body is private creator content, and this response carries one — so it must not be cacheable by
    /// anything between the creator and the API.
    /// </summary>
    [Fact]
    public async Task The_response_carries_no_shared_cache_permission()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, Manual());

        Assert.True(response.Headers.CacheControl is null or { Public: false });
    }

    [Fact]
    public async Task A_contributing_member_may_save_a_prompt()
    {
        // Workspace A's second member is an Editor, which is above Contributor.
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, Manual());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_not_save_a_prompt()
    {
        // Workspace B's second member is a Viewer, which is below Contributor.
        using var client = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceB, Manual());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task A_malformed_prompt_answers_400_with_the_field_that_is_wrong()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "instagram",
            imageKind = "Hero",
            text = "   ",
            source = "Manual",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.PromptInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("text", out _));
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_prompt_may_pin_the_recipe_and_version_it_was_written_for()
    {
        var recipe = await CreateRecipeAsync(_fixture.WorkspaceA, _fixture.WorkspaceA.OwnerEmail);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "blog",
            imageKind = "ProcessStep",
            text = "The dough just before it goes in, cross cut on top.",
            source = "Manual",
            recipeId = recipe.RecipeId,
            recipeVersionId = recipe.VersionId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(recipe.RecipeId, body.GetProperty("recipeId").GetGuid());
        Assert.Equal(recipe.VersionId, body.GetProperty("recipeVersionId").GetGuid());
    }

    [Fact]
    public async Task A_pin_this_workspace_does_not_have_answers_422()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(
            client, _fixture.WorkspaceA, new
            {
                channelKey = "instagram",
                imageKind = "Hero",
                text = "Overhead shot.",
                source = "Manual",
                recipeId = Guid.NewGuid(),
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, Code(await BodyOf(response)));
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    // ---- replay ------------------------------------------------------------------------------------------

    /// <summary>
    /// The protection that matters for a permanent row: a client whose save committed but whose response was
    /// lost retries and gets the first answer back rather than a second copy of the prompt.
    /// </summary>
    [Fact]
    public async Task A_repeated_key_and_body_replays_the_first_save_rather_than_saving_twice()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await SaveAsync(client, _fixture.WorkspaceA, Manual(), "prompt-key-1");
        var second = await SaveAsync(client, _fixture.WorkspaceA, Manual(), "prompt-key-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        Assert.Equal(
            (await BodyOf(first)).GetProperty("promptRecordId").GetGuid(),
            (await BodyOf(second)).GetProperty("promptRecordId").GetGuid());
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task The_same_key_with_a_different_prompt_is_refused_rather_than_replayed()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        await SaveAsync(client, _fixture.WorkspaceA, Manual(), "prompt-key-2");
        var reused = await SaveAsync(client, _fixture.WorkspaceA, Manual("A different prompt."), "prompt-key-2");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(reused)));
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// Without a key, two identical saves are two prompts. Stated as a test because it is the cost of
    /// <c>KeyRequired: false</c> and a client should know to send a key.
    /// </summary>
    [Fact]
    public async Task Two_saves_with_no_key_are_two_prompts()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        await SaveAsync(client, _fixture.WorkspaceA, Manual());
        await SaveAsync(client, _fixture.WorkspaceA, Manual());

        Assert.Equal(2, await CountAsync(_fixture.WorkspaceA));
    }

    // ---- two workspaces ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_prompt_saved_in_one_workspace_does_not_appear_in_the_other()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        await SaveAsync(ownerA, _fixture.WorkspaceA, Manual());
        await SaveAsync(ownerB, _fixture.WorkspaceB, Manual("Theirs."));

        Assert.Equal(1, await CountAsync(_fixture.WorkspaceA));
        Assert.Equal(1, await CountAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_save_into_the_other()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await SaveAsync(client, _fixture.WorkspaceB, Manual());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceB));
    }

    /// <summary>
    /// A recipe belonging to the other workspace is refused in the same words as a recipe that does not exist,
    /// so a prompt save cannot be used to ask what a neighbour owns.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_is_refused_exactly_as_an_unknown_one_is()
    {
        var theirs = await CreateRecipeAsync(_fixture.WorkspaceB, _fixture.WorkspaceB.OwnerEmail);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var borrowed = await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "instagram",
            imageKind = "Hero",
            text = "Overhead shot.",
            source = "Manual",
            recipeId = theirs.RecipeId,
        });

        var unknown = await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "instagram",
            imageKind = "Hero",
            text = "Overhead shot.",
            source = "Manual",
            recipeId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowed.StatusCode);
        Assert.Equal(unknown.StatusCode, borrowed.StatusCode);

        var borrowedBody = await BodyOf(borrowed);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(Code(unknownBody), Code(borrowedBody));
        Assert.Equal(unknownBody.GetProperty("title").GetString(), borrowedBody.GetProperty("title").GetString());
        Assert.Equal(
            unknownBody.GetProperty("errors").GetRawText(),
            borrowedBody.GetProperty("errors").GetRawText());
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// The same, for the version pin, and over HTTP: pinning this workspace's recipe to the other workspace's
    /// version answers exactly as an invented version id does, down to the `errors` payload.
    /// </summary>
    /// <remarks>
    /// Worth asserting separately from the recipe case because a different facade read resolves it —
    /// <c>GetSnapshotAsync</c> rather than <c>GetDetailAsync</c> — so the two could drift apart.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_version_is_refused_exactly_as_an_unknown_one_is()
    {
        var mine = await CreateRecipeAsync(_fixture.WorkspaceA, _fixture.WorkspaceA.OwnerEmail);
        var theirs = await CreateRecipeAsync(_fixture.WorkspaceB, _fixture.WorkspaceB.OwnerEmail);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var borrowed = await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "instagram",
            imageKind = "Hero",
            text = "Overhead shot.",
            source = "Manual",
            recipeId = mine.RecipeId,
            recipeVersionId = theirs.VersionId,
        });

        var unknown = await SaveAsync(client, _fixture.WorkspaceA, new
        {
            channelKey = "instagram",
            imageKind = "Hero",
            text = "Overhead shot.",
            source = "Manual",
            recipeId = mine.RecipeId,
            recipeVersionId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowed.StatusCode);
        Assert.Equal(unknown.StatusCode, borrowed.StatusCode);

        var borrowedBody = await BodyOf(borrowed);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, Code(borrowedBody));
        Assert.Equal(Code(unknownBody), Code(borrowedBody));
        Assert.Equal(unknownBody.GetProperty("title").GetString(), borrowedBody.GetProperty("title").GetString());
        Assert.Equal(
            unknownBody.GetProperty("errors").GetRawText(),
            borrowedBody.GetProperty("errors").GetRawText());
        Assert.Equal(0, await CountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// An idempotency key is scoped to its workspace: the same key used in the other workspace is a new
    /// request there, not a replay of this one's answer.
    /// </summary>
    [Fact]
    public async Task An_idempotency_key_does_not_cross_the_workspace_boundary()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var mine = await SaveAsync(ownerA, _fixture.WorkspaceA, Manual(), "shared-key");
        var theirs = await SaveAsync(ownerB, _fixture.WorkspaceB, Manual(), "shared-key");

        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.False(theirs.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.NotEqual(
            (await BodyOf(mine)).GetProperty("promptRecordId").GetGuid(),
            (await BodyOf(theirs)).GetProperty("promptRecordId").GetGuid());
    }
}
