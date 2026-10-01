using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
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
/// <c>GET .../versions/{n}/extraction</c> and <c>POST .../versions/{n}/extraction/corrections</c> through the real
/// Gateway, over an in-memory object store: the two contracts of 11A.11.
/// </summary>
/// <remarks>
/// <para>
/// Two routes in one file because they are one feature: a correction is composed from a read, quotes the artifact
/// that read returned, and is answered in the same shape. Most of what is worth asserting is that the two agree —
/// the same situations are the same 404, and neither carries an address.
/// </para>
/// <para>
/// Artifacts are seeded directly rather than produced by running the extraction worker. The worker is driven end
/// to end in <see cref="BrandSourceExtractionQueueTests"/>, which also covers a correction landing on top of a
/// real parser's output; what this file is about is the HTTP contract, and seeding is how it reaches states — an
/// <c>Unsupported</c> artifact, a second correction — that would otherwise take a parser and a clock to arrange.
/// </para>
/// </remarks>
public sealed class BrandSourceExtractionReviewEndpointTests : IAsyncLifetime
{
    private const string ViewerEmail = "extraction-review-viewer-a@example.com";

    private const string Password = "correct horse battery";

    private const string Text = "# House style\n\nWarm, plain, never breathless.\n";

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

    // ---- Reading ----

    [Fact]
    public async Task Reading_extracted_text_carries_the_text_the_artifact_identity_and_its_origin()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var extractionId = await SeedTextAsync(seeded, ordinal: 1, Text);

        var body = await BodyOf(await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Succeeded", body.GetProperty("state").GetString());
        Assert.Equal(extractionId, body.GetProperty("id").GetGuid());
        Assert.Equal(1, body.GetProperty("ordinal").GetInt32());
        Assert.Equal("Extracted", body.GetProperty("origin").GetString());
        Assert.Equal(Text, body.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("at").ValueKind);
    }

    [Fact]
    public async Task The_read_carries_no_address_no_checksum_and_no_membership_id()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        await SeedTextAsync(seeded, ordinal: 1, Text);

        var raw = await (await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The whole point of the pointer staying in the data layer: nothing here is an address a client could
        // follow, and nothing names the person on the row.
        Assert.DoesNotContain("workspaces/", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brand-sources", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256:", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reading_an_unsupported_extraction_gives_the_reason_and_no_text()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        await SeedReviewStateAsync(seeded, ordinal: 1, BrandSourceExtractionStatus.Unsupported, BrandSourceExtractionReasons.Image);

        var body = await BodyOf(await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken));

        Assert.Equal("Unsupported", body.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("text").ValueKind);

        // Says plainly that nothing was recognised, rather than returning an empty success.
        Assert.Equal(BrandSourceExtractionReasons.Image, body.GetProperty("reason").GetString());
        Assert.Contains("No text recognition is performed", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Reading_a_version_nothing_has_read_yet_is_a_state_rather_than_a_404()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);

        var response = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken);

        // The version exists and downloads; a creator watching a queued extraction needs "not yet", not "no
        // such thing".
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("NotExtracted", body.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("id").ValueKind);
        Assert.Equal(0, body.GetProperty("ordinal").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("at").ValueKind);
    }

    [Fact]
    public async Task An_older_version_s_text_stays_readable_after_a_replacement()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        await SeedTextAsync(seeded, ordinal: 1, "Version one's text.\n");

        var replaced = await ReplaceAsync(client, seeded.DocumentId);
        await SeedTextAsync(replaced, ordinal: 1, "Version two's text.\n");

        var first = await BodyOf(await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken));
        var second = await BodyOf(await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 2), TestContext.Current.CancellationToken));

        // History is readable: a guide version citing version 1's text can still show what it cited.
        Assert.Equal("Version one's text.\n", first.GetProperty("text").GetString());
        Assert.Equal("Version two's text.\n", second.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_viewer_may_read_the_text_but_not_correct_it()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(owner);
        var extractionId = await SeedTextAsync(seeded, ordinal: 1, Text);

        using var viewer = await _fixture.SignInAsync(
            ViewerEmail, Password, TestContext.Current.CancellationToken);

        var read = await viewer.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken);
        var corrected = await CorrectAsync(viewer, seeded.DocumentId, 1, extractionId, "Mine now.\n");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, corrected.StatusCode);
    }

    [Fact]
    public async Task Unknown_removed_cross_workspace_and_a_version_that_does_not_exist_are_one_404()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        await SeedTextAsync(seeded, ordinal: 1, Text);

        var unknown = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, Guid.NewGuid(), 1), TestContext.Current.CancellationToken);
        var noSuchVersion = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 7), TestContext.Current.CancellationToken);

        using var other = await SignInAsync(_fixture.WorkspaceB);
        var crossWorkspace = await other.GetAsync(
            ExtractionIn(_fixture.WorkspaceB, seeded.DocumentId, 1), TestContext.Current.CancellationToken);

        await SetStatusAsync(seeded.DocumentId, BrandSourceDocumentStatus.Removed);
        var removed = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken);

        foreach (var response in new[] { unknown, noSuchVersion, crossWorkspace, removed })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Text_whose_stored_object_has_gone_is_a_503_rather_than_a_404()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        await SeedTextAsync(seeded, ordinal: 1, Text);

        await _store.DeleteAsync(
            BrandSourceObjectKey.Container,
            BrandSourceObjectKey.ForExtractedText(_fixture.WorkspaceA.Id, seeded.DocumentId, seeded.VersionId, 1),
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken);

        // The document is there and the row says there is text, so this is a fault to retry rather than a
        // document to report as missing.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceStorageUnavailable, (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Correcting ----

    [Fact]
    public async Task A_correction_becomes_the_next_artifact_and_leaves_the_upload_and_the_earlier_text_alone()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);
        var originalKey = BrandSourceObjectKey.ForOriginal(
            _fixture.WorkspaceA.Id, seeded.DocumentId, seeded.VersionId);
        var firstKey = BrandSourceObjectKey.ForExtractedText(
            _fixture.WorkspaceA.Id, seeded.DocumentId, seeded.VersionId, 1);

        var response = await CorrectAsync(
            client, seeded.DocumentId, 1, first, "# House style\n\nWarm, plain, never breathless. Ever.\n");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/brand-source-documents/{seeded.DocumentId}"
                + "/versions/1/extraction",
            response.Headers.Location?.ToString());

        var body = await BodyOf(response);
        Assert.Equal("Succeeded", body.GetProperty("state").GetString());
        Assert.Equal("Corrected", body.GetProperty("origin").GetString());
        Assert.Equal(2, body.GetProperty("ordinal").GetInt32());
        Assert.NotEqual(first, body.GetProperty("id").GetGuid());
        Assert.Equal("# House style\n\nWarm, plain, never breathless. Ever.\n", body.GetProperty("text").GetString());
        Assert.Equal("Fixed a word.", body.GetProperty("reason").GetString());

        // Nothing was overwritten: the uploaded file and the first artifact are both still exactly there.
        Assert.Contains(originalKey, _store.Keys);
        Assert.Equal(Text, await StoredTextAsync(firstKey));

        // And the earlier artifact's row survives, so what a citation pinned still resolves.
        Assert.Equal(2, (await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId)).Count);

        // The document's own state now reports the correction, through the surfaces 11A.6 and 11A.7 already have.
        var detail = await BodyOf(await client.GetAsync(
            $"{SourcesIn(_fixture.WorkspaceA)}/{seeded.DocumentId}", TestContext.Current.CancellationToken));
        Assert.Equal("Succeeded", detail.GetProperty("extraction").GetProperty("state").GetString());
        Assert.Equal("Corrected", detail.GetProperty("extraction").GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Correcting_an_unsupported_extraction_is_how_a_scan_gets_its_text()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var unreadable = await SeedReviewStateAsync(
            seeded, ordinal: 1, BrandSourceExtractionStatus.Unsupported, BrandSourceExtractionReasons.NoTextLayer);

        var body = await BodyOf(await CorrectAsync(
            client, seeded.DocumentId, 1, unreadable, "Typed out from the scan.\n"));

        // The point of the route, not an edge case: a document with no text layer becomes a document with text.
        Assert.Equal("Succeeded", body.GetProperty("state").GetString());
        Assert.Equal("Corrected", body.GetProperty("origin").GetString());
        Assert.Equal("Typed out from the scan.\n", body.GetProperty("text").GetString());

        // The review state it replaced is still on the record, carrying why there was no text.
        var artifacts = await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId);
        Assert.Equal(BrandSourceExtractionStatus.Unsupported, artifacts[0].Status);
        Assert.Equal(BrandSourceExtractionReasons.NoTextLayer, artifacts[0].Reason);
    }

    [Fact]
    public async Task A_correction_records_its_actor_and_their_reason()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);

        await CorrectAsync(client, seeded.DocumentId, 1, first, "Corrected.\n", reason: "The heading was garbled.");

        var corrected = (await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId))[1];

        Assert.Equal(BrandSourceExtractionOrigin.Corrected, corrected.Origin);
        Assert.Equal("The heading was garbled.", corrected.Reason);

        // The schema insists a corrected row names its author, and the resolved context is where it comes from.
        Assert.NotNull(corrected.CreatedByMembershipId);

        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var entry = await db.AuditLogs.SingleAsync(
            row => row.Action == BrandAuditActions.SourceDocumentTextCorrected, TestContext.Current.CancellationToken);

        Assert.NotNull(entry.ActorUserId);
        Assert.Contains("ordinal 2", entry.Summary);

        // The creator's own words stay on the row; an audit summary is not where free text about a private
        // document belongs.
        Assert.DoesNotContain("garbled", entry.Summary);
    }

    [Fact]
    public async Task A_correction_quoting_a_superseded_artifact_is_a_conflict_that_writes_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);

        await CorrectAsync(client, seeded.DocumentId, 1, first, "Mine.\n");

        // Composed against the artifact the first correction replaced.
        var stale = await CorrectAsync(client, seeded.DocumentId, 1, first, "Mine too.\n");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceExtractionConflict, (await BodyOf(stale)).GetProperty("code").GetString());

        // Two artifacts, not three, and no orphaned object at the ordinal the loser would have taken.
        Assert.Equal(2, (await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId)).Count);
        Assert.DoesNotContain(
            BrandSourceObjectKey.ForExtractedText(_fixture.WorkspaceA.Id, seeded.DocumentId, seeded.VersionId, 3),
            _store.Keys);
    }

    [Fact]
    public async Task A_version_whose_text_has_not_been_read_cannot_be_corrected_yet()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);

        var response = await CorrectAsync(client, seeded.DocumentId, 1, Guid.NewGuid(), "Guessing.\n");

        // A correction at ordinal 1 would race the worker about to write it, and the loser of that race is
        // whichever one the creator cares about.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceExtractionPendingConflict, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Empty(await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId));
    }

    [Fact]
    public async Task A_superseded_version_s_text_reads_but_cannot_be_corrected()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);
        await ReplaceAsync(client, seeded.DocumentId);

        var read = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken);
        var corrected = await CorrectAsync(client, seeded.DocumentId, 1, first, "Too late.\n");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, corrected.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceExtractionSupersededConflict,
            (await BodyOf(corrected)).GetProperty("code").GetString());
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId));
    }

    [Fact]
    public async Task An_archived_document_s_text_reads_but_cannot_be_corrected()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);
        await SetStatusAsync(seeded.DocumentId, BrandSourceDocumentStatus.Archived);

        var read = await client.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, seeded.DocumentId, 1), TestContext.Current.CancellationToken);
        var corrected = await CorrectAsync(client, seeded.DocumentId, 1, first, "On the shelf.\n");

        // An archive is a shelf: the text still reads, and a write waits for a restore.
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, corrected.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceArchivedConflict, (await BodyOf(corrected)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Text_identical_to_what_is_stored_is_answered_without_writing_anything()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);

        // Sent with Windows line endings and trailing blank lines, which normalize to exactly what is stored.
        var body = await BodyOf(await CorrectAsync(
            client, seeded.DocumentId, 1, first, Text.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n\r\n"));

        Assert.Equal(first, body.GetProperty("id").GetGuid());
        Assert.Equal(1, body.GetProperty("ordinal").GetInt32());
        Assert.Equal("Extracted", body.GetProperty("origin").GetString());

        // An ordinal that changes nothing is provenance a later reader has to explain for no gain.
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId));
    }

    [Fact]
    public async Task A_replayed_correction_returns_the_original_answer_and_writes_nothing_new()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);
        var key = Guid.NewGuid().ToString("N");

        var original = await CorrectAsync(client, seeded.DocumentId, 1, first, "Mine.\n", idempotencyKey: key);
        var replayed = await CorrectAsync(client, seeded.DocumentId, 1, first, "Mine.\n", idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Created, replayed.StatusCode);
        Assert.Equal("true", replayed.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        // Without the key the retry would quote an artifact its own first attempt superseded, and be told it
        // conflicted with itself.
        Assert.Equal((await BodyOf(original)).GetProperty("id").GetGuid(), (await BodyOf(replayed)).GetProperty("id").GetGuid());
        Assert.Equal(2, (await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId)).Count);
    }

    [Fact]
    public async Task The_same_key_describing_a_different_correction_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);
        var key = Guid.NewGuid().ToString("N");

        await CorrectAsync(client, seeded.DocumentId, 1, first, "Mine.\n", idempotencyKey: key);
        var conflicting = await CorrectAsync(
            client, seeded.DocumentId, 1, first, "Something else entirely.\n", idempotencyKey: key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflicting.StatusCode);
        Assert.Equal(
            IdempotencyPolicy.KeyReusedCode, (await BodyOf(conflicting)).GetProperty("code").GetString());
        Assert.Equal(2, (await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId)).Count);
    }

    [Theory]
    [InlineData("", "Reason", "empty text")]
    [InlineData("   \n\n", "Reason", "whitespace only")]
    [InlineData("Warm​and plain.\n", "Reason", "a zero-width character")]
    [InlineData("Warm‮and plain.\n", "Reason", "a bidirectional override")]
    [InlineData("Warm\u0007and plain.\n", "Reason", "a control character")]
    [InlineData("Corrected.\n", "", "no reason")]
    public async Task Text_and_reason_are_refused_at_their_boundaries(string text, string reason, string why)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);

        var response = await CorrectAsync(client, seeded.DocumentId, 1, first, text, reason: reason);

        // Refused rather than quietly cleaned: a creator whose text came back different with nothing said about
        // it has had their words rewritten, which is the thing this feature exists not to do.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceInvalidRequest,
            (await BodyOf(response)).GetProperty("code").GetString(),
            StringComparer.Ordinal);
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId));
        Assert.True(why.Length > 0);
    }

    [Fact]
    public async Task Text_past_the_stored_limit_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);

        var oversize = new string('a', (int)BrandPolicy.ExtractedTextMaxBytes + 1);
        var response = await CorrectAsync(client, seeded.DocumentId, 1, first, oversize);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceInvalidRequest, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId));
    }

    [Fact]
    public async Task A_correction_that_cannot_be_stored_records_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedDocumentAsync(client);
        var first = await SeedTextAsync(seeded, ordinal: 1, Text);

        _store.Unavailable = true;
        var response = await CorrectAsync(client, seeded.DocumentId, 1, first, "Mine.\n");
        _store.Unavailable = false;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceStorageUnavailable, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceA, seeded.VersionId));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Neither_workspace_can_read_or_correct_the_other_s_text()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA);
        using var inB = await SignInAsync(_fixture.WorkspaceB);

        var a = await SeedDocumentAsync(inA);
        var b = await SeedDocumentAsync(inB, workspace: _fixture.WorkspaceB);
        var inAArtifact = await SeedTextAsync(a, ordinal: 1, "Workspace A voice.\n");
        await SeedTextAsync(b, ordinal: 1, "Workspace B voice.\n", workspace: _fixture.WorkspaceB);

        // B asking for A's document, under B's own slug and under A's: the first is not B's document, and the
        // second is a workspace B is not a member of.
        var underB = await inB.GetAsync(
            ExtractionIn(_fixture.WorkspaceB, a.DocumentId, 1), TestContext.Current.CancellationToken);
        var underA = await inB.GetAsync(
            ExtractionIn(_fixture.WorkspaceA, a.DocumentId, 1), TestContext.Current.CancellationToken);
        var corrected = await CorrectAsync(inB, a.DocumentId, 1, inAArtifact, "Not mine.\n", workspace: _fixture.WorkspaceB);

        Assert.Equal(HttpStatusCode.NotFound, underB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, underA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, corrected.StatusCode);

        // A's text is untouched and still reads as A's; B's own artifact count has not moved.
        Assert.Equal(
            "Workspace A voice.\n",
            (await BodyOf(await inA.GetAsync(
                ExtractionIn(_fixture.WorkspaceA, a.DocumentId, 1), TestContext.Current.CancellationToken)))
                .GetProperty("text").GetString());
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceA, a.VersionId));
        Assert.Single(await ArtifactsAsync(_fixture.WorkspaceB, b.VersionId));
    }

    [Fact]
    public async Task Each_workspace_s_corrected_text_is_stored_under_its_own_prefix()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA);
        using var inB = await SignInAsync(_fixture.WorkspaceB);

        var a = await SeedDocumentAsync(inA);
        var b = await SeedDocumentAsync(inB, workspace: _fixture.WorkspaceB);
        var firstInA = await SeedTextAsync(a, ordinal: 1, "A.\n");
        var firstInB = await SeedTextAsync(b, ordinal: 1, "B.\n", workspace: _fixture.WorkspaceB);

        await CorrectAsync(inA, a.DocumentId, 1, firstInA, "A corrected.\n");
        await CorrectAsync(inB, b.DocumentId, 1, firstInB, "B corrected.\n", workspace: _fixture.WorkspaceB);

        var keyInA = BrandSourceObjectKey.ForExtractedText(_fixture.WorkspaceA.Id, a.DocumentId, a.VersionId, 2);
        var keyInB = BrandSourceObjectKey.ForExtractedText(_fixture.WorkspaceB.Id, b.DocumentId, b.VersionId, 2);

        Assert.StartsWith($"workspaces/{_fixture.WorkspaceA.Id:N}/", keyInA);
        Assert.StartsWith($"workspaces/{_fixture.WorkspaceB.Id:N}/", keyInB);
        Assert.Equal("A corrected.\n", await StoredTextAsync(keyInA));
        Assert.Equal("B corrected.\n", await StoredTextAsync(keyInB));
    }

    // ---- Helpers ----

    private sealed record SeededVersion(SeededWorkspace Workspace, Guid DocumentId, Guid VersionId);

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static string SourcesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static string ExtractionIn(SeededWorkspace workspace, Guid documentId, int versionNumber) =>
        $"{SourcesIn(workspace)}/{documentId}/versions/{versionNumber}/extraction";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> CorrectAsync(
        GatewayClient client,
        Guid documentId,
        int versionNumber,
        Guid expectedExtractionId,
        string text,
        string reason = "Fixed a word.",
        string? idempotencyKey = null,
        SeededWorkspace? workspace = null) =>
        client.PostAsJsonAsync(
            $"{ExtractionIn(workspace ?? _fixture.WorkspaceA, documentId, versionNumber)}/corrections",
            new { expectedExtractionId, text, reason },
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

    /// <summary>Uploads one document and returns it with its first version's id.</summary>
    private async Task<SeededVersion> SeedDocumentAsync(GatewayClient client, SeededWorkspace? workspace = null)
    {
        var target = workspace ?? _fixture.WorkspaceA;
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf("house style"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        var response = await client.PostAsync(
            SourcesIn(target), form, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return new SeededVersion(
            target, body.GetProperty("id").GetGuid(), body.GetProperty("currentVersion").GetProperty("id").GetGuid());
    }

    /// <summary>Replaces a document's file and returns the new version.</summary>
    private async Task<SeededVersion> ReplaceAsync(GatewayClient client, Guid documentId)
    {
        var token = (await BodyOf(await client.GetAsync(
                $"{SourcesIn(_fixture.WorkspaceA)}/{documentId}", TestContext.Current.CancellationToken)))
            .GetProperty("concurrencyToken").GetString()!;

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf("second"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent(token), "expectedConcurrencyToken");

        var response = await client.PostAsync(
            $"{SourcesIn(_fixture.WorkspaceA)}/{documentId}/versions",
            form,
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return new SeededVersion(
            _fixture.WorkspaceA, documentId, body.GetProperty("currentVersion").GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Writes one succeeded artifact — object and row — for a version, and returns its id.
    /// </summary>
    /// <remarks>
    /// Seeded rather than extracted, for the reason this file's own remarks give: the worker is driven end to end
    /// elsewhere, and these tests need to reach states a parser would take a particular document to produce.
    /// </remarks>
    private async Task<Guid> SeedTextAsync(
        SeededVersion seeded, int ordinal, string text, SeededWorkspace? workspace = null)
    {
        var target = workspace ?? seeded.Workspace;
        var bytes = Encoding.UTF8.GetBytes(text);
        var key = BrandSourceObjectKey.ForExtractedText(target.Id, seeded.DocumentId, seeded.VersionId, ordinal);

        var write = await _store.PutAsync(
            BrandSourceObjectKey.Container,
            key,
            new MemoryStream(bytes),
            "text/plain; charset=utf-8",
            BrandPolicy.ExtractedTextMaxBytes,
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectWriteOutcome.Stored, write.Outcome);

        return await AddArtifactAsync(target, seeded, new BrandSourceExtraction
        {
            Ordinal = ordinal,
            Status = BrandSourceExtractionStatus.Succeeded,
            Origin = BrandSourceExtractionOrigin.Extracted,
            ExtractedTextObjectKey = key,
            ContentChecksum = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
        });
    }

    /// <summary>Writes one review-state artifact — no object, no checksum — and returns its id.</summary>
    private Task<Guid> SeedReviewStateAsync(
        SeededVersion seeded, int ordinal, BrandSourceExtractionStatus status, string reason) =>
        AddArtifactAsync(seeded.Workspace, seeded, new BrandSourceExtraction
        {
            Ordinal = ordinal,
            Status = status,
            Origin = BrandSourceExtractionOrigin.Extracted,
            Reason = reason,
        });

    private async Task<Guid> AddArtifactAsync(
        SeededWorkspace workspace, SeededVersion seeded, BrandSourceExtraction extraction)
    {
        extraction.Id = Guid.NewGuid();
        extraction.WorkspaceId = workspace.Id;
        extraction.BrandSourceDocumentVersionId = seeded.VersionId;
        extraction.CreatedAt = DateTimeOffset.UtcNow;

        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.BrandSourceExtractions.Add(extraction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return extraction.Id;
    }

    private async Task<List<BrandSourceExtraction>> ArtifactsAsync(SeededWorkspace workspace, Guid versionId)
    {
        await using var scope = ScopeFor(workspace);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .BrandSourceExtractions
            .AsNoTracking()
            .Where(extraction => extraction.BrandSourceDocumentVersionId == versionId)
            .OrderBy(extraction => extraction.Ordinal)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> StoredTextAsync(string objectKey)
    {
        await using var stored = await _store.OpenReadAsync(
            BrandSourceObjectKey.Container, objectKey, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        using var reader = new StreamReader(stored.Content, Encoding.UTF8);

        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Moves a document to a status, for the two the review routes treat differently.</summary>
    private async Task SetStatusAsync(Guid documentId, BrandSourceDocumentStatus status)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var document = await db.BrandSourceDocuments.SingleAsync(
            candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);

        document.Status = status;
        document.ArchivedAt = status is BrandSourceDocumentStatus.Archived ? DateTimeOffset.UtcNow : null;

        // A tombstone names when and who — CK_BrandSourceDocuments_Removed_Consistent refuses one that does not.
        var removed = status is BrandSourceDocumentStatus.Removed;
        document.RemovedAt = removed ? DateTimeOffset.UtcNow : null;
        document.RemovedByMembershipId = removed ? Guid.NewGuid() : null;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return scope;
    }
}
