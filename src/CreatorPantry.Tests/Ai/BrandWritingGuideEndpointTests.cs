using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// <c>GET .../brand-writing-guide</c> through the real Gateway (11A.21a): which guide a writing screen would
/// use, whether it is stale, and what it would contribute to the task — and that none of it crosses workspaces.
/// </summary>
public sealed class BrandWritingGuideEndpointTests : IAsyncLifetime
{
    private const string ViewerEmail = "writing-guide-viewer-a@example.com";

    private const string Password = "correct horse battery";

    private static readonly DateTimeOffset Moment = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(new InMemoryPrivateObjectStore());
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
        });

        var userId = await _fixture.Api.CreateUserAsync(ViewerEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Viewer,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- No guide ----

    [Fact]
    public async Task A_workspace_with_no_active_guide_gets_an_answer_not_an_error()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await GetAsync(client, _fixture.WorkspaceA, "editorial-package");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeGuide").ValueKind);
        Assert.Empty(body.GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task A_guide_that_is_created_but_not_activated_is_not_offered()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Draft only", questionnaire = new { voice = "Warm." } });

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "editorial-package"));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeGuide").ValueKind);
    }

    // ---- The active guide ----

    [Fact]
    public async Task The_active_guide_is_named_with_its_version_and_approval_and_is_not_stale()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Warm kitchen voice",
            questionnaire = new { voice = "Warm and plain.", tone = "Friendly." },
        });

        var guide = (await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "editorial-package"))).GetProperty("activeGuide");

        Assert.Equal("Warm kitchen voice", guide.GetProperty("name").GetString());
        Assert.Equal(1, guide.GetProperty("versionNumber").GetInt32());
        Assert.Equal(Moment, guide.GetProperty("approvedAt").GetDateTimeOffset());
        Assert.False(guide.GetProperty("isStale").GetBoolean());
        Assert.Equal(0, guide.GetProperty("staleSourceCount").GetInt32());
        Assert.Contains("Voice", Strings(guide.GetProperty("appliedSections")));
    }

    [Fact]
    public async Task Each_task_gets_what_generation_would_send_for_it_and_no_more()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "Warm." },
            sections = new object[] { new { sectionKey = "Storytelling", body = "Open with a memory." } },
        });

        var editorial = await LabelsAsync(client, "editorial-package");
        var seo = await LabelsAsync(client, "seo-package");

        // Storytelling is long-form guidance; an SEO package is metadata, not a narrative.
        Assert.Contains("Storytelling", editorial);
        Assert.DoesNotContain("Storytelling", seo);
        Assert.Contains("Voice", seo);
    }

    [Fact]
    public async Task The_preview_is_the_creators_own_words_shortened_to_a_line()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var long_ = string.Join(' ', Enumerable.Repeat("conversational", 40));
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Long", questionnaire = new { voice = long_ } });

        var voice = Assert.Single((await RulesAsync(client, "editorial-package")), rule => rule.Label == "Voice");

        Assert.EndsWith("…", voice.Summary);
        Assert.True(voice.Summary.Length <= 161);
        Assert.StartsWith("conversational conversational", voice.Summary);
    }

    [Fact]
    public async Task Guide_rules_appear_as_do_and_dont_lines()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Rules",
            questionnaire = new { alwaysDo = new[] { "Lead with the dish." }, neverDo = new[] { "Use exclamation marks." } },
        });

        var rules = await RulesAsync(client, "editorial-package");

        Assert.Contains(rules, rule => rule is { Label: "Do", Summary: "Lead with the dish." });
        Assert.Contains(rules, rule => rule is { Label: "Don't", Summary: "Use exclamation marks." });
    }

    [Fact]
    public async Task A_channel_adds_that_channels_own_guidance_under_its_display_name()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Channels",
            questionnaire = new { voice = "Warm." },
            sections = new object[] { new { sectionKey = "ChannelVariant", channelKey = "instagram", body = "Short and playful." } },
        });

        var withChannel = await RulesAsync(client, "editorial-package", "instagram");
        var without = await RulesAsync(client, "editorial-package");

        Assert.Contains(withChannel, rule => rule is { Label: "Instagram", Summary: "Short and playful." });
        Assert.DoesNotContain(without, rule => rule.Label == "Instagram");
    }

    // ---- Staleness ----

    [Fact]
    public async Task A_guide_is_stale_once_a_source_it_cites_has_been_replaced_and_is_still_the_active_one()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var document = await UploadAsync(client, _fixture.WorkspaceA, "house-style.pdf");
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Sourced",
            sourceDocuments = new[] { new { documentId = document.Id, versionNumber = 1 } },
        });

        var before = (await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "editorial-package"))).GetProperty("activeGuide");
        Assert.False(before.GetProperty("isStale").GetBoolean());

        await ReplaceAsync(client, _fixture.WorkspaceA, document);

        var after = (await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "editorial-package"))).GetProperty("activeGuide");

        // Reported, never acted on: the guide is still the one the workspace activated.
        Assert.Equal("Sourced", after.GetProperty("name").GetString());
        Assert.True(after.GetProperty("isStale").GetBoolean());
        Assert.Equal(1, after.GetProperty("staleSourceCount").GetInt32());
    }

    [Fact]
    public async Task One_workspaces_replaced_source_never_makes_another_workspaces_guide_stale()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var inA = await UploadAsync(ownerA, _fixture.WorkspaceA, "a-style.pdf");
        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB, "b-style.pdf");

        await ActivatedGuideAsync(ownerA, _fixture.WorkspaceA, new
        {
            displayName = "A voice",
            sourceDocuments = new[] { new { documentId = inA.Id, versionNumber = 1 } },
        });
        await ActivatedGuideAsync(ownerB, _fixture.WorkspaceB, new
        {
            displayName = "B voice",
            sourceDocuments = new[] { new { documentId = inB.Id, versionNumber = 1 } },
        });

        // Only B's source moves on. Both workspaces have an active guide, so a wrong selection or a count that
        // leaked across the join would show up as the two answers agreeing.
        await ReplaceAsync(ownerB, _fixture.WorkspaceB, inB);

        var a = (await BodyOf(await GetAsync(ownerA, _fixture.WorkspaceA, "editorial-package"))).GetProperty("activeGuide");
        var b = (await BodyOf(await GetAsync(ownerB, _fixture.WorkspaceB, "editorial-package"))).GetProperty("activeGuide");

        Assert.Equal("A voice", a.GetProperty("name").GetString());
        Assert.False(a.GetProperty("isStale").GetBoolean());
        Assert.Equal(0, a.GetProperty("staleSourceCount").GetInt32());

        Assert.Equal("B voice", b.GetProperty("name").GetString());
        Assert.True(b.GetProperty("isStale").GetBoolean());
        Assert.Equal(1, b.GetProperty("staleSourceCount").GetInt32());
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("")]
    [InlineData("?task=recipe-review")]
    [InlineData("?task=image-prompt")]
    [InlineData("?task=EditorialPackage")]
    [InlineData("?task=editorial-package&channel=myspace")]
    public async Task A_task_that_is_not_grounded_in_brand_voice_or_an_unknown_channel_is_refused(string query)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.GetAsync(Route(_fixture.WorkspaceA) + query, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ai.brandWritingGuide.invalid_request", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Roles and isolation ----

    [Fact]
    public async Task A_viewer_may_read_it()
    {
        using var viewer = await _fixture.SignInAsync(ViewerEmail, Password, TestContext.Current.CancellationToken);

        var response = await GetAsync(viewer, _fixture.WorkspaceA, "seo-package");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task One_workspaces_guide_never_appears_in_another()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        await ActivatedGuideAsync(ownerB, _fixture.WorkspaceB, new
        {
            displayName = "B's secret voice",
            questionnaire = new { voice = "B-only wording." },
        });

        // A has activated nothing, and B's guide is invisible to it — by name, rule text and count.
        var seenByA = await GetAsync(ownerA, _fixture.WorkspaceA, "editorial-package");
        var textA = await seenByA.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, seenByA.StatusCode);
        Assert.DoesNotContain("secret", textA);
        Assert.DoesNotContain("B-only", textA);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(textA).RootElement.GetProperty("activeGuide").ValueKind);

        var seenByB = await BodyOf(await GetAsync(ownerB, _fixture.WorkspaceB, "editorial-package"));
        Assert.Equal("B's secret voice", seenByB.GetProperty("activeGuide").GetProperty("name").GetString());

        // A cannot reach B's route at all, and that is indistinguishable from a workspace that does not exist.
        var crossing = await GetAsync(ownerA, _fixture.WorkspaceB, "editorial-package");
        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);

        var missing = await ownerA.GetAsync(
            "/api/v1/workspaces/no-such-workspace/brand-writing-guide?task=editorial-package",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            (await BodyOf(missing)).GetProperty("code").GetString(),
            (await BodyOf(crossing)).GetProperty("code").GetString());
    }

    // ---- Harness ----

    private sealed record SeededGuide(Guid Id, Guid VersionId);

    private sealed record SeededDocument(Guid Id, string Token);

    private sealed record RuleLine(string Label, string Summary);

    private static string Route(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-writing-guide";

    private static string GuidesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> GetAsync(
        GatewayClient client, SeededWorkspace workspace, string task, string? channel = null) =>
        client.GetAsync(
            $"{Route(workspace)}?task={task}" + (channel is null ? string.Empty : $"&channel={channel}"),
            TestContext.Current.CancellationToken);

    private async Task<List<RuleLine>> RulesAsync(GatewayClient client, string task, string? channel = null)
    {
        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, task, channel));

        return [.. body.GetProperty("rules").EnumerateArray().Select(rule =>
            new RuleLine(rule.GetProperty("label").GetString()!, rule.GetProperty("summary").GetString()!))];
    }

    private async Task<List<string>> LabelsAsync(GatewayClient client, string task) =>
        [.. (await RulesAsync(client, task)).Select(rule => rule.Label)];

    private async Task<SeededGuide> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededGuide(
            created.GetProperty("id").GetGuid(), created.GetProperty("version").GetProperty("id").GetGuid());
    }

    /// <summary>Creates a guide, then approves and activates its first version — what 11A.16c will do over HTTP.</summary>
    private async Task<SeededGuide> ActivatedGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var guide = await CreateGuideAsync(client, workspace, body);

        await InScopeAsync(workspace, async db =>
        {
            db.BrandStyleGuideApprovals.Add(new BrandStyleGuideApproval
            {
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = guide.VersionId,
                Reason = "Signed off",
                ApprovedByMembershipId = Guid.NewGuid(),
                ApprovedAt = Moment,
            });
            db.BrandStyleGuideDefaults.Add(new BrandStyleGuideDefault
            {
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = guide.VersionId,
                Reason = "Launch voice",
                ActivatedByMembershipId = Guid.NewGuid(),
                ActivatedAt = Moment.AddHours(1),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

        return guide;
    }

    private async Task<SeededDocument> UploadAsync(GatewayClient client, SeededWorkspace workspace, string fileName)
    {
        var response = await client.PostAsync(
            SourcesIn(workspace), FileForm(fileName), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededDocument(
            created.GetProperty("id").GetGuid(), created.GetProperty("concurrencyToken").GetString()!);
    }

    private async Task ReplaceAsync(GatewayClient client, SeededWorkspace workspace, SeededDocument document)
    {
        var form = FileForm("house-style-v2.pdf", "the second file", metadata: false);
        form.Add(new StringContent(document.Token), "expectedConcurrencyToken");

        var response = await client.PostAsync(
            $"{SourcesIn(workspace)}/{document.Id}/versions",
            form,
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static MultipartFormDataContent FileForm(string fileName, string contents = "house style", bool metadata = true)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf(contents));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);

        if (metadata)
        {
            form.Add(new StringContent(fileName), "title");
            form.Add(new StringContent("StyleGuide"), "documentType");
            form.Add(new StringContent("Voice"), "purpose");
        }

        return form;
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
