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
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET .../brand-style-guides/{guideId}/versions/compare</c> through the real Gateway: that the comparer's
/// vocabulary survives the seam, that a version number names its own parameter when it is wrong, and that
/// nothing one workspace holds is reachable from the other.
/// </summary>
/// <remarks>
/// The algebra itself belongs to <see cref="BrandStyleGuideComparerTests"/>, which can state the whole truth
/// table without a database. These tests are about the route. Later versions and approvals are seeded
/// directly, as in <see cref="BrandStyleGuideVersionListEndpointTests"/>: nothing in the API writes them yet
/// (11A.15 edits, 11A.16c activates).
/// </remarks>
public sealed class BrandStyleGuideCompareEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(new InMemoryPrivateObjectStore());
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- The comparison ----

    [Fact]
    public async Task One_request_reports_every_state_and_names_both_sides()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new
            {
                voice = "Warm.",
                alwaysDo = new[] { "Say you", "Lead with the dish" },
                neverDo = new[] { "Shout" },
            },
            sections = new object[] { new { sectionKey = "Tone", body = "Dry." } },
        });

        // Version 2: Voice untouched, Tone reworded, Audience new, a do reordered, a don't reworded.
        var second = await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId, version =>
        {
            Add(version, BrandStyleGuideSectionKey.Voice, "Warm.");
            Add(version, BrandStyleGuideSectionKey.Tone, "Warmer.");
            Add(version, BrandStyleGuideSectionKey.Audience, "Home cooks.");
            Add(version, BrandStyleGuideRuleKind.Do, "Lead with the dish", 0);
            Add(version, BrandStyleGuideRuleKind.Do, "Say you", 1);
            Add(version, BrandStyleGuideRuleKind.Dont, "Yell", 2);
        });

        await ApproveAsync(_fixture.WorkspaceA, guide.VersionId);

        var response = await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 1, 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);

        // Both sides describe themselves, and the approval seeded on version 1 shows as its status.
        Assert.Equal(guide.VersionId, body.GetProperty("from").GetProperty("versionId").GetGuid());
        Assert.Equal(1, body.GetProperty("from").GetProperty("versionNumber").GetInt32());
        Assert.Equal("Approved", body.GetProperty("from").GetProperty("status").GetString());
        Assert.Equal(second, body.GetProperty("to").GetProperty("versionId").GetGuid());
        Assert.Equal("Draft", body.GetProperty("to").GetProperty("status").GetString());

        var comparison = body.GetProperty("comparison");
        Assert.True(comparison.GetProperty("hasChanges").GetBoolean());

        var sections = comparison.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal("Unchanged", SectionState(sections, "Voice"));
        Assert.Equal("Changed", SectionState(sections, "Tone"));
        Assert.Equal("Added", SectionState(sections, "Audience"));

        var rules = comparison.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal("Moved", RuleState(rules, "Lead with the dish"));
        Assert.Equal("Moved", RuleState(rules, "Say you"));

        // A reworded rule is a removal and an addition, never a change.
        Assert.Equal("Removed", RuleState(rules, "Shout"));
        Assert.Equal("Added", RuleState(rules, "Yell"));
        Assert.DoesNotContain("Changed", rules.Select(rule => rule.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task A_re_pinned_citation_is_one_change_and_the_sides_can_be_read_either_way_round()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var document = await UploadAsync(client, _fixture.WorkspaceA);

        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Sourced",
            sourceDocuments = new[] { new { documentId = document, versionNumber = 1 } },
        });

        var secondVersionId = await SecondVersionOf(document);

        // Version 2 cites version 2 of the same document, as fixing a stale citation would.
        await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId, version =>
            version.SourceLinks.Add(new BrandStyleGuideSourceLink
            {
                BrandSourceDocumentVersionId = secondVersionId,
            }));

        var forward = (await BodyOf(await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 1, 2)))
            .GetProperty("comparison").GetProperty("sources");
        var backward = (await BodyOf(await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 2, 1)))
            .GetProperty("comparison").GetProperty("sources");

        var one = Assert.Single(forward.EnumerateArray());
        Assert.Equal(document, one.GetProperty("documentId").GetGuid());
        Assert.Equal("Changed", one.GetProperty("state").GetString());
        Assert.Equal(1, one.GetProperty("fromVersionNumber").GetInt32());
        Assert.Equal(2, one.GetProperty("toVersionNumber").GetInt32());

        // Reversing the request reverses the reading rather than refusing it.
        var other = Assert.Single(backward.EnumerateArray());
        Assert.Equal(2, other.GetProperty("fromVersionNumber").GetInt32());
        Assert.Equal(1, other.GetProperty("toVersionNumber").GetInt32());
    }

    [Fact]
    public async Task A_version_compared_with_itself_answers_no_changes()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "One version",
            questionnaire = new { voice = "Warm.", alwaysDo = new[] { "Say you" } },
        });

        var body = await BodyOf(await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 1, 1));
        var comparison = body.GetProperty("comparison");

        Assert.False(comparison.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(
            body.GetProperty("from").GetProperty("versionId").GetGuid(),
            body.GetProperty("to").GetProperty("versionId").GetGuid());
        Assert.Equal("Unchanged", Assert.Single(comparison.GetProperty("sections").EnumerateArray()).GetProperty("state").GetString());
        Assert.Equal("Unchanged", Assert.Single(comparison.GetProperty("rules").EnumerateArray()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Comparing_writes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Untouched" });
        await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId, _ => { });

        var before = await CountsAsync(_fixture.WorkspaceA);
        Assert.Equal(HttpStatusCode.OK, (await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 1, 2)).StatusCode);
        var after = await CountsAsync(_fixture.WorkspaceA);

        // No version, no approval and no default appears from having compared. Nor does one vanish.
        Assert.Equal(before, after);

        var read = await BodyOf(await client.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{guide.Id}", TestContext.Current.CancellationToken));
        Assert.Equal("Active", read.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("activeVersion").ValueKind);
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("?to=2", "from")]
    [InlineData("?from=1", "to")]
    [InlineData("?from=0&to=2", "from")]
    [InlineData("?from=1&to=-3", "to")]
    public async Task A_missing_or_impossible_version_number_is_refused_at_the_edge(string query, string field)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Edge" });

        var response = await client.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{guide.Id}/versions/compare{query}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.GuideInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), query);
    }

    [Fact]
    public async Task A_version_number_this_guide_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "One version only" });

        var one = await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 1, 9);
        var both = await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 8, 9);

        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, both.StatusCode);

        var oneBody = await BodyOf(one);
        Assert.Equal(BrandErrorCodes.GuideVersionNotFound, oneBody.GetProperty("code").GetString());
        Assert.False(oneBody.GetProperty("errors").TryGetProperty("from", out _));
        Assert.True(oneBody.GetProperty("errors").TryGetProperty("to", out _));

        // Both wrong names both, so a creator who mistyped twice is not sent round the loop twice.
        var bothErrors = (await BodyOf(both)).GetProperty("errors");
        Assert.True(bothErrors.TryGetProperty("from", out _));
        Assert.True(bothErrors.TryGetProperty("to", out _));
    }

    [Fact]
    public async Task A_number_the_guide_lacks_on_both_sides_is_reported_once()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "One version only" });

        var errors = (await BodyOf(await CompareAsync(client, _fixture.WorkspaceA, guide.Id, 7, 7)))
            .GetProperty("errors");

        // Equal numbers are one question, so the same version is not reported missing twice.
        Assert.True(errors.TryGetProperty("from", out _));
        Assert.False(errors.TryGetProperty("to", out _));
    }

    // ---- Isolation ----

    [Fact]
    public async Task An_unknown_guide_and_another_workspaces_are_the_same_answer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var inB = await CreateGuideAsync(ownerB, _fixture.WorkspaceB, new { displayName = "B's guide" });

        var foreign = await CompareAsync(ownerA, _fixture.WorkspaceA, inB.Id, 1, 1);
        var unknown = await CompareAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid(), 1, 1);
        var malformed = await ownerA.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/not-a-guid/versions/compare?from=1&to=1", cancellation);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        var foreignBody = await BodyOf(foreign);
        Assert.Equal(BrandErrorCodes.GuideNotFound, foreignBody.GetProperty("code").GetString());
        Assert.Equal(BrandErrorCodes.GuideNotFound, (await BodyOf(unknown)).GetProperty("code").GetString());

        // The guide 404 comes before any version lookup, so it names no parameter and says nothing about
        // which versions B's guide has. `errors` is on every refusal this API makes; this one's is empty.
        Assert.Empty(foreignBody.GetProperty("errors").EnumerateObject());

        var intoNowhere = await ownerB.GetAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-style-guides/{inB.Id}/versions/compare?from=1&to=1", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
    }

    /// <summary>
    /// That a version number resolves only inside the route's guide.
    /// </summary>
    /// <remarks>
    /// Named for what it proves. The exclusion here is the <c>BrandStyleGuideId == guideId</c> predicate in
    /// <c>FindVersionsByNumberAsync</c>, not the workspace query filter: B's version 2 carries B's guide id,
    /// so it is out of this query's reach whether or not the filter exists. The filter's own
    /// load-bearing test is <see cref="An_unknown_guide_and_another_workspaces_are_the_same_answer"/>, where
    /// removing it would turn a 404 into a 200 — and the only door to a version is its guide, which is why
    /// that is the test that matters. See
    /// <see cref="A_version_cannot_be_stored_under_one_workspace_pointing_at_anothers_guide"/> for why a
    /// version can never disagree with its guide about which workspace it is in.
    /// </remarks>
    [Fact]
    public async Task A_version_number_resolves_only_inside_the_guide_the_route_names()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        // The same guide name on both sides. B has two versions and secret text; A has one.
        var inA = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "A's voice" },
        });
        var inB = await CreateGuideAsync(ownerB, _fixture.WorkspaceB, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "B's secret voice" },
        });
        await AddVersionAsync(_fixture.WorkspaceB, inB.Id, 2, inB.VersionId, version =>
            Add(version, BrandStyleGuideSectionKey.Voice, "B's second secret"));

        // Version 2 exists in B and not in A. Asking A for it is a missing version, not B's content.
        var reachedForB = await CompareAsync(ownerA, _fixture.WorkspaceA, inA.Id, 1, 2);
        Assert.Equal(HttpStatusCode.NotFound, reachedForB.StatusCode);
        Assert.Equal(
            BrandErrorCodes.GuideVersionNotFound,
            (await BodyOf(reachedForB)).GetProperty("code").GetString());
        Assert.DoesNotContain(
            "secret",
            await reachedForB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.OrdinalIgnoreCase);

        // Each side compares only its own, even under the same name.
        var fromA = await BodyOf(await CompareAsync(ownerA, _fixture.WorkspaceA, inA.Id, 1, 1));
        var fromB = await BodyOf(await CompareAsync(ownerB, _fixture.WorkspaceB, inB.Id, 1, 2));

        Assert.Contains("A's voice", fromA.GetRawText());
        Assert.DoesNotContain("secret", fromA.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(fromB.GetProperty("comparison").GetProperty("hasChanges").GetBoolean());
    }

    /// <summary>
    /// That a version cannot disagree with its guide about which workspace it is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap this closes: every other isolation test here excludes another workspace's rows through the
    /// guide — the query filter on <c>BrandStyleGuides</c>, or the guide-id predicate on the version query.
    /// Neither says anything about a version row that claims one workspace while naming the other's guide,
    /// which is the row that would make reading versions by guide id unsafe.
    /// </para>
    /// <para>
    /// It cannot exist, and not because of a filter: the version's foreign key to its guide is the composite
    /// <c>(WorkspaceId, BrandStyleGuideId)</c> against the guide's <c>(WorkspaceId, Id)</c>, so such a row has
    /// no principal to reference and the store refuses it. That is why resolving a version by its guide needs
    /// no second workspace check of its own — and this test is what stops a future migration from quietly
    /// dropping the composite key down to <c>BrandStyleGuideId</c> alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_version_cannot_be_stored_under_one_workspace_pointing_at_anothers_guide()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var inA = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, new { displayName = "A's guide" });

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => InScopeAsync(_fixture.WorkspaceB, async db =>
        {
            db.BrandStyleGuideVersions.Add(new BrandStyleGuideVersion
            {
                Id = Guid.NewGuid(),

                // B's workspace, A's guide. There is no BrandStyleGuide row with this pair.
                WorkspaceId = _fixture.WorkspaceB.Id,
                BrandStyleGuideId = inA.Id,
                VersionNumber = 1,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Moment,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        }));

        Assert.Contains("FOREIGN KEY", refused.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_viewer_can_compare_versions()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceB, new { displayName = "Readable" });

        using var viewer = await _fixture.SignInAsync(
            _fixture.WorkspaceB.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HttpStatusCode.OK,
            (await CompareAsync(viewer, _fixture.WorkspaceB, guide.Id, 1, 1)).StatusCode);
    }

    // ---- Harness ----

    private sealed record SeededGuide(Guid Id, Guid VersionId);

    private static string GuidesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> CompareAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int from, int to) =>
        client.GetAsync(
            $"{GuidesIn(workspace)}/{guideId}/versions/compare?from={from}&to={to}",
            TestContext.Current.CancellationToken);

    private static string? SectionState(IReadOnlyList<JsonElement> sections, string sectionKey) =>
        sections.Single(section => section.GetProperty("sectionKey").GetString() == sectionKey)
            .GetProperty("state").GetString();

    private static string? RuleState(IReadOnlyList<JsonElement> rules, string text) =>
        rules.Single(rule => rule.GetProperty("text").GetString() == text).GetProperty("state").GetString();

    private async Task<SeededGuide> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededGuide(
            created.GetProperty("id").GetGuid(), created.GetProperty("version").GetProperty("id").GetGuid());
    }

    /// <summary>Uploads a document and replaces it, so it has versions 1 and 2 to cite.</summary>
    private async Task<Guid> UploadAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var cancellation = TestContext.Current.CancellationToken;

        var upload = await client.PostAsync(
            $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents",
            FileForm("house-style.pdf", "house style", metadata: true),
            Guid.NewGuid().ToString("N"),
            cancellation);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var created = await BodyOf(upload);
        var documentId = created.GetProperty("id").GetGuid();

        var replacement = FileForm("house-style-v2.pdf", "the second file", metadata: false);
        replacement.Add(new StringContent(created.GetProperty("concurrencyToken").GetString()!), "expectedConcurrencyToken");

        var replaced = await client.PostAsync(
            $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents/{documentId}/versions",
            replacement,
            Guid.NewGuid().ToString("N"),
            cancellation);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);

        return documentId;
    }

    private static MultipartFormDataContent FileForm(string fileName, string contents, bool metadata)
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

    private Task<Guid> SecondVersionOf(Guid documentId) =>
        InScopeAsync(_fixture.WorkspaceA, db => db.BrandSourceDocumentVersions
            .Where(version => version.BrandSourceDocumentId == documentId && version.VersionNumber == 2)
            .Select(version => version.Id)
            .SingleAsync(TestContext.Current.CancellationToken));

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>Adds a later version with whatever content the caller writes into it, as an edit will.</summary>
    private Task<Guid> AddVersionAsync(
        SeededWorkspace workspace,
        Guid guideId,
        int number,
        Guid parentId,
        Action<BrandStyleGuideVersion> content) =>
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

            content(version);

            foreach (var section in version.Sections) { section.WorkspaceId = workspace.Id; section.BrandStyleGuideVersionId = version.Id; }
            foreach (var rule in version.Rules) { rule.WorkspaceId = workspace.Id; rule.BrandStyleGuideVersionId = version.Id; }
            foreach (var link in version.SourceLinks) { link.WorkspaceId = workspace.Id; link.BrandStyleGuideVersionId = version.Id; }

            db.BrandStyleGuideVersions.Add(version);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return version.Id;
        });

    private static void Add(BrandStyleGuideVersion version, BrandStyleGuideSectionKey key, string body) =>
        version.Sections.Add(new BrandStyleGuideSection
        {
            Id = Guid.NewGuid(),
            SectionKey = key,
            ChannelKey = string.Empty,
            Body = body,
        });

    private static void Add(BrandStyleGuideVersion version, BrandStyleGuideRuleKind kind, string text, int sortOrder) =>
        version.Rules.Add(new BrandStyleGuideRule
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Text = text,
            SortOrder = sortOrder,
        });

    private Task ApproveAsync(SeededWorkspace workspace, Guid versionId) =>
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

    /// <summary>Row counts a comparison must not move.</summary>
    private Task<(int Versions, int Sections, int Rules, int Links, int Approvals, int Defaults)> CountsAsync(
        SeededWorkspace workspace) =>
        InScopeAsync(workspace, async db =>
        {
            var cancellation = TestContext.Current.CancellationToken;

            return (
                await db.BrandStyleGuideVersions.CountAsync(cancellation),
                await db.BrandStyleGuideSections.CountAsync(cancellation),
                await db.BrandStyleGuideRules.CountAsync(cancellation),
                await db.BrandStyleGuideSourceLinks.CountAsync(cancellation),
                await db.BrandStyleGuideApprovals.CountAsync(cancellation),
                await db.BrandStyleGuideDefaults.CountAsync(cancellation));
        });
}
