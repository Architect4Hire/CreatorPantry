using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>GET .../generated-images/{id}/preview</c>, <c>.../content</c> and <c>DELETE .../{id}</c> through the
/// real Gateway, over an in-memory object store: the three contracts of IMG-005 and IMG-006.
/// </summary>
/// <remarks>
/// <para>
/// Three routes in one file because they are one feature with one disclosure rule, and most of what is
/// worth asserting is that they agree: the same situations are the same 404, and no response carries an
/// address for a staging object.
/// </para>
/// <para>
/// The retrieval assertions are mostly about the <em>name</em> and the <em>type</em>, because that is
/// where a download can lie about what a provider actually returned. The bytes are asserted once, byte
/// for byte.
/// </para>
/// </remarks>
public sealed class GeneratedImageEndpointTests : IAsyncLifetime
{
    private readonly InMemoryPrivateObjectStore _store = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- requesting a generation ------------------------------------------------------------------------

    [Fact]
    public async Task A_request_is_accepted_and_queued_rather_than_generated_in_the_request()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Body(variantCount: 3), "key-1");

        // 202, because nothing is generated here: a worker claims the operation (api-contract.md).
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal("Requested", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("variantCount").GetInt32());
        Assert.Equal(0, body.GetProperty("stagedCount").GetInt32());

        // Nothing about the prompt comes back: it is private creator content and the caller already has it.
        Assert.False(body.TryGetProperty("promptText", out _));
    }

    [Fact]
    public async Task A_repeated_key_returns_the_first_operation_rather_than_buying_a_second()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await PostAsync(client, _fixture.WorkspaceA, Body(), "same-key");
        var second = await PostAsync(client, _fixture.WorkspaceA, Body(), "same-key");

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await OperationIdAsync(first), await OperationIdAsync(second));

        // The unique index is the guarantee: a second row for one key is unrepresentable.
        Assert.Single(await OperationRowsAsync(_fixture.WorkspaceA.Id));
    }

    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Body(), idempotencyKey: null);

        // Required on this route, unlike most: a lost response must never become a second charge.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency.key_required", await CodeAsync(response));
        Assert.Empty(await OperationRowsAsync(_fixture.WorkspaceA.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task A_variant_count_outside_the_policy_is_refused(int variantCount)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Body(variantCount: variantCount), "key-2");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("media.generation.invalid_request", await CodeAsync(response));
        Assert.Empty(await OperationRowsAsync(_fixture.WorkspaceA.Id));
    }

    [Fact]
    public async Task A_request_with_no_prompt_is_refused()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PostAsync(
            client, _fixture.WorkspaceA, new { avoidText = (string?)null, variantCount = 1 }, "key-3");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("media.generation.invalid_request", await CodeAsync(response));
    }

    [Fact]
    public async Task A_viewer_cannot_spend_the_workspaces_generation_budget()
    {
        // Workspace B's member is seeded as a Viewer.
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceB, Body(), "key-4");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await OperationRowsAsync(_fixture.WorkspaceB.Id));
    }

    [Fact]
    public async Task One_workspaces_key_does_not_collide_with_anothers()
    {
        using var a = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var b = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var inA = await PostAsync(a, _fixture.WorkspaceA, Body(), "shared-key");
        var inB = await PostAsync(b, _fixture.WorkspaceB, Body(), "shared-key");

        // The index is (WorkspaceId, IdempotencyKey): a key is a workspace's own, not the platform's.
        Assert.Equal(HttpStatusCode.Accepted, inA.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, inB.StatusCode);
        Assert.NotEqual(await OperationIdAsync(inA), await OperationIdAsync(inB));

        Assert.Single(await OperationRowsAsync(_fixture.WorkspaceA.Id));
        Assert.Single(await OperationRowsAsync(_fixture.WorkspaceB.Id));
    }

    [Fact]
    public async Task A_request_records_the_workspace_from_the_route_rather_than_the_body()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // A body that tries to name a workspace at all: the ViewModel has no such field, so it is ignored
        // by the binder, and the row is stamped from the resolved context (tenancy.md).
        var response = await PostAsync(
            client,
            _fixture.WorkspaceA,
            new { promptText = "soup", variantCount = 1, workspaceId = _fixture.WorkspaceB.Id },
            "key-5");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await OperationRowsAsync(_fixture.WorkspaceA.Id));
        Assert.Empty(await OperationRowsAsync(_fixture.WorkspaceB.Id));
    }

    // ---- retrieval ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_carries_the_bytes_the_type_read_from_them_and_a_name_that_matches()
    {
        var bytes = BrandSourceSampleFiles.Png(width: 64, height: 48);
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0, bytes: bytes);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Content(_fixture.WorkspaceA, image));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // The type established from the bytes at staging, never one a provider declared.
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // Variant zero is the creator's first image, and the extension follows the real media type.
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("generated-1.png", response.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.ETag!.IsWeak is false);
    }

    [Fact]
    public async Task A_preview_is_the_same_response_shown_inline()
    {
        var bytes = BrandSourceSampleFiles.Jpeg(width: 32, height: 32);
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 1, bytes: bytes, mediaType: "image/jpeg");

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Preview(_fixture.WorkspaceA, image));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        // The one difference from the download, and the extension is still the real one.
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("generated-2.jpg", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task A_matching_entity_tag_answers_304_without_the_body()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = await client.GetAsync(Content(_fixture.WorkspaceA, image));
        var tag = first.Headers.ETag!.ToString();

        var second = await client.GetAsync(
            Content(_fixture.WorkspaceA, image), new Dictionary<string, string> { ["If-None-Match"] = tag });

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // Every read handed out was handed back: a 304 releases its lease like any other path.
        Assert.Equal(0, _store.OpenReads);
    }

    // ---- what is not there, and what is not yours ------------------------------------------------------

    [Fact]
    public async Task An_unknown_image_answers_the_not_found_code()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/generated-images/{Guid.NewGuid()}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task Another_workspaces_image_is_the_same_404_as_one_that_does_not_exist()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // The real id of a real image, asked for under the neighbour's slug. Nothing may distinguish this
        // from an id that was never issued, or a caller could count a neighbour's images (tenancy.md).
        foreach (var path in new[] { Content(_fixture.WorkspaceA, image), Preview(_fixture.WorkspaceA, image) })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
        }

        // And the bytes were never opened, let alone sent.
        Assert.Equal(0, _store.OpenReads);
    }

    [Fact]
    public async Task An_image_whose_bytes_retention_already_removed_is_not_found_rather_than_unavailable()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);
        await PurgeRowAsync(image);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Content(_fixture.WorkspaceA, image));

        // Gone for good, so "try again" would be the wrong thing to tell a creator.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task A_row_whose_bytes_cannot_be_read_is_unavailable_rather_than_not_found()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);
        _store.Unavailable = true;

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Content(_fixture.WorkspaceA, image));

        // The image exists and retrying is the remedy. A 404 here would tell a creator their work is gone.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("media.staged_image.unavailable", await CodeAsync(response));
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("workspaces")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_segment_that_is_not_an_image_id_never_reaches_a_store(string segment)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/generated-images/{segment}/content");

        // The route is Guid-constrained, so anything shaped like a path does not route at all — there is
        // no code path from a URL segment to an object key, which is what makes traversal unreachable
        // rather than merely filtered.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _store.OpenReads);
    }

    // ---- declining ------------------------------------------------------------------------------------

    [Fact]
    public async Task Declining_an_image_marks_the_row_and_leaves_the_bytes_for_the_sweep()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceA, image);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await RowAsync(image);
        Assert.Equal(GeneratedImageStatus.Rejected, row.Status);

        // The bytes stay. One system is written to per request, so there is no half-done delete to recover.
        Assert.Null(row.ObjectDeletedAt);
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task Declining_twice_answers_the_same_way()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, image)).StatusCode);

        // A client that retried, or a creator who clicked twice. They asked for the image to be gone and
        // it is, which is the answer they wanted however many times they ask.
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, image)).StatusCode);
        Assert.Equal(GeneratedImageStatus.Rejected, (await RowAsync(image)).Status);
    }

    [Fact]
    public async Task A_kept_image_can_no_longer_be_declined()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0, status: GeneratedImageStatus.Kept);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceA, image);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("media.staged_image.conflict", await CodeAsync(response));
        Assert.Equal(GeneratedImageStatus.Kept, (await RowAsync(image)).Status);
    }

    [Fact]
    public async Task A_viewer_cannot_decline_an_image()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        // Workspace B's member is seeded as a Viewer.
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceB, image);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GeneratedImageStatus.Staged, (await RowAsync(image)).Status);
    }

    [Fact]
    public async Task A_viewer_may_still_read_an_image()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync(Content(_fixture.WorkspaceB, image))).StatusCode);
    }

    [Fact]
    public async Task Declining_another_workspaces_image_changes_nothing()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceA, image);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
        Assert.Equal(GeneratedImageStatus.Staged, (await RowAsync(image)).Status);
    }

    // ---- one key, one request (12.10l) --------------------------------------------------------------------

    /// <summary>
    /// A key names one request. The same key with different words is a second request wearing the first one's
    /// key: answering it with the first operation would hand back pictures of something else.
    /// </summary>
    [Theory]
    [InlineData("a loaf of soda bread on linen", 1)]
    [InlineData("a bowl of soup on a wooden table", 3)]
    public async Task A_key_reused_for_a_different_request_is_refused_and_buys_nothing(string prompt, int variantCount)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await PostAsync(client, _fixture.WorkspaceA, Body(), "reused-key");
        var second = await PostAsync(client, _fixture.WorkspaceA, Body(variantCount, prompt), "reused-key");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, await CodeAsync(second));

        // Still the one operation, and still the first one's.
        var row = Assert.Single(await OperationRowsAsync(_fixture.WorkspaceA.Id));
        Assert.Equal("a bowl of soup on a wooden table", row.PromptText);
        Assert.Equal(1, row.VariantCount);
    }

    [Fact]
    public async Task A_key_reused_with_a_different_avoid_list_is_refused_too()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var body = new { promptText = "a bowl of soup", variantCount = 1, avoidText = "steam" };

        await PostAsync(client, _fixture.WorkspaceA, body, "avoid-key");
        var same = await PostAsync(client, _fixture.WorkspaceA, new { promptText = "  a bowl of soup  ", variantCount = 1, avoidText = " steam " }, "avoid-key");
        var different = await PostAsync(client, _fixture.WorkspaceA, new { promptText = "a bowl of soup", variantCount = 1, avoidText = "spoons" }, "avoid-key");

        // Compared as stored — trimmed — so a retry that differs only in padding is still the same request.
        Assert.Equal(HttpStatusCode.Accepted, same.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
    }

    /// <summary>
    /// A key is one member's. Another member of the same workspace who happens to send it is not handed the
    /// first member's operation.
    /// </summary>
    [Fact]
    public async Task Another_members_request_under_the_same_key_is_refused_rather_than_answered_with_the_first()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var member = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var first = await PostAsync(owner, _fixture.WorkspaceA, Body(), "shared-key");
        var second = await PostAsync(member, _fixture.WorkspaceA, Body(), "shared-key");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, await CodeAsync(second));
        Assert.Single(await OperationRowsAsync(_fixture.WorkspaceA.Id));
    }

    // ---- the proposal a request names (12.10l) -----------------------------------------------------------

    [Fact]
    public async Task A_request_may_name_a_proposal_of_its_own_workspace()
    {
        var proposal = await SeedProposalAsync(_fixture.WorkspaceA);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PostAsync(
            client, _fixture.WorkspaceA, new { promptText = "a bowl of soup", variantCount = 1, aiProposalId = proposal }, "proposal-key");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(proposal, Assert.Single(await OperationRowsAsync(_fixture.WorkspaceA.Id)).AiProposalId);
    }

    /// <summary>
    /// A proposal that is not this workspace's is refused in words — it used to reach the foreign key and come
    /// back as a server error — and a neighbour's is answered exactly as one that does not exist.
    /// </summary>
    [Fact]
    public async Task An_unknown_proposal_and_another_workspaces_are_the_same_refusal_and_queue_nothing()
    {
        var neighbours = await SeedProposalAsync(_fixture.WorkspaceB);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        async Task<string> Refused(Guid proposalId, string key)
        {
            var response = await PostAsync(
                client, _fixture.WorkspaceA, new { promptText = "a bowl of soup", variantCount = 1, aiProposalId = proposalId }, key);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var fields = string.Join(",", body.GetProperty("errors").EnumerateObject().Select(field => $"{field.Name}={field.Value}"));

            return $"{(int)response.StatusCode}|{body.GetProperty("code").GetString()}|{body.GetProperty("title").GetString()}|{fields}";
        }

        var forUnknown = await Refused(Guid.NewGuid(), "unknown-proposal");

        Assert.StartsWith($"422|{MediaErrorCodes.GenerationProposalUnprocessable}|", forUnknown);
        Assert.Contains("aiProposalId=", forUnknown);
        Assert.Equal(forUnknown, await Refused(neighbours, "neighbours-proposal"));
        Assert.Empty(await OperationRowsAsync(_fixture.WorkspaceA.Id));
    }

    // ---- a declined image is not served (12.10l) ---------------------------------------------------------

    /// <summary>
    /// Declined means declined now, not once the sweep has run: the bytes are still in storage, and neither
    /// route serves them. The answer is the one an unknown image gets, so it does not change when they go.
    /// </summary>
    [Theory]
    [InlineData(GeneratedImageStatus.Rejected)]
    [InlineData(GeneratedImageStatus.Expired)]
    public async Task An_image_that_was_declined_or_expired_is_not_served_though_its_bytes_remain(GeneratedImageStatus status)
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0, status: status);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var content = await client.GetAsync(Content(_fixture.WorkspaceA, image), TestContext.Current.CancellationToken);
        var preview = await client.GetAsync(Preview(_fixture.WorkspaceA, image), TestContext.Current.CancellationToken);
        var unknown = await client.GetAsync(Content(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(await CodeAsync(unknown), await CodeAsync(content));

        // Not purged: this is the status deciding, not the sweep having already been.
        Assert.Null((await RowAsync(image)).ObjectDeletedAt);
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task Declining_an_image_stops_it_being_served_at_once()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var before = await client.GetAsync(Preview(_fixture.WorkspaceA, image), TestContext.Current.CancellationToken);
        await DeleteAsync(client, _fixture.WorkspaceA, image);
        var after = await client.GetAsync(Preview(_fixture.WorkspaceA, image), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task A_kept_image_is_still_served()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0, status: GeneratedImageStatus.Kept);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(Preview(_fixture.WorkspaceA, image), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>An AI operation and a proposal from it, inserted directly: no model runs in a test.</summary>
    private async Task<Guid> SeedProposalAsync(SeededWorkspace workspace)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            TaskType = AiTaskType.ImagePrompt,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"generation-proposal-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            AiOperationId = operationId,
            OutputSchemaVersion = "image.prompt.v1",
            PromptTemplateId = "image.prompt",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:" + new string('a', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return proposalId;
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private static object Body(int variantCount = 1, string prompt = "a bowl of soup on a wooden table") =>
        new { promptText = prompt, variantCount };

    private static Task<HttpResponseMessage> PostAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? idempotencyKey) =>
        idempotencyKey is null
            ? client.PostAsJsonAsync(
                $"/api/v1/workspaces/{workspace.Slug}/generated-images",
                body,
                TestContext.Current.CancellationToken)
            : client.PostAsJsonAsync(
                $"/api/v1/workspaces/{workspace.Slug}/generated-images",
                body,
                idempotencyKey,
                TestContext.Current.CancellationToken);

    private static async Task<Guid> OperationIdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

    /// <inheritdoc cref="RowAsync"/>
    private async Task<IReadOnlyList<GeneratedImageOperation>> OperationRowsAsync(Guid workspaceId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .GeneratedImageOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(operation => operation.WorkspaceId == workspaceId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static string Content(SeededWorkspace workspace, Guid imageId) =>
        $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}/content";

    private static string Preview(SeededWorkspace workspace, Guid imageId) =>
        $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}/preview";

    private static Task<HttpResponseMessage> DeleteAsync(
        GatewayClient client, SeededWorkspace workspace, Guid imageId) =>
        client.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}",
            body: null,
            headers: null,
            TestContext.Current.CancellationToken);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("code").GetString();

    /// <summary>
    /// Writes one staged image: the object, then the operation and the row that owns it.
    /// </summary>
    /// <remarks>
    /// Straight to the store and the database rather than through the generation job, because this file is
    /// about what the three routes do with a staged image rather than about how one comes to exist —
    /// <c>GeneratedImageWorkerTests</c> owns that, and staging through it would make every test here
    /// depend on a provider fake it does not care about.
    /// </remarks>
    private async Task<Guid> StageAsync(
        SeededWorkspace workspace,
        int variantIndex,
        byte[]? bytes = null,
        string mediaType = "image/png",
        GeneratedImageStatus status = GeneratedImageStatus.Staged)
    {
        var content = bytes ?? BrandSourceSampleFiles.Png(width: 16, height: 12);
        var operationId = Guid.NewGuid();
        var key = GeneratedImageObjectKey.For(workspace.Id, operationId, variantIndex);

        var write = await _store.PutAsync(
            GeneratedImageObjectKey.Container,
            key,
            new MemoryStream(content),
            mediaType,
            MediaPolicy.ImageMaxBytes,
            TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        var imageId = Guid.NewGuid();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspace.Id,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = MediaPolicy.MaxVariantsPerOperation,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspace.Id,
            GeneratedImageOperationId = operationId,
            VariantIndex = variantIndex,
            Status = status,
            ObjectKey = key,
            MediaType = mediaType,
            Width = 16,
            Height = 12,
            SizeBytes = write.Object!.SizeBytes,
            ContentChecksum = write.Object.ContentChecksum,
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return imageId;
    }

    /// <summary>Puts a row into the state the sweep leaves behind: bytes gone, record kept.</summary>
    private async Task PurgeRowAsync(Guid imageId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var row = await db.GeneratedImages
            .IgnoreQueryFilters()
            .FirstAsync(image => image.Id == imageId, TestContext.Current.CancellationToken);

        row.Status = GeneratedImageStatus.Expired;
        row.ObjectDeletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc cref="PurgeRowAsync"/>
    private async Task<GeneratedImage> RowAsync(Guid imageId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .GeneratedImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(image => image.Id == imageId, TestContext.Current.CancellationToken);
    }
}
