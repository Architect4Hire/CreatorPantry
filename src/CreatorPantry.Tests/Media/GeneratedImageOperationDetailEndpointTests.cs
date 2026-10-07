using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>GET .../generated-images/operations/{operationId}</c> through the real Gateway: the read that makes the
/// <c>202</c> from a generation request usable.
/// </summary>
/// <remarks>
/// <para>
/// Before this route a client had an operation id and no way to learn a single image in it, so progress, a
/// contact sheet, a download and a decline were all unreachable. What is worth asserting is therefore mostly
/// about <em>what a grid needs</em>: one entry per staged image, in variant order, each carrying its own status
/// — and nothing a client has no business holding.
/// </para>
/// <para>
/// Rows are written straight to the database rather than through the generation job, because this file is about
/// what the read publishes rather than about how an image comes to exist. <c>GeneratedImageWorkerTests</c> owns
/// the latter, and staging through it would make every test here depend on a provider fake it does not care
/// about.
/// </para>
/// </remarks>
public sealed class GeneratedImageOperationDetailEndpointTests : IAsyncLifetime
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

    [Fact]
    public async Task It_reads_the_operation_with_everything_a_contact_sheet_needs()
    {
        var operationId = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 4, stagedVariants: [0, 1]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(
            Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyAsync(response);

        Assert.Equal(operationId, body.GetProperty("id").GetGuid());
        Assert.Equal("Running", body.GetProperty("status").GetString());
        Assert.Equal(4, body.GetProperty("variantCount").GetInt32());

        var images = body.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(2, images.Count);

        var first = images[0];
        Assert.Equal(0, first.GetProperty("variantIndex").GetInt32());
        Assert.Equal("Staged", first.GetProperty("status").GetString());
        Assert.Equal("image/png", first.GetProperty("mediaType").GetString());
        Assert.Equal(16, first.GetProperty("width").GetInt32());
        Assert.Equal(12, first.GetProperty("height").GetInt32());
        Assert.True(first.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.True(first.TryGetProperty("retentionExpiresAt", out _));
        Assert.True(first.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Images_appear_as_they_stage_and_the_count_agrees_with_them()
    {
        // A row exists only once there are bytes to describe, so two of four variants means two entries — which
        // is what lets a grid fill in rather than flipping from empty to four.
        var operationId = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 4, stagedVariants: [0, 1]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyAsync(
            await client.GetAsync(Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken));

        Assert.Equal(2, body.GetProperty("stagedCount").GetInt32());
        Assert.Equal(2, body.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task Images_come_back_in_variant_order_however_they_were_inserted()
    {
        // A worker stages variants as the provider returns them, which is not necessarily in order.
        var operationId = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 4, stagedVariants: [3, 0, 2]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyAsync(
            await client.GetAsync(Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken));

        Assert.Equal(
            [0, 2, 3],
            body.GetProperty("images").EnumerateArray()
                .Select(image => image.GetProperty("variantIndex").GetInt32())
                .ToArray());
    }

    [Fact]
    public async Task Every_image_reports_its_own_status_rather_than_being_hidden()
    {
        var operationId = await SeedOperationAsync(
            _fixture.WorkspaceA,
            variantCount: 3,
            stagedVariants: [0, 1, 2],
            statuses: new()
            {
                [1] = GeneratedImageStatus.Rejected,
                [2] = GeneratedImageStatus.Expired,
            });
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyAsync(
            await client.GetAsync(Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken));

        // A creator who generated four and declined three should still see that they did, so a declined or
        // expired image is reported as itself rather than left out and rendered as a hole.
        Assert.Equal(
            ["Staged", "Rejected", "Expired"],
            body.GetProperty("images").EnumerateArray()
                .Select(image => image.GetProperty("status").GetString())
                .ToArray());
    }

    [Fact]
    public async Task A_failed_operation_carries_why_it_failed()
    {
        var operationId = await SeedOperationAsync(
            _fixture.WorkspaceA,
            variantCount: 2,
            stagedVariants: [],
            status: GeneratedImageOperationStatus.Failed,
            failureCategory: "Provider",
            failureSummary: "The image service refused the prompt.");
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyAsync(
            await client.GetAsync(Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken));

        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal("Provider", body.GetProperty("failureCategory").GetString());
        Assert.Equal("The image service refused the prompt.", body.GetProperty("failureSummary").GetString());
        Assert.Equal(0, body.GetProperty("images").GetArrayLength());
    }
    [Fact]
    public async Task It_publishes_no_prompt_no_key_and_no_checksum()
    {
        var operationId = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 2, stagedVariants: [0]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(
            Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(raw).RootElement;

        // The prompt is private creator content the caller already has; the key and the checksum are, or become,
        // addresses. The repository projects rather than reading entities, so none of them is even loaded.
        Assert.False(body.TryGetProperty("promptText", out _));
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentChecksum", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PromptText, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratedImageObjectKey.Container, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_is_never_cached()
    {
        var operationId = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 1, stagedVariants: [0]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(
            Url(_fixture.WorkspaceA, operationId), TestContext.Current.CancellationToken);

        // An operation in flight is different on the next read, and what it names is private creator content.
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_viewer_may_read_what_they_may_already_look_at()
    {
        // The preview and download routes are Viewer, so a member who could see an image but not learn that it
        // exists would be a contract at odds with itself.
        var operationId = await SeedOperationAsync(_fixture.WorkspaceB, variantCount: 1, stagedVariants: [0]);
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await viewer.GetAsync(
            Url(_fixture.WorkspaceB, operationId), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_operation_answers_one_not_found()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(
            Url(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MediaErrorCodes.GenerationOperationNotFound, await CodeAsync(response));
    }

    [Fact]
    public async Task A_neighbours_operation_answers_exactly_the_same_not_found()
    {
        var mine = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 1, stagedVariants: [0]);
        var theirs = await SeedOperationAsync(_fixture.WorkspaceB, variantCount: 1, stagedVariants: [0]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var unknown = await client.GetAsync(
            Url(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);
        var neighbours = await client.GetAsync(
            Url(_fixture.WorkspaceA, theirs), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, neighbours.StatusCode);

        // The whole body, not just the code: "exactly the same" is what this test is named for, and comparing one
        // field would pass while a message or an extension gave the neighbour away. `traceId` is per-request and
        // is the only thing allowed to differ.
        Assert.Equal(await ProblemWithoutTraceAsync(unknown), await ProblemWithoutTraceAsync(neighbours));

        // Mine is still readable, so the isolation is the filter rather than an empty database.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(Url(_fixture.WorkspaceA, mine), TestContext.Current.CancellationToken))
                .StatusCode);
    }

    [Fact]
    public async Task A_slug_the_caller_is_not_in_answers_the_same_as_a_slug_that_does_not_exist()
    {
        // The slug-level refusal, which is the one that matters for existence: a workspace a caller is not in must
        // be indistinguishable from one that was never created (tenancy.md).
        var theirs = await SeedOperationAsync(_fixture.WorkspaceB, variantCount: 1, stagedVariants: [0]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var inaccessible = await client.GetAsync(
            Url(_fixture.WorkspaceB, theirs), TestContext.Current.CancellationToken);
        var nonexistent = await client.GetAsync(
            $"/api/v1/workspaces/no-such-workspace-at-all/generated-images/operations/{theirs}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
        Assert.Equal(await ProblemWithoutTraceAsync(nonexistent), await ProblemWithoutTraceAsync(inaccessible));
    }

    [Fact]
    public async Task A_viewer_of_one_workspace_learns_nothing_about_another()
    {
        var mine = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 1, stagedVariants: [0]);
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await viewer.GetAsync(
            Url(_fixture.WorkspaceA, mine), TestContext.Current.CancellationToken);

        // Viewer is the lowest role that may read — in their own workspace. It buys nothing in anyone else's.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_refusal_is_no_more_cacheable_than_an_answer()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(
            Url(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        // A cached 404 would be a small signal about which ids exist, so the header is set before the branch.
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_neighbours_image_is_not_listed_under_my_operation()
    {
        var mine = await SeedOperationAsync(_fixture.WorkspaceA, variantCount: 2, stagedVariants: [0]);
        await SeedOperationAsync(_fixture.WorkspaceB, variantCount: 2, stagedVariants: [0, 1]);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyAsync(
            await client.GetAsync(Url(_fixture.WorkspaceA, mine), TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("images").GetArrayLength());
    }
    // ---- helpers ----------------------------------------------------------------------------------------

    private const string PromptText = "a bowl of soup on a scrubbed oak table in side light";

    private static string Url(SeededWorkspace workspace, Guid operationId) =>
        $"/api/v1/workspaces/{workspace.Slug}/generated-images/operations/{operationId}";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>
    /// One refusal's body with <c>traceId</c> removed, so two refusals can be compared in full.
    /// </summary>
    /// <remarks>
    /// `traceId` is per-request and is the only field two otherwise identical refusals may differ in. Everything
    /// else — status, title, detail, code, any extension — has to match, or one of them says something about the
    /// other's existence.
    /// </remarks>
    private static async Task<string> ProblemWithoutTraceAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(
            TestContext.Current.CancellationToken);

        return string.Join(
            " | ",
            (body ?? [])
                .Where(pair => pair.Key is not "traceId")
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.GetRawText()}"));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("code").GetString();

    /// <summary>
    /// Writes one operation and a row per staged variant, with the object behind each.
    /// </summary>
    /// <param name="stagedVariants">
    /// Which variants have landed, in the order the rows are inserted — so a test can insert out of order and
    /// assert the read sorts them.
    /// </param>
    /// <param name="statuses">A per-variant status, for the variants that are not plain `Staged`.</param>
    private async Task<Guid> SeedOperationAsync(
        SeededWorkspace workspace,
        int variantCount,
        int[] stagedVariants,
        Dictionary<int, GeneratedImageStatus>? statuses = null,
        GeneratedImageOperationStatus status = GeneratedImageOperationStatus.Running,
        string? failureCategory = null,
        string? failureSummary = null)
    {
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var content = BrandSourceSampleFiles.Png(width: 16, height: 12);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspace.Id,
            Status = status,
            PromptText = PromptText,
            VariantCount = variantCount,
            ProviderName = "fake",
            ModelName = "fake-image-1",
            FailureCategory = failureCategory,
            FailureSummary = failureSummary,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        foreach (var variantIndex in stagedVariants)
        {
            var key = GeneratedImageObjectKey.For(workspace.Id, operationId, variantIndex);

            var write = await _store.PutAsync(
                GeneratedImageObjectKey.Container,
                key,
                new MemoryStream(content),
                "image/png",
                MediaPolicy.ImageMaxBytes,
                TestContext.Current.CancellationToken);

            db.GeneratedImages.Add(new GeneratedImage
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                GeneratedImageOperationId = operationId,
                VariantIndex = variantIndex,
                Status = statuses?.GetValueOrDefault(variantIndex, GeneratedImageStatus.Staged)
                    ?? GeneratedImageStatus.Staged,
                ObjectKey = key,
                MediaType = "image/png",
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
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return operationId;
    }
}
