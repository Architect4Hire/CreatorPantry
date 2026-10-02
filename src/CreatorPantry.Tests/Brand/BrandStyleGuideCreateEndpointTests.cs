using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
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
/// <c>POST .../brand-style-guides</c> through the real Gateway: what a questionnaire, structured sections and
/// cited sources become, what blank answers do not create, how a pointer that does not resolve is refused,
/// replay, the Editor policy and workspace isolation.
/// </summary>
public sealed class BrandStyleGuideCreateEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "guide-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private readonly InMemoryPrivateObjectStore _store = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
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

    private static string GuidesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Key() => Guid.NewGuid().ToString("N");

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? key = null) =>
        client.PostAsJsonAsync(GuidesIn(workspace), body, key ?? Key(), TestContext.Current.CancellationToken);

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private Task<int> GuideCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuides.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Uploads a document and returns its id and the concurrency token its lifecycle commands need.</summary>
    private async Task<(Guid Id, string Token)> UploadDocumentAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        var response = await client.PostAsync(SourcesIn(workspace), form, Key(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("id").GetGuid(), body.GetProperty("concurrencyToken").GetString()!);
    }

    private async Task<string> CommandAsync(
        GatewayClient client, SeededWorkspace workspace, Guid documentId, string command, string token)
    {
        var response = await client.PostAsJsonAsync(
            $"{SourcesIn(workspace)}/{documentId}/{command}",
            new { expectedConcurrencyToken = token },
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"{command} answered {response.StatusCode}");

        return response.StatusCode == HttpStatusCode.NoContent
            ? token
            : (await BodyOf(response)).GetProperty("concurrencyToken").GetString()!;
    }

    // ---- Success ----

    [Fact]
    public async Task A_name_alone_creates_a_guide_and_an_empty_version_one()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "  Sam's voice  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);

        var body = await BodyOf(response);
        Assert.Equal("Sam's voice", body.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("purpose").ValueKind);
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));

        var version = body.GetProperty("version");
        Assert.Equal(1, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, version.GetProperty("changeReason").ValueKind);
        Assert.Empty(version.GetProperty("sections").EnumerateArray());
        Assert.Empty(version.GetProperty("rules").EnumerateArray());
        Assert.Empty(version.GetProperty("sourceDocuments").EnumerateArray());

        await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var guide = await db.BrandStyleGuides.SingleAsync(cancellation);
            var row = await db.BrandStyleGuideVersions.SingleAsync(cancellation);

            Assert.Equal(_fixture.WorkspaceA.Id, guide.WorkspaceId);
            Assert.Equal(guide.Id, row.BrandStyleGuideId);
            Assert.Equal(1, row.VersionNumber);
            Assert.Null(row.ParentVersionId);
            Assert.Empty(await db.BrandStyleGuideSections.ToListAsync(cancellation));
            Assert.Empty(await db.BrandStyleGuideRules.ToListAsync(cancellation));

            // Not approved and not the workspace default: both are separate, explicit acts.
            Assert.Empty(await db.BrandStyleGuideApprovals.ToListAsync(cancellation));
            Assert.Empty(await db.BrandStyleGuideDefaults.ToListAsync(cancellation));

            var audit = Assert.Single(await db.AuditLogs.ToListAsync(cancellation), log => log.Action == BrandAuditActions.StyleGuideCreated);
            Assert.Equal(guide.Id.ToString("D"), audit.ResourceId);

            return 0;
        });
    }

    [Fact]
    public async Task The_questionnaire_becomes_sections_and_rules_exactly_as_typed_and_blanks_become_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            purpose = "Everything we publish",
            questionnaire = new
            {
                voice = "  Warm, plain.\nNever breathless.  ",
                tone = "   ",
                audience = "Busy home cooks",
                vocabulary = (string?)null,
                notes = "Say 'supper', not 'dinner'.",
                alwaysDo = new[] { "Lead with the dish", "", "  ", "Name the pan size" },
                neverDo = new[] { "Use exclamation marks" },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var version = (await BodyOf(response)).GetProperty("version");

        // Only the answered questions have a row, in a stable order, with the text untouched — surrounding
        // spaces and the line break included.
        var sections = version.GetProperty("sections").EnumerateArray()
            .Select(section => (section.GetProperty("sectionKey").GetString(), section.GetProperty("body").GetString()))
            .ToList();

        Assert.Equal(
            [
                ("Voice", "  Warm, plain.\nNever breathless.  "),
                ("Audience", "Busy home cooks"),
                ("UserNotes", "Say 'supper', not 'dinner'."),
            ],
            sections);

        Assert.Equal(
            [("Do", "Lead with the dish"), ("Do", "Name the pan size"), ("Dont", "Use exclamation marks")],
            version.GetProperty("rules").EnumerateArray()
                .Select(rule => (rule.GetProperty("kind").GetString(), rule.GetProperty("text").GetString())));

        await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var stored = await db.BrandStyleGuideSections.ToListAsync(cancellation);
            Assert.Equal(3, stored.Count);
            Assert.DoesNotContain(stored, section => section.SectionKey is BrandStyleGuideSectionKey.Tone or BrandStyleGuideSectionKey.Vocabulary);
            Assert.All(stored, section => Assert.Equal(string.Empty, section.ChannelKey));

            var rules = await db.BrandStyleGuideRules.OrderBy(rule => rule.SortOrder).ToListAsync(cancellation);
            Assert.Equal([0, 1, 2], rules.Select(rule => rule.SortOrder));

            return 0;
        });
    }

    [Fact]
    public async Task Structured_sections_and_channel_variants_are_stored_beside_the_questionnaire()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Channels",
            questionnaire = new { voice = "Warm." },
            sections = new object[]
            {
                new { sectionKey = "BlogGuidance", body = "Long, with a story." },
                new { sectionKey = "ChannelVariant", channelKey = "instagram", body = "Short and bright." },
                new { sectionKey = "ChannelVariant", channelKey = "newsletter", body = "Chatty." },
                new { sectionKey = "SentenceRhythm", body = "   " },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var stored = await db.BrandStyleGuideSections.ToListAsync(cancellation);

            Assert.Equal(4, stored.Count);
            Assert.DoesNotContain(stored, section => section.SectionKey is BrandStyleGuideSectionKey.SentenceRhythm);
            Assert.Equal(
                ["instagram", "newsletter"],
                stored.Where(section => section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant)
                    .Select(section => section.ChannelKey).Order());

            return 0;
        });
    }

    [Fact]
    public async Task A_cited_source_is_stored_as_the_exact_version_and_an_archived_document_can_be_cited()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (live, _) = await UploadDocumentAsync(client, _fixture.WorkspaceA);
        var (shelved, token) = await UploadDocumentAsync(client, _fixture.WorkspaceA);
        await CommandAsync(client, _fixture.WorkspaceA, shelved, "archive", token);

        var response = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Sourced",
            sourceDocuments = new object[]
            {
                new { documentId = live, versionNumber = 1 },
                new { documentId = shelved, versionNumber = 1 },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var cited = (await BodyOf(response)).GetProperty("version").GetProperty("sourceDocuments").EnumerateArray()
            .Select(source => (source.GetProperty("documentId").GetGuid(), source.GetProperty("versionNumber").GetInt32()));
        Assert.Equal([(live, 1), (shelved, 1)], cited);

        await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var versions = await db.BrandSourceDocumentVersions
                .Where(version => version.BrandSourceDocumentId == live || version.BrandSourceDocumentId == shelved)
                .Select(version => version.Id)
                .ToListAsync(cancellation);
            var links = await db.BrandStyleGuideSourceLinks.ToListAsync(cancellation);

            Assert.Equal(versions.Order(), links.Select(link => link.BrandSourceDocumentVersionId).Order());
            Assert.All(links, link => Assert.Equal(_fixture.WorkspaceA.Id, link.WorkspaceId));

            return 0;
        });
    }

    // ---- Source pointers that do not resolve ----

    [Fact]
    public async Task Every_way_a_pointer_can_fail_to_resolve_is_refused_identically_and_creates_nothing()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var (usable, _) = await UploadDocumentAsync(ownerA, _fixture.WorkspaceA);
        var (removed, removeToken) = await UploadDocumentAsync(ownerA, _fixture.WorkspaceA);
        await CommandAsync(ownerA, _fixture.WorkspaceA, removed, "remove", removeToken);
        var (othersDocument, _) = await UploadDocumentAsync(ownerB, _fixture.WorkspaceB);

        var pointers = new (string Why, Guid Document, int Version)[]
        {
            ("never issued", Guid.NewGuid(), 1),
            ("another workspace's", othersDocument, 1),
            ("removed", removed, 1),
            ("a version it does not have", usable, 2),
        };

        var answers = new List<(HttpStatusCode, string, string)>();

        foreach (var (why, document, version) in pointers)
        {
            // Mixed with a pointer that is fine: the whole request fails, and nothing is half-created.
            var response = await CreateAsync(ownerA, _fixture.WorkspaceA, new
            {
                displayName = $"Refused: {why}",
                questionnaire = new { voice = "Warm." },
                sourceDocuments = new object[]
                {
                    new { documentId = usable, versionNumber = 1 },
                    new { documentId = document, versionNumber = version },
                },
            });

            var body = await BodyOf(response);
            answers.Add((response.StatusCode, Code(body), body.GetProperty("errors").GetRawText()));
        }

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.Item1));
        Assert.All(answers, answer => Assert.Equal(BrandErrorCodes.GuideSourceUnprocessable, answer.Item2));

        // The same answer for every cause, down to the field it names.
        Assert.Single(answers.Select(answer => answer.Item3).Distinct());

        Assert.Equal(0, await GuideCountAsync(_fixture.WorkspaceA));
        Assert.Equal(0, await InScopeAsync(_fixture.WorkspaceA, db => db.BrandStyleGuideSourceLinks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InScopeAsync(_fixture.WorkspaceA, db => db.AuditLogs.CountAsync(
            log => log.Action == BrandAuditActions.StyleGuideCreated, TestContext.Current.CancellationToken)));
    }

    // ---- Validation ----

    [Fact]
    public async Task Shape_failures_answer_400_with_the_field_and_create_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var document = Guid.NewGuid();
        var tooLong = new string('x', BrandPolicy.StyleGuideSectionBodyMaxLength + 1);

        var cases = new (string Field, object Body)[]
        {
            ("displayName", new { displayName = "   " }),
            ("displayName", new { displayName = new string('n', BrandPolicy.StyleGuideDisplayNameMaxLength + 1) }),
            ("purpose", new { displayName = "G", purpose = new string('p', BrandPolicy.StyleGuidePurposeMaxLength + 1) }),
            ("questionnaire.voice", new { displayName = "G", questionnaire = new { voice = tooLong } }),
            ("sections[0].body", new { displayName = "G", sections = new[] { new { sectionKey = "Tone", body = tooLong } } }),
            ("sections[0].sectionKey", new { displayName = "G", sections = new[] { new { sectionKey = (string?)null, body = "x" } } }),
            ("sections[0].channelKey", new { displayName = "G", sections = new[] { new { sectionKey = "ChannelVariant", body = "x" } } }),
            ("sections[0].channelKey", new { displayName = "G", sections = new[] { new { sectionKey = "ChannelVariant", channelKey = "My Space!", body = "x" } } }),
            ("sections[0].channelKey", new { displayName = "G", sections = new[] { new { sectionKey = "Tone", channelKey = "instagram", body = "x" } } }),
            ("sections[1].sectionKey", new { displayName = "G", sections = new[] { new { sectionKey = "Tone", body = "a" }, new { sectionKey = "Tone", body = "b" } } }),
            ("sections[0].sectionKey", new { displayName = "G", questionnaire = new { tone = "a" }, sections = new[] { new { sectionKey = "Tone", body = "b" } } }),
            ("questionnaire", new { displayName = "G", questionnaire = new { alwaysDo = new[] { "Same", "same " } } }),
            ("questionnaire.alwaysDo[0]", new { displayName = "G", questionnaire = new { alwaysDo = new[] { new string('r', BrandPolicy.StyleGuideRuleTextMaxLength + 1) } } }),
            ("questionnaire", new { displayName = "G", questionnaire = new { alwaysDo = Enumerable.Range(0, BrandPolicy.MaxStyleGuideRules + 1).Select(index => $"Rule {index}").ToArray() } }),
            ("sourceDocuments[0]", new { displayName = "G", sourceDocuments = new[] { new { documentId = document, versionNumber = 0 } } }),
            ("sourceDocuments[1]", new { displayName = "G", sourceDocuments = new[] { new { documentId = document, versionNumber = 1 }, new { documentId = document, versionNumber = 1 } } }),
        };

        foreach (var (field, body) in cases)
        {
            var response = await CreateAsync(client, _fixture.WorkspaceA, body);
            var json = await BodyOf(response);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(BrandErrorCodes.GuideInvalidRequest, Code(json));
            Assert.True(
                json.GetProperty("errors").EnumerateObject().Any(error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase)),
                $"no error for {field}: {json.GetProperty("errors").GetRawText()}");
        }

        Assert.Equal(0, await GuideCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Too_many_channel_variants_and_sources_are_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var variants = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "G",
            sections = Enumerable.Range(0, BrandPolicy.MaxStyleGuideChannelVariants + 1)
                .Select(index => new { sectionKey = "ChannelVariant", channelKey = $"channel-{index}", body = "x" }).ToArray(),
        });
        var sources = await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "G",
            sourceDocuments = Enumerable.Range(1, BrandPolicy.MaxStyleGuideSourceLinks + 1)
                .Select(index => new { documentId = Guid.NewGuid(), versionNumber = index }).ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, variants.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sources.StatusCode);
        Assert.Equal(0, await GuideCountAsync(_fixture.WorkspaceA));
    }

    // ---- Idempotency ----

    [Fact]
    public async Task Replaying_a_create_returns_the_original_and_creates_nothing_more()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var body = new { displayName = "Once", questionnaire = new { voice = "Warm." } };

        var first = await CreateAsync(client, _fixture.WorkspaceA, body, "guide-create-1");
        var replay = await CreateAsync(client, _fixture.WorkspaceA, body, "guide-create-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal((await BodyOf(first)).GetProperty("id").GetGuid(), (await BodyOf(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, await GuideCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_request_that_differs_only_in_blanks_is_the_same_request()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "Same" }, "guide-create-blank");
        var replay = await CreateAsync(
            client,
            _fixture.WorkspaceA,
            new { displayName = " Same ", purpose = " ", questionnaire = new { tone = "" } },
            "guide-create-blank");

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(1, await GuideCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_guide_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "One" }, "guide-create-2");
        var reused = await CreateAsync(client, _fixture.WorkspaceA, new { displayName = "Two" }, "guide-create-2");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(1, await GuideCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_create_without_a_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            GuidesIn(_fixture.WorkspaceA), new { displayName = "Keyless" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await GuideCountAsync(_fixture.WorkspaceA));
    }

    // ---- Roles ----

    [Fact]
    public async Task A_viewer_cannot_create_a_guide()
    {
        var email = "guide-viewer-a@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, Password);

        await using (var scope = _fixture.Api.Factory.Services.CreateAsyncScope())
        {
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var viewer = await _fixture.SignInAsync(email, Password, TestContext.Current.CancellationToken);

        var response = await CreateAsync(viewer, _fixture.WorkspaceA, new { displayName = "Nope" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await GuideCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_contributor_cannot_create_a_guide()
    {
        using var contributor = await _fixture.SignInAsync(ContributorEmail, Password, TestContext.Current.CancellationToken);

        var response = await CreateAsync(contributor, _fixture.WorkspaceA, new { displayName = "Nope" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await GuideCountAsync(_fixture.WorkspaceA));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Two_workspaces_keep_their_guides_apart_even_with_the_same_name_and_key()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var (documentA, _) = await UploadDocumentAsync(ownerA, _fixture.WorkspaceA);
        var (documentB, _) = await UploadDocumentAsync(ownerB, _fixture.WorkspaceB);
        const string key = "shared-guide-key";

        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "A's secret voice" },
            sourceDocuments = new[] { new { documentId = documentA, versionNumber = 1 } },
        }, key);
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "B's secret voice" },
            sourceDocuments = new[] { new { documentId = documentB, versionNumber = 1 } },
        }, key);

        Assert.Equal(HttpStatusCode.Created, inA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);

        // The key is scoped to the workspace: B's was not a replay of A's.
        Assert.False(inB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.NotEqual((await BodyOf(inA)).GetProperty("id").GetGuid(), (await BodyOf(inB)).GetProperty("id").GetGuid());

        foreach (var (workspace, own, foreign, document) in new[]
        {
            (_fixture.WorkspaceA, "A's secret voice", "B's secret voice", documentA),
            (_fixture.WorkspaceB, "B's secret voice", "A's secret voice", documentB),
        })
        {
            await InScopeAsync(workspace, async db =>
            {
                var guide = await db.BrandStyleGuides.SingleAsync(cancellation);
                var sections = await db.BrandStyleGuideSections.ToListAsync(cancellation);
                var links = await db.BrandStyleGuideSourceLinks.ToListAsync(cancellation);
                var cited = await db.BrandSourceDocumentVersions
                    .Where(version => version.BrandSourceDocumentId == document).Select(version => version.Id).SingleAsync(cancellation);

                Assert.Equal(workspace.Id, guide.WorkspaceId);
                Assert.Equal(own, Assert.Single(sections).Body);
                Assert.DoesNotContain(sections, section => section.Body == foreign);
                Assert.Equal(cited, Assert.Single(links).BrandSourceDocumentVersionId);

                return 0;
            });
        }

        // B cannot cite A's document, and is told nothing about whether it exists.
        var crossCite = await CreateAsync(ownerB, _fixture.WorkspaceB, new
        {
            displayName = "Borrowed",
            sourceDocuments = new[] { new { documentId = documentA, versionNumber = 1 } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossCite.StatusCode);
        Assert.Equal(1, await GuideCountAsync(_fixture.WorkspaceB));

        // And the other way round: the refusal is symmetric.
        var crossCiteBack = await CreateAsync(ownerA, _fixture.WorkspaceA, new
        {
            displayName = "Borrowed back",
            sourceDocuments = new[] { new { documentId = documentB, versionNumber = 1 } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossCiteBack.StatusCode);
        Assert.Equal(1, await GuideCountAsync(_fixture.WorkspaceA));

        // The database holds the line too, should the code above ever be bypassed: a link whose guide version
        // is A's and whose source version is B's has no composite key to resolve against.
        var foreignVersion = await InScopeAsync(_fixture.WorkspaceB, db => db.BrandSourceDocumentVersions
            .Where(version => version.BrandSourceDocumentId == documentB).Select(version => version.Id).SingleAsync(cancellation));

        await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            db.BrandStyleGuideSourceLinks.Add(new Domain.Modules.Brand.Data.Entities.BrandStyleGuideSourceLink
            {
                WorkspaceId = _fixture.WorkspaceA.Id,
                BrandStyleGuideVersionId = (await db.BrandStyleGuideVersions.SingleAsync(cancellation)).Id,
                BrandSourceDocumentVersionId = foreignVersion,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));

            return 0;
        });

        // A guide cannot be created in a workspace the caller is not in, and that looks like no workspace.
        var intoA = await CreateAsync(ownerB, _fixture.WorkspaceA, new { displayName = "Intruder" });
        var intoNowhere = await ownerB.PostAsJsonAsync(
            "/api/v1/workspaces/no-such-kitchen/brand-style-guides", new { displayName = "Intruder" }, Key(), cancellation);
        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(1, await GuideCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task The_audit_entry_names_ids_and_counts_and_none_of_the_creators_words()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Quietly private name",
            questionnaire = new { voice = "Quietly private voice", alwaysDo = new[] { "Quietly private rule" } },
        });

        var audit = await InScopeAsync(_fixture.WorkspaceA, db => db.AuditLogs
            .SingleAsync(log => log.Action == BrandAuditActions.StyleGuideCreated, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Quietly", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("1 section(s), 1 rule(s), 0 source(s)", audit.Summary, StringComparison.Ordinal);
    }
}
