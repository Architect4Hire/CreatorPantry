using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Data.SqlTypes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// <c>GET .../brand-visual-guide</c> through the real Gateway (11A.21b): which visual style an image screen would
/// use, what the look says, which references could be named and whether each would be used — and that none of it
/// crosses workspaces.
/// </summary>
public sealed class BrandVisualGuideEndpointTests : IAsyncLifetime
{
    private const string ViewerEmail = "visual-guide-viewer-a@example.com";

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

    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    // ---- No guide / no look ----

    [Fact]
    public async Task A_workspace_with_no_active_guide_gets_an_answer_and_no_references_to_choose()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedDocumentAsync(_fixture.WorkspaceA, "Moodboard", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 2);

        var response = await GetAsync(client, _fixture.WorkspaceA, "photography-concept");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeGuide").ValueKind);
        Assert.False(body.GetProperty("hasVisualGuidance").GetBoolean());
        Assert.Empty(body.GetProperty("styleLines").EnumerateArray());
        Assert.Empty(body.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public async Task A_look_the_creator_marked_as_not_theirs_is_never_offered_as_a_reference()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Voice only", questionnaire = new { voice = "Warm." } });
        await SeedDocumentAsync(_fixture.WorkspaceA, "Not my look", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.NotMyVoice, chunks: 2);
        await SeedDocumentAsync(_fixture.WorkspaceA, "Moodboard", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 2);

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "photography-concept"));

        var titles = body.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Moodboard"], titles);
    }

    [Fact]
    public async Task An_active_guide_with_no_visual_sections_says_so_and_still_offers_references()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Voice only", questionnaire = new { voice = "Warm." } });
        await SeedDocumentAsync(_fixture.WorkspaceA, "Moodboard", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 1);

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "image-prompt"));

        Assert.Equal("Voice only", body.GetProperty("activeGuide").GetProperty("name").GetString());
        Assert.False(body.GetProperty("hasVisualGuidance").GetBoolean());
        Assert.Empty(body.GetProperty("styleLines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("negativeGuidance").ValueKind);
        Assert.Single(body.GetProperty("references").EnumerateArray());
    }

    // ---- The look ----

    [Fact]
    public async Task The_look_is_the_visual_sections_only_with_negative_guidance_apart_and_no_voice_or_rules()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Bright table",
            questionnaire = new { voice = "Warm and plain.", alwaysDo = new[] { "Lead with the dish." } },
            sections = new object[]
            {
                new { sectionKey = "VisualIdentity", body = "Warm, honest, never staged." },
                new { sectionKey = "PhotographyDirection", body = "Soft window light." },
                new { sectionKey = "NegativeVisualGuidance", body = "No flash, no neon." },
            },
        });

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "photography-concept"));
        var lines = body.GetProperty("styleLines").EnumerateArray()
            .Select(line => (line.GetProperty("label").GetString(), line.GetProperty("summary").GetString())).ToList();

        Assert.True(body.GetProperty("hasVisualGuidance").GetBoolean());
        Assert.Equal(
            [("Visual identity", "Warm, honest, never staged."), ("Photography direction", "Soft window light.")],
            lines);
        Assert.Equal("No flash, no neon.", body.GetProperty("negativeGuidance").GetString());

        // Brand voice and Do/Don't rules are never part of an image task, so they never appear in its preview.
        var text = body.GetRawText();
        Assert.DoesNotContain("Warm and plain", text);
        Assert.DoesNotContain("Lead with the dish", text);
        Assert.DoesNotContain("\"Voice\"", text);
    }

    [Fact]
    public async Task Each_image_task_gets_its_own_selection()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Looks",
            sections = new object[] { new { sectionKey = "ImagePromptGuidance", body = "Overhead, natural light." } },
        });

        var concept = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "photography-concept"));
        var prompt = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "image-prompt"));

        // Both tasks are grounded in the same four visual sections today; the table, not this route, decides.
        Assert.Contains("Image prompt guidance", concept.GetRawText() + prompt.GetRawText());
        Assert.True(prompt.GetProperty("hasVisualGuidance").GetBoolean());
    }

    // ---- References ----

    [Fact]
    public async Task A_reference_is_usable_only_if_text_from_it_would_reach_a_generation()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new { displayName = "G" });

        var ok = await SeedDocumentAsync(_fixture.WorkspaceA, "Spring moodboard", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 3);
        var notReady = await SeedDocumentAsync(_fixture.WorkspaceA, "Fresh upload", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, ExtractionKind.Succeeded, chunks: 0);
        var unread = await SeedDocumentAsync(_fixture.WorkspaceA, "Unread board", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, ExtractionKind.None, chunks: 0);
        var unsupported = await SeedDocumentAsync(_fixture.WorkspaceA, "Odd file", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, ExtractionKind.Unsupported, chunks: 0);
        var failed = await SeedDocumentAsync(_fixture.WorkspaceA, "Broken scan", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, ExtractionKind.Failed, chunks: 0);

        var references = await ReferencesAsync(client);

        Assert.True(references[ok].Usable);
        Assert.Null(references[ok].Reason);

        Assert.False(references[notReady].Usable);
        Assert.StartsWith("Its text is not ready to use yet.", references[notReady].Reason);
        Assert.StartsWith("We have not read it yet.", references[unread].Reason);
        Assert.StartsWith("We cannot read text from this kind of file.", references[unsupported].Reason);
        Assert.StartsWith("We could not read it.", references[failed].Reason);

        foreach (var id in new[] { notReady, unread, unsupported, failed })
        {
            Assert.EndsWith(BrandVisualReferenceReasons.Guarantee, references[id].Reason);
        }
    }

    [Fact]
    public async Task Only_active_visual_references_are_offered()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new { displayName = "G" });

        var byType = await SeedDocumentAsync(_fixture.WorkspaceA, "By type", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.Background, chunks: 1);
        var byPurpose = await SeedDocumentAsync(_fixture.WorkspaceA, "By purpose", BrandSourceDocumentType.Other, BrandSourcePurpose.VisualDirection, chunks: 1);
        var writing = await SeedDocumentAsync(_fixture.WorkspaceA, "A writing sample", BrandSourceDocumentType.WritingSample, BrandSourcePurpose.Voice, chunks: 1);
        var archived = await SeedDocumentAsync(_fixture.WorkspaceA, "Shelved", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 1, status: BrandSourceDocumentStatus.Archived);

        var references = await ReferencesAsync(client);

        Assert.Contains(byType, references.Keys);
        Assert.Contains(byPurpose, references.Keys);
        Assert.DoesNotContain(writing, references.Keys);
        Assert.DoesNotContain(archived, references.Keys);
    }

    [Fact]
    public async Task A_reference_is_a_title_an_id_and_a_boolean_and_nothing_about_the_file()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new { displayName = "G" });
        await SeedDocumentAsync(_fixture.WorkspaceA, "Moodboard", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 2);

        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "image-prompt"));
        var reference = Assert.Single(body.GetProperty("references").EnumerateArray());

        Assert.Equal(
            ["documentId", "title", "unusableReason", "usable"],
            reference.EnumerateObject().Select(property => property.Name).Order().ToArray());

        var text = body.GetRawText();
        Assert.DoesNotContain("pdf", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brand-sources/", text);
        Assert.DoesNotContain("We write like a friend", text);
    }

    // ---- Staleness ----

    [Fact]
    public async Task A_guide_whose_source_was_replaced_is_reported_stale_and_is_still_the_active_one()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var document = await UploadAsync(client, _fixture.WorkspaceA, "look.pdf");
        await ActivatedGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Sourced look",
            sourceDocuments = new[] { new { documentId = document.Id, versionNumber = 1 } },
        });

        await ReplaceAsync(client, _fixture.WorkspaceA, document);

        var guide = (await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "image-prompt"))).GetProperty("activeGuide");

        Assert.Equal("Sourced look", guide.GetProperty("name").GetString());
        Assert.True(guide.GetProperty("isStale").GetBoolean());
        Assert.Equal(1, guide.GetProperty("staleSourceCount").GetInt32());
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("")]
    [InlineData("?task=editorial-package")]
    [InlineData("?task=seo-package")]
    [InlineData("?task=recipe-review")]
    [InlineData("?task=ImagePrompt")]
    public async Task A_task_that_is_not_grounded_in_the_brands_look_is_refused(string query)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.GetAsync(Route(_fixture.WorkspaceA) + query, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ai.brandVisualGuide.invalid_request", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Roles and isolation ----

    [Fact]
    public async Task A_viewer_may_read_it()
    {
        using var viewer = await _fixture.SignInAsync(ViewerEmail, Password, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(viewer, _fixture.WorkspaceA, "image-prompt")).StatusCode);
    }

    [Fact]
    public async Task One_workspaces_style_and_references_never_appear_in_another()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        await ActivatedGuideAsync(ownerA, _fixture.WorkspaceA, new
        {
            displayName = "A look",
            sections = new object[] { new { sectionKey = "VisualIdentity", body = "A-only wording." } },
        });
        await ActivatedGuideAsync(ownerB, _fixture.WorkspaceB, new
        {
            displayName = "B secret look",
            sections = new object[] { new { sectionKey = "VisualIdentity", body = "B-only wording." } },
        });

        var inA = await SeedDocumentAsync(_fixture.WorkspaceA, "A board", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 1);
        var inB = await SeedDocumentAsync(_fixture.WorkspaceB, "B secret board", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, chunks: 1);

        var seenByA = await BodyOf(await GetAsync(ownerA, _fixture.WorkspaceA, "image-prompt"));
        var seenByB = await BodyOf(await GetAsync(ownerB, _fixture.WorkspaceB, "image-prompt"));

        Assert.Equal("A look", seenByA.GetProperty("activeGuide").GetProperty("name").GetString());
        Assert.Equal(inA, Assert.Single(seenByA.GetProperty("references").EnumerateArray()).GetProperty("documentId").GetGuid());
        Assert.DoesNotContain("secret", seenByA.GetRawText());
        Assert.DoesNotContain("B-only", seenByA.GetRawText());

        Assert.Equal("B secret look", seenByB.GetProperty("activeGuide").GetProperty("name").GetString());
        Assert.Equal(inB, Assert.Single(seenByB.GetProperty("references").EnumerateArray()).GetProperty("documentId").GetGuid());
        Assert.DoesNotContain("A board", seenByB.GetRawText());

        var crossing = await GetAsync(ownerA, _fixture.WorkspaceB, "image-prompt");
        var missing = await ownerA.GetAsync(
            "/api/v1/workspaces/no-such-workspace/brand-visual-guide?task=image-prompt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            (await BodyOf(missing)).GetProperty("code").GetString(), (await BodyOf(crossing)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Text_stamped_with_another_workspace_never_makes_a_reference_usable()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        await ActivatedGuideAsync(ownerA, _fixture.WorkspaceA, new { displayName = "A" });

        // A's document has extracted text but nothing indexed yet, so it is not usable.
        var inA = await SeedDocumentAsync(_fixture.WorkspaceA, "Moodboard", BrandSourceDocumentType.VisualReference, BrandSourcePurpose.VisualDirection, ExtractionKind.Succeeded, chunks: 0);

        // The adversarial row: indexed text stamped with workspace B but pointing at A's document, version and
        // extraction. If the passage read leaned on the document id alone, A would now see a usable reference.
        var (versionId, extractionId) = await InScopeAsync(_fixture.WorkspaceA, async db =>
        {
            var version = await db.BrandSourceDocumentVersions.SingleAsync(v => v.BrandSourceDocumentId == inA, TestContext.Current.CancellationToken);
            var extraction = await db.BrandSourceExtractions.SingleAsync(e => e.BrandSourceDocumentVersionId == version.Id, TestContext.Current.CancellationToken);

            return (version.Id, extraction.Id);
        });

        var attached = await TryAttachChunksAsync(_fixture.WorkspaceB, inA, versionId, extractionId);

        var references = await ReferencesAsync(ownerA);

        // Either the schema refuses the cross-attached row outright, or the read ignores it. Both keep A's
        // reference unusable, and neither lets B's text speak for A's document.
        Assert.False(references[inA].Usable, attached ? "a row stamped with another workspace was read" : "unexpectedly usable");
    }

    /// <summary>Writes an indexed-text row stamped with <paramref name="workspace"/> against another workspace's document.</summary>
    private async Task<bool> TryAttachChunksAsync(SeededWorkspace workspace, Guid documentId, Guid versionId, Guid extractionId)
    {
        try
        {
            await InScopeAsync(workspace, async db =>
            {
                var set = new BrandSourceChunkSet
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceDocumentId = documentId,
                    BrandSourceDocumentVersionId = versionId,
                    BrandSourceExtractionId = extractionId,
                    SourceStatus = BrandSourceExtractionStatus.Succeeded,
                    Status = BrandSourceChunkSetStatus.Current,
                    ChunkerId = "text/paragraph-1600c-200o@1",
                    EmbeddingModel = "text-embedding-3-small",
                    EmbeddingDimension = BrandPolicy.EmbeddingDimension,
                    ChunkCount = 1,
                    CreatedAt = Moment,
                    EmbeddedAt = Moment,
                };
                db.BrandSourceChunkSets.Add(set);

                var values = new float[BrandPolicy.EmbeddingDimension];
                values[1] = 1f;
                db.BrandSourceChunks.Add(new BrandSourceChunk
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceChunkSetId = set.Id,
                    Ordinal = 1,
                    StartByteOffset = 0,
                    ByteLength = 1600,
                    ContentChecksum = Checksum,
                    Text = "Planted by the other workspace.",
                    Embedding = new SqlVector<float>(values),
                });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                return 0;
            });

            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    // ---- Harness ----

    private enum ExtractionKind { None, Succeeded, Unsupported, Failed }

    private sealed record SeededGuide(Guid Id, Guid VersionId);

    private sealed record SeededDocument(Guid Id, string Token);

    private sealed record ReferenceLine(bool Usable, string? Reason);

    private static string Route(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-visual-guide";

    private static string GuidesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> GetAsync(GatewayClient client, SeededWorkspace workspace, string task) =>
        client.GetAsync($"{Route(workspace)}?task={task}", TestContext.Current.CancellationToken);

    private async Task<Dictionary<Guid, ReferenceLine>> ReferencesAsync(GatewayClient client)
    {
        var body = await BodyOf(await GetAsync(client, _fixture.WorkspaceA, "image-prompt"));

        return body.GetProperty("references").EnumerateArray().ToDictionary(
            reference => reference.GetProperty("documentId").GetGuid(),
            reference => new ReferenceLine(
                reference.GetProperty("usable").GetBoolean(),
                reference.GetProperty("unusableReason").GetString()));
    }

    /// <summary>
    /// Seeds a library document directly — its version, an optional extraction, and a current chunk set — because
    /// the extraction and indexing workers that would produce them are not what is under test.
    /// </summary>
    private Task<Guid> SeedDocumentAsync(
        SeededWorkspace workspace,
        string title,
        BrandSourceDocumentType type,
        BrandSourcePurpose purpose,
        ExtractionKind extraction = ExtractionKind.Succeeded,
        int chunks = 0,
        BrandSourceDocumentStatus status = BrandSourceDocumentStatus.Active) =>
        InScopeAsync(workspace, async db =>
        {
            var member = Guid.NewGuid();
            var document = new BrandSourceDocument
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                Title = title,
                DocumentType = type,
                Purpose = purpose,
                Status = status,
                ArchivedAt = status == BrandSourceDocumentStatus.Archived ? Moment : null,
                CurrentVersionNumber = 1,
                CreatedAt = Moment,
                UpdatedAt = Moment,
                CreatedByMembershipId = member,
                UpdatedByMembershipId = member,
            };
            var version = new BrandSourceDocumentVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                BrandSourceDocumentId = document.Id,
                VersionNumber = 1,
                MediaType = "application/pdf",
                SizeBytes = 1024,
                ContentChecksum = Checksum,
                OriginalFileName = "board.pdf",
                ObjectKey = $"brand-sources/{document.Id:N}/1",
                CreatedByMembershipId = member,
                CreatedAt = Moment,
            };
            db.BrandSourceDocuments.Add(document);
            db.BrandSourceDocumentVersions.Add(version);

            BrandSourceExtraction? row = null;

            if (extraction != ExtractionKind.None || chunks > 0)
            {
                row = new BrandSourceExtraction
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceDocumentVersionId = version.Id,
                    Ordinal = 1,
                    Status = extraction switch
                    {
                        ExtractionKind.Unsupported => BrandSourceExtractionStatus.Unsupported,
                        ExtractionKind.Failed => BrandSourceExtractionStatus.Failed,
                        _ => BrandSourceExtractionStatus.Succeeded,
                    },
                    Origin = BrandSourceExtractionOrigin.Extracted,
                    CreatedAt = Moment,
                };

                // The table insists a failed or unsupported extraction carries no text artifact.
                if (row.Status == BrandSourceExtractionStatus.Succeeded)
                {
                    row.ExtractedTextObjectKey = $"brand-sources/text/{version.Id:N}/1";
                    row.ContentChecksum = Checksum;
                }

                db.BrandSourceExtractions.Add(row);
            }

            if (chunks > 0)
            {
                var set = new BrandSourceChunkSet
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceDocumentId = document.Id,
                    BrandSourceDocumentVersionId = version.Id,
                    BrandSourceExtractionId = row!.Id,
                    SourceStatus = BrandSourceExtractionStatus.Succeeded,
                    Status = BrandSourceChunkSetStatus.Current,
                    ChunkerId = "text/paragraph-1600c-200o@1",
                    EmbeddingModel = "text-embedding-3-small",
                    EmbeddingDimension = BrandPolicy.EmbeddingDimension,
                    ChunkCount = chunks,
                    CreatedAt = Moment,
                    EmbeddedAt = Moment,
                };
                db.BrandSourceChunkSets.Add(set);

                for (var ordinal = 1; ordinal <= chunks; ordinal++)
                {
                    var values = new float[BrandPolicy.EmbeddingDimension];
                    values[ordinal % BrandPolicy.EmbeddingDimension] = 1f;
                    db.BrandSourceChunks.Add(new BrandSourceChunk
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = workspace.Id,
                        BrandSourceChunkSetId = set.Id,
                        Ordinal = ordinal,
                        StartByteOffset = (ordinal - 1) * 1400,
                        ByteLength = 1600,
                        ContentChecksum = Checksum,
                        Text = $"We write like a friend who happens to cook. Passage {ordinal}.",
                        Embedding = new SqlVector<float>(values),
                    });
                }
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return document.Id;
        });

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
