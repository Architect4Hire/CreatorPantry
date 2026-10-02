using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
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
/// <c>GET .../brand-style-guides/{guideId}</c> through the real Gateway: the working version, the active
/// version only when the workspace default is one of the guide's own, the empty guide, and that an unknown
/// guide and another workspace's are the same answer.
/// </summary>
/// <remarks>
/// Versions after the first, approvals and the workspace default are seeded directly: nothing in the API
/// writes them yet (11A.15 edits, 11A.16c activates), and the read has to be right about them before it does.
/// </remarks>
public sealed class BrandStyleGuideReadEndpointTests : IAsyncLifetime
{
    private const string ViewerEmail = "guide-viewer-a@example.com";

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

    private static string GuidesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private async Task<JsonElement> CreateAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private Task<HttpResponseMessage> GetAsync(GatewayClient client, SeededWorkspace workspace, Guid guideId) =>
        client.GetAsync($"{GuidesIn(workspace)}/{guideId}", TestContext.Current.CancellationToken);

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>Approves a version and makes it the workspace default, as 11A.16c will.</summary>
    private Task ActivateAsync(SeededWorkspace workspace, Guid versionId, string reason) =>
        InScopeAsync(workspace, async db =>
        {
            db.BrandStyleGuideApprovals.Add(new BrandStyleGuideApproval
            {
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = versionId,
                Reason = "Signed off",
                ApprovedByMembershipId = Guid.NewGuid(),
                ApprovedAt = Moment,
            });
            db.BrandStyleGuideDefaults.Add(new BrandStyleGuideDefault
            {
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = versionId,
                Reason = reason,
                ActivatedByMembershipId = Guid.NewGuid(),
                ActivatedAt = Moment.AddHours(1),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

    /// <summary>Adds a later version, as an edit will, and returns its id.</summary>
    private Task<Guid> AddVersionAsync(SeededWorkspace workspace, Guid guideId, int number, Guid parentId, string voice) =>
        InScopeAsync(workspace, async db =>
        {
            var version = new BrandStyleGuideVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                BrandStyleGuideId = guideId,
                VersionNumber = number,
                ParentVersionId = parentId,
                ChangeReason = $"Revision {number}",
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Moment.AddDays(number),
            };
            version.Sections.Add(new BrandStyleGuideSection
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = version.Id,
                SectionKey = BrandStyleGuideSectionKey.Voice,
                ChannelKey = string.Empty,
                Body = voice,
            });
            db.BrandStyleGuideVersions.Add(version);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return version.Id;
        });

    // ---- Current ----

    [Fact]
    public async Task A_new_guide_reads_back_as_created_with_no_active_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            purpose = "Everything we publish",
            questionnaire = new { voice = "Warm.", alwaysDo = new[] { "Lead with the dish" }, neverDo = new[] { "Shout" } },
            sections = new object[] { new { sectionKey = "ChannelVariant", channelKey = "instagram", body = "Short." } },
        });
        var guideId = created.GetProperty("id").GetGuid();

        var response = await GetAsync(client, _fixture.WorkspaceA, guideId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);
        Assert.Equal(guideId, body.GetProperty("id").GetGuid());
        Assert.Equal("House voice", body.GetProperty("displayName").GetString());
        Assert.Equal("Everything we publish", body.GetProperty("purpose").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("archivedAt").ValueKind);
        Assert.Equal(created.GetProperty("concurrencyToken").GetString(), body.GetProperty("concurrencyToken").GetString());

        var working = body.GetProperty("workingVersion");
        var createdVersion = created.GetProperty("version");
        Assert.Equal(createdVersion.GetProperty("id").GetGuid(), working.GetProperty("id").GetGuid());
        Assert.Equal(1, working.GetProperty("versionNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, working.GetProperty("parentVersionNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, working.GetProperty("approval").ValueKind);
        Assert.Equal(JsonValueKind.Null, working.GetProperty("activation").ValueKind);
        Assert.Equal(createdVersion.GetProperty("sections").GetRawText(), working.GetProperty("sections").GetRawText());
        Assert.Equal(createdVersion.GetProperty("rules").GetRawText(), working.GetProperty("rules").GetRawText());

        // No default exists, so nothing is active — and nothing stands in for it.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeVersion").ValueKind);
    }

    [Fact]
    public async Task The_working_version_is_the_highest_number_and_the_active_one_is_the_default_if_the_guide_owns_it()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "Warm, plain." },
        });
        var guideId = created.GetProperty("id").GetGuid();
        var first = created.GetProperty("version").GetProperty("id").GetGuid();
        var second = await AddVersionAsync(_fixture.WorkspaceA, guideId, 2, first, "Warmer, plainer.");

        await ActivateAsync(_fixture.WorkspaceA, first, "Launch voice");

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guideId));

        // Working is the newest, approved or not; active is the approved one the workspace writes with.
        var working = body.GetProperty("workingVersion");
        Assert.Equal(second, working.GetProperty("id").GetGuid());
        Assert.Equal(2, working.GetProperty("versionNumber").GetInt32());
        Assert.Equal(1, working.GetProperty("parentVersionNumber").GetInt32());
        Assert.Equal("Revision 2", working.GetProperty("changeReason").GetString());
        Assert.Equal("Warmer, plainer.", working.GetProperty("sections")[0].GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, working.GetProperty("approval").ValueKind);
        Assert.Equal(JsonValueKind.Null, working.GetProperty("activation").ValueKind);

        var active = body.GetProperty("activeVersion");
        Assert.Equal(first, active.GetProperty("id").GetGuid());
        Assert.Equal(1, active.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Warm, plain.", active.GetProperty("sections")[0].GetProperty("body").GetString());
        Assert.Equal("Signed off", active.GetProperty("approval").GetProperty("reason").GetString());
        Assert.Equal(Moment, active.GetProperty("approval").GetProperty("approvedAt").GetDateTimeOffset());
        Assert.Equal("Launch voice", active.GetProperty("activation").GetProperty("reason").GetString());
        Assert.Equal(Moment.AddHours(1), active.GetProperty("activation").GetProperty("activatedAt").GetDateTimeOffset());

        // Authors and approvers are not part of the read.
        var raw = body.GetRawText();
        Assert.All(
            new[] { "membership", "createdBy", "approvedBy", "activatedBy", "updatedBy", "objectKey", "storageKey", "workspaceId" },
            forbidden => Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_the_working_version_is_the_active_one_both_carry_it_in_full()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "One version", questionnaire = new { voice = "Warm." } });
        var guideId = created.GetProperty("id").GetGuid();

        await ActivateAsync(_fixture.WorkspaceA, created.GetProperty("version").GetProperty("id").GetGuid(), "Only one");

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guideId));

        Assert.Equal(
            body.GetProperty("workingVersion").GetProperty("sections").GetRawText(),
            body.GetProperty("activeVersion").GetProperty("sections").GetRawText());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("workingVersion").GetProperty("approval").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("activeVersion").GetProperty("activation").ValueKind);
    }

    [Fact]
    public async Task A_default_that_belongs_to_another_guide_does_not_make_this_one_active()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guideOne = await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "One" });
        var guideTwo = await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "Two" });

        await ActivateAsync(_fixture.WorkspaceA, guideTwo.GetProperty("version").GetProperty("id").GetGuid(), "Two is the default");

        var one = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guideOne.GetProperty("id").GetGuid()));
        var two = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guideTwo.GetProperty("id").GetGuid()));

        Assert.Equal(JsonValueKind.Null, one.GetProperty("activeVersion").ValueKind);
        Assert.Equal(JsonValueKind.Object, two.GetProperty("activeVersion").ValueKind);
    }

    // ---- Empty ----

    [Fact]
    public async Task A_guide_with_only_a_name_reads_back_with_empty_content()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "Blank slate" });

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, created.GetProperty("id").GetGuid()));
        var working = body.GetProperty("workingVersion");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("purpose").ValueKind);
        Assert.Empty(working.GetProperty("sections").EnumerateArray());
        Assert.Empty(working.GetProperty("rules").EnumerateArray());
        Assert.Empty(working.GetProperty("sourceDocuments").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeVersion").ValueKind);
    }

    [Fact]
    public async Task Cited_sources_and_an_archived_guide_read_normally()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");
        var upload = await client.PostAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/brand-source-documents", form,
            Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        var documentId = (await BodyOf(upload)).GetProperty("id").GetGuid();

        var created = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Sourced",
            sourceDocuments = new[] { new { documentId, versionNumber = 1 } },
        });
        var guideId = created.GetProperty("id").GetGuid();

        await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var guide = await db.BrandStyleGuides.SingleAsync(TestContext.Current.CancellationToken);
            guide.Status = BrandStyleGuideStatus.Archived;
            guide.ArchivedAt = Moment;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, guideId));

        Assert.Equal("Archived", body.GetProperty("status").GetString());
        Assert.Equal(Moment, body.GetProperty("archivedAt").GetDateTimeOffset());

        var cited = Assert.Single(body.GetProperty("workingVersion").GetProperty("sourceDocuments").EnumerateArray());
        Assert.Equal(documentId, cited.GetProperty("documentId").GetGuid());
        Assert.Equal(1, cited.GetProperty("versionNumber").GetInt32());
    }

    // ---- Isolation and roles ----

    [Fact]
    public async Task Another_workspaces_guide_is_indistinguishable_from_one_that_does_not_exist()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, new { displayName = "House voice", questionnaire = new { voice = "A's secret voice" } });
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, new { displayName = "House voice", questionnaire = new { voice = "B's secret voice" } });
        var idA = inA.GetProperty("id").GetGuid();
        var idB = inB.GetProperty("id").GetGuid();

        // B's guide through A's route, a guide nobody has, and a malformed id.
        var foreign = await GetAsync(ownerA, _fixture.WorkspaceA, idB);
        var unknown = await GetAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid());
        var malformed = await ownerA.GetAsync($"{GuidesIn(_fixture.WorkspaceA)}/not-a-guid", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal("brand.guide.not_found", (await BodyOf(foreign)).GetProperty("code").GetString());
        Assert.Equal("brand.guide.not_found", (await BodyOf(unknown)).GetProperty("code").GetString());
        Assert.DoesNotContain("B's secret voice", await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A caller who is not a member of A, and a workspace that does not exist, look the same too.
        var notMember = await GetAsync(ownerB, _fixture.WorkspaceA, idA);
        var noWorkspace = await ownerB.GetAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-style-guides/{idA}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noWorkspace.StatusCode);

        // Each reads only its own, even with the same name.
        var readA = await BodyOf(await GetAsync(ownerA, _fixture.WorkspaceA, idA));
        var readB = await BodyOf(await GetAsync(ownerB, _fixture.WorkspaceB, idB));
        Assert.Equal("A's secret voice", readA.GetProperty("workingVersion").GetProperty("sections")[0].GetProperty("body").GetString());
        Assert.Equal("B's secret voice", readB.GetProperty("workingVersion").GetProperty("sections")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_default_in_another_workspace_never_makes_a_guide_here_active()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, new { displayName = "A guide" });
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, new { displayName = "B guide" });

        await ActivateAsync(_fixture.WorkspaceB, inB.GetProperty("version").GetProperty("id").GetGuid(), "B's default");

        var readA = await BodyOf(await GetAsync(ownerA, _fixture.WorkspaceA, inA.GetProperty("id").GetGuid()));
        var readB = await BodyOf(await GetAsync(ownerB, _fixture.WorkspaceB, inB.GetProperty("id").GetGuid()));

        Assert.Equal(JsonValueKind.Null, readA.GetProperty("activeVersion").ValueKind);
        Assert.Equal("B's default", readB.GetProperty("activeVersion").GetProperty("activation").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_viewer_can_read_a_guide()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var created = await CreateAsync(owner, _fixture.WorkspaceA, new { displayName = "Readable" });

        using var viewer = await _fixture.SignInAsync(ViewerEmail, Password, TestContext.Current.CancellationToken);
        var response = await GetAsync(viewer, _fixture.WorkspaceA, created.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
