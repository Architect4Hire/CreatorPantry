using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data;
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
/// <c>POST .../brand-style-guides/{guideId}/versions/{versionNumber}/approval</c> through the real Gateway: the
/// approval, the no-op, the replay, every refusal, the role split against activation, and that neither
/// workspace can approve or see the other's.
/// </summary>
/// <remarks>
/// <para>
/// The route an Editor needs before an Owner can activate anything. Until it existed, activation was defined
/// only over an already-approved version and nothing in the API could produce one — which is why
/// <see cref="BrandStyleGuideActivationEndpointTests"/> seeds approvals directly. The pair of tests at the top
/// here is the join: an Editor approves through the route, and the workspace's Owner then activates.
/// </para>
/// <para>
/// <strong>The simultaneous-approval race is staged through the DataLayer</strong> rather than over HTTP, as
/// the activation tests stage theirs: two requests cannot be made to interleave deterministically, and what is
/// worth testing is the recovery, not the scheduler.
/// </para>
/// </remarks>
public sealed class BrandStyleGuideApprovalEndpointTests : IAsyncLifetime
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

    // ---- Approving ----

    [Fact]
    public async Task An_editor_can_approve_a_version()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        var response = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = "Reads like me now",
        });

        // 200 rather than 201: an approval has no read route of its own, and the guide read reports it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        var body = await BodyOf(response);
        Assert.Equal(guide.Id, body.GetProperty("guideId").GetGuid());
        Assert.Equal(guide.VersionId, body.GetProperty("versionId").GetGuid());
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Reads like me now", body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("alreadyApproved").GetBoolean());
        Assert.NotEqual(Guid.Empty, body.GetProperty("approvedByMembershipId").GetGuid());

        var stored = Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
        Assert.Equal(guide.VersionId, stored.BrandStyleGuideVersionId);
        Assert.Equal("Reads like me now", stored.Reason);
        Assert.Equal(body.GetProperty("approvedAt").GetDateTimeOffset(), stored.ApprovedAt);

        // Nothing was activated: approving and activating are two decisions, and this is only the first.
        Assert.Equal(0, await DefaultCountAsync(_fixture.WorkspaceA));
        var read = await BodyOf(await client.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{guide.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("activeVersion").ValueKind);
    }

    [Fact]
    public async Task An_editor_approves_and_the_owner_can_then_activate()
    {
        using var editor = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(editor, _fixture.WorkspaceA);
        await Approved(editor, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        // The Editor who approved cannot make it the workspace default: that gate is still the Owner's.
        var refused = await ActivateAsync(editor, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var activated = await ActivateAsync(owner, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(guide.VersionId, (await BodyOf(activated)).GetProperty("versionId").GetGuid());
        Assert.Equal(guide.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task Approval_writes_one_audit_entry_naming_the_version_and_no_creator_text()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        await Approved(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true, reason = "A secret note" });

        var entry = Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
        Assert.Equal(BrandAuditActions.StyleGuideResourceType, entry.ResourceType);
        Assert.Equal(guide.Id.ToString("D"), entry.ResourceId);

        // Nothing was moved off, so there is no before reference. The after reference matches activation's.
        Assert.Null(entry.BeforeReference);
        Assert.Equal($"{guide.Id:N}:1", entry.AfterReference);

        // The approver's own words stay on the approval row. An audit summary has to be safe to display.
        Assert.DoesNotContain("A secret note", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("version 1", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_an_already_approved_version_writes_nothing_and_cannot_rewrite_it()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        var first = await Approved(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = "Reads like me now",
        });

        // A different key and different words, so this is a fresh decision rather than a replay.
        var again = await Approved(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = "Changed my mind about why",
        });

        Assert.True(again.GetProperty("alreadyApproved").GetBoolean());

        // The original approval, down to the instant and the words: an approval is write-once, so a second
        // request reports the first rather than replacing it.
        Assert.Equal(first.GetProperty("approvedAt").GetDateTimeOffset(), again.GetProperty("approvedAt").GetDateTimeOffset());
        Assert.Equal("Reads like me now", again.GetProperty("reason").GetString());
        Assert.Equal(
            first.GetProperty("approvedByMembershipId").GetGuid(),
            again.GetProperty("approvedByMembershipId").GetGuid());

        var stored = Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
        Assert.Equal("Reads like me now", stored.Reason);

        // And no audit entry for the no-op: a trail full of them is harder to read than one without.
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_replayed_key_returns_the_first_approval_and_writes_nothing_further()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);
        var key = Guid.NewGuid().ToString("N");
        var body = new { confirmed = true, reason = "Reads like me now" };

        var first = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, body, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, body, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        // The committed answer, not a fresh decision — which would have reported alreadyApproved instead.
        var original = await BodyOf(first);
        var replayed = await BodyOf(replay);
        Assert.Equal(original.GetProperty("approvedAt").GetDateTimeOffset(), replayed.GetProperty("approvedAt").GetDateTimeOffset());
        Assert.False(replayed.GetProperty("alreadyApproved").GetBoolean());

        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
        Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task The_same_key_with_a_different_version_is_refused()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);
        await VersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);
        var key = Guid.NewGuid().ToString("N");

        Assert.Equal(
            HttpStatusCode.OK,
            (await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true }, key)).StatusCode);

        var reused = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 2, new { confirmed = true }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, await CodeOf(reused));

        // And version 2 is not approved: the refusal wrote nothing.
        Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_request_without_a_key_is_refused()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ApprovalOf(_fixture.WorkspaceA, guide.Id, 1),
            new { confirmed = true },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, await CodeOf(response));
        Assert.Empty(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    // ---- Refusals ----

    [Fact]
    public async Task An_unconfirmed_request_is_refused()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        foreach (var body in new object[] { new { }, new { confirmed = false } })
        {
            var response = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, body);

            // A well-formed body is not by itself a decision, and false is a well-formed answer meaning no.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(BrandErrorCodes.GuideInvalidRequest, await CodeOf(response));
        }

        Assert.Empty(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_refused()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        var response = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = new string('x', BrandPolicy.ReasonMaxLength + 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideInvalidRequest, await CodeOf(response));
        Assert.Empty(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_viewer_cannot_approve()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var guide = await GuideAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(
            _fixture.WorkspaceB.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await ApproveAsync(viewer, _fixture.WorkspaceB, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ApprovalsAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task An_unknown_guide_is_not_found()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);

        var response = await ApproveAsync(client, _fixture.WorkspaceA, Guid.NewGuid(), 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideNotFound, await CodeOf(response));
    }

    [Fact]
    public async Task A_version_the_guide_does_not_have_is_not_found_and_names_the_segment()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);

        var response = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 7, new { confirmed = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Read once: the body is a single-read stream, and this test needs two things out of it.
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.GuideVersionNotFound, body.GetProperty("code").GetString());

        // Told apart from the guide 404 only because it names what was wrong; the guide 404 names nothing.
        Assert.True(body.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    [Fact]
    public async Task A_version_of_an_archived_guide_cannot_be_approved()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);
        await ArchiveGuideAsync(_fixture.WorkspaceA, guide.Id);

        var response = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideArchivedConflict, await CodeOf(response));
        Assert.Empty(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_version_with_nothing_in_it_cannot_be_approved()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);

        // Legitimate: creation requires only a display name, so a guide may exist as a name while its creator
        // is still filling it in. What it cannot be is approved.
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "House voice" });

        var response = await ApproveAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideVersionEmptyConflict, await CodeOf(response));
        Assert.Empty(await ApprovalsAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_version_citing_a_replaced_document_is_approved_but_still_cannot_be_activated()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var document = await UploadAsync(owner, _fixture.WorkspaceA, "house-style.pdf");
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "Warm, direct, never fussy." },
            sourceDocuments = new[] { new { documentId = document.Id, versionNumber = 1 } },
        });

        // The cited version is superseded, which is what "stale" means.
        await ReplaceAsync(owner, _fixture.WorkspaceA, document);

        // Approval is about the wording being finished, and that stays true when a source moves on.
        var approved = await Approved(owner, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });
        Assert.False(approved.GetProperty("alreadyApproved").GetBoolean());

        // Grounding the whole workspace on it is the decision staleness blocks, and it still does.
        var activation = await ActivateAsync(owner, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });
        Assert.Equal(HttpStatusCode.Conflict, activation.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideVersionStaleConflict, await CodeOf(activation));
    }

    [Fact]
    public async Task Two_approvals_of_the_same_version_leave_one_and_tell_the_loser_to_ask_again()
    {
        using var client = await EditorOf(_fixture.WorkspaceA);
        var guide = await GuideAsync(client, _fixture.WorkspaceA);
        await Approved(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        // The loser's half of the race, staged where it is deterministic: an insert against a committed
        // approval of the same version, which the primary key refuses.
        var write = await WithDataLayerAsync(_fixture.WorkspaceA, (dataLayer, _) => dataLayer.ApproveAsync(
            guide.VersionId,
            "Signed off twice",
            Guid.NewGuid(),
            Moment,
            AuditOf(guide.Id),
            TestContext.Current.CancellationToken));

        Assert.Equal(BrandStyleGuideApprovalWrite.Conflict, write);

        // Nothing of the winner's was touched, and the loser wrote neither an approval nor an audit entry.
        var stored = Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
        Assert.NotEqual("Signed off twice", stored.Reason);
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));

        // And asking again over the route reports the approval that was recorded.
        var again = await Approved(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });
        Assert.True(again.GetProperty("alreadyApproved").GetBoolean());
    }

    // ---- Isolation ----

    [Fact]
    public async Task Neither_workspace_can_approve_or_see_the_other_s_version()
    {
        using var inA = await EditorOf(_fixture.WorkspaceA);
        using var inB = await OwnerOf(_fixture.WorkspaceB);
        var guideA = await GuideAsync(inA, _fixture.WorkspaceA);
        var guideB = await GuideAsync(inB, _fixture.WorkspaceB);

        // A's guide addressed through B's workspace, by a member of B: indistinguishable from one that never
        // existed, so B cannot even learn it is there.
        var acrossB = await ApproveAsync(inB, _fixture.WorkspaceB, guideA.Id, 1, new { confirmed = true });
        Assert.Equal(HttpStatusCode.NotFound, acrossB.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideNotFound, await CodeOf(acrossB));

        // Asserted here rather than only at the end: a leaked write would be an approval of the *same*
        // version, so the legitimate approval below would absorb it and report alreadyApproved instead.
        await NothingWrittenAsync();

        // And A's own slug does not help a member of B: membership is what the route is resolved against,
        // and a workspace they cannot see answers exactly as one that does not exist — same status, same
        // code, so the attempt cannot be used to find out which it was.
        var intoA = await ApproveAsync(inB, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true });
        var intoNowhere = await inB.PostAsJsonAsync(
            $"/api/v1/workspaces/not-a-workspace/brand-style-guides/{guideA.Id}/versions/1/approval",
            new { confirmed = true },
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(await CodeOf(intoNowhere), await CodeOf(intoA));

        await NothingWrittenAsync();

        await Approved(inA, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true });
        await Approved(inB, _fixture.WorkspaceB, guideB.Id, 1, new { confirmed = true });

        // One approval each, and each names only its own workspace's version.
        var approvalsA = Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
        var approvalsB = Assert.Single(await ApprovalsAsync(_fixture.WorkspaceB));
        Assert.Equal(guideA.VersionId, approvalsA.BrandStyleGuideVersionId);
        Assert.Equal(guideB.VersionId, approvalsB.BrandStyleGuideVersionId);
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceB));

        // Nor can B read what A approved: the guide carrying the approval is 404 to them either way round.
        foreach (var url in new[]
                 {
                     $"{GuidesIn(_fixture.WorkspaceB)}/{guideA.Id}",
                     $"{GuidesIn(_fixture.WorkspaceA)}/{guideA.Id}",
                 })
        {
            var read = await inB.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }
    }

    [Fact]
    public async Task One_key_used_in_each_workspace_approves_both_independently()
    {
        using var inA = await EditorOf(_fixture.WorkspaceA);
        using var inB = await OwnerOf(_fixture.WorkspaceB);
        var guideA = await GuideAsync(inA, _fixture.WorkspaceA);
        var guideB = await GuideAsync(inB, _fixture.WorkspaceB);
        var key = Guid.NewGuid().ToString("N");

        var first = await ApproveAsync(inA, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true }, key);
        var second = await ApproveAsync(inB, _fixture.WorkspaceB, guideB.Id, 1, new { confirmed = true }, key);

        // The idempotency scope carries the workspace, so the same key in another workspace is another
        // request rather than a replay of this one — and neither answer is the other's.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(guideB.VersionId, (await BodyOf(second)).GetProperty("versionId").GetGuid());

        Assert.Single(await ApprovalsAsync(_fixture.WorkspaceA));
        Assert.Single(await ApprovalsAsync(_fixture.WorkspaceB));
    }

    // ---- Harness ----

    /// <summary>A guide as created, and the id of its version 1.</summary>
    private sealed record SeededGuide(Guid Id, Guid VersionId);

    /// <summary>A source document as uploaded, with the token a replacement of it has to quote.</summary>
    private sealed record SeededDocument(Guid Id, string Token);

    private static string GuidesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static string SourcesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static string ApprovalOf(SeededWorkspace workspace, Guid guideId, int versionNumber) =>
        $"{GuidesIn(workspace)}/{guideId}/versions/{versionNumber}/approval";

    private static string ActivationOf(SeededWorkspace workspace, Guid guideId, int versionNumber) =>
        $"{GuidesIn(workspace)}/{guideId}/versions/{versionNumber}/activation";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Workspace A's second member is an Editor: the role this route is defined for.</summary>
    private async Task<GatewayClient> EditorOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ApproveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int versionNumber, object body,
        string? idempotencyKey = null) =>
        client.PostAsJsonAsync(
            ApprovalOf(workspace, guideId, versionNumber),
            body,
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

    /// <summary>Approves and insists it worked, for the arrange half of a test about something else.</summary>
    private async Task<JsonElement> Approved(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int versionNumber, object body)
    {
        var response = await ApproveAsync(client, workspace, guideId, versionNumber, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private Task<HttpResponseMessage> ActivateAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int versionNumber, object body) =>
        client.PostAsJsonAsync(
            ActivationOf(workspace, guideId, versionNumber),
            body,
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

    private async Task<SeededGuide> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededGuide(
            created.GetProperty("id").GetGuid(), created.GetProperty("version").GetProperty("id").GetGuid());
    }

    /// <summary>A guide whose version 1 says something: the approvable starting point.</summary>
    private Task<SeededGuide> GuideAsync(
        GatewayClient client, SeededWorkspace workspace, string displayName = "House voice") =>
        CreateGuideAsync(client, workspace, new
        {
            displayName,
            questionnaire = new { voice = "Warm, direct, never fussy." },
        });

    /// <summary>Adds a later version that says something, as an edit will. Not approved.</summary>
    private Task<Guid> VersionAsync(SeededWorkspace workspace, Guid guideId, int number, Guid parentId) =>
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
                Body = $"Warm, revision {number}.",
            });

            db.BrandStyleGuideVersions.Add(version);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return version.Id;
        });

    private Task ArchiveGuideAsync(SeededWorkspace workspace, Guid guideId) =>
        InScopeAsync(workspace, async db =>
        {
            var guide = await db.BrandStyleGuides.SingleAsync(
                candidate => candidate.Id == guideId, TestContext.Current.CancellationToken);
            guide.Status = BrandStyleGuideStatus.Archived;
            guide.ArchivedAt = Moment;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

    /// <summary>
    /// That neither workspace holds an approval or an audit entry yet.
    /// </summary>
    /// <remarks>
    /// Asserted between cross-workspace attempts rather than only at the end of one. A leaked write would be
    /// an approval of a version that is legitimately approved later in the same test, so by the end it would
    /// look like the right row — and the legitimate request would report <c>alreadyApproved</c> rather than
    /// failing.
    /// </remarks>
    private async Task NothingWrittenAsync()
    {
        foreach (var workspace in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
        {
            Assert.Empty(await ApprovalsAsync(workspace));
            Assert.Empty(await AuditEntriesAsync(workspace));
        }
    }

    private Task<List<BrandStyleGuideApproval>> ApprovalsAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideApprovals
            .AsNoTracking()
            .OrderBy(approval => approval.ApprovedAt)
            .ToListAsync(TestContext.Current.CancellationToken));

    private Task<int> DefaultCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideDefaults.CountAsync(TestContext.Current.CancellationToken));

    private Task<BrandStyleGuideDefault?> DefaultAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideDefaults
            .AsNoTracking()
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken));

    /// <summary>The workspace's approval entries, oldest first.</summary>
    private Task<List<AuditLog>> AuditEntriesAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.Action == BrandAuditActions.StyleGuideVersionApproved)
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken));

    /// <summary>An audit entry shaped like the one Business writes, for a DataLayer call made directly.</summary>
    private static AuditEntry AuditOf(Guid guideId) => new(
        "test-user",
        BrandAuditActions.StyleGuideVersionApproved,
        BrandAuditActions.StyleGuideResourceType,
        guideId.ToString("D"),
        Guid.NewGuid(),
        "Approved a version of this brand style guide.");

    private async Task<SeededDocument> UploadAsync(GatewayClient client, SeededWorkspace workspace, string fileName)
    {
        var response = await client.PostAsync(
            SourcesIn(workspace), FileForm(fileName), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededDocument(
            created.GetProperty("id").GetGuid(), created.GetProperty("concurrencyToken").GetString()!);
    }

    /// <summary>Replaces the document's file, which moves it to version 2 and supersedes every citation of 1.</summary>
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

    private static MultipartFormDataContent FileForm(
        string fileName, string contents = "house style", bool metadata = true)
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

    /// <summary>
    /// Runs one DataLayer call in its own request scope, for the write race an HTTP request cannot stage
    /// deterministically.
    /// </summary>
    private async Task<T> WithDataLayerAsync<T>(
        SeededWorkspace workspace, Func<IBrandStyleGuideDataLayer, CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(
            scope.ServiceProvider.GetRequiredService<IBrandStyleGuideDataLayer>(),
            scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
